// Sonde de développement : petit serveur HTTP sur la TV (127.0.0.1:8123), joint
// depuis le PC par « sdb forward tcp:8123 tcp:8123 » (voir tools/tv.mjs).
// Elle sert à lire les mesures, simuler des touches et capturer l'interface
// sans que l'auteur ait à envoyer de photo.
//
// - N'existe que si le symbole PROBE est défini : paquet de test construit avec
//   « dotnet build -c Release -p:Probe=true ». Sans lui (paquet publié), les appels à
//   Log, Boot et BootFrame disparaissent à la compilation, arguments compris.
// - N'écoute que sur la TV elle-même : seul un PC relié par sdb peut la joindre.
// - Ne renvoie jamais la clé de connexion ni les liens vidéo.
//
// Adresses :
//   /metrics          mesures en JSON (fluidité, mémoire, écran, lignes de l'écran)
//   /events?since=N   journal (une ligne par événement : numéro, ms, type, texte)
//   /key?k=Down       touche simulée, traitée comme une touche de la télécommande
//   /shot             capture de l'interface en JPEG (sans la vidéo)
//   /reset            remet à zéro les délais mesurés et le journal
//   /quit             ferme l'application
//   /open?id=ID       ouvre la page d'un titre (test de stabilité, tools/stress.mjs)
//   /play?id=ID&max=B lance la lecture depuis le début, premier lien d'au plus B bit/s
//   /home             revient à l'accueil (sans quitter, contrairement à Retour depuis l'accueil)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tizen.NUI;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Core
{
    static class Probe
    {
        public const int Port = 8123;
        const int MaxEvents = 3000;

        static readonly object gate = new object();
        static readonly Queue<string> events = new Queue<string>();
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static long seq;
        static bool on;

        // Sonde en marche (le panneau de mesures garde alors son rappel à chaque image).
        public static bool On => on;
        static SynchronizationContext ui;
        static HttpListener listener;
        static Capture capture; // gardée dans un champ pendant la capture

        // Millisecondes depuis le démarrage de l'application.
        public static long Now => clock.ElapsedMilliseconds;

        // À appeler dans OnCreate, sur le fil d'interface.
        public static void Start()
        {
#if PROBE
            ui = SynchronizationContext.Current;
            if (ui == null) return;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                listener.Start();
                on = true;
                Log("start", "sonde prête sur le port " + Port);
                string[] early;
                lock (gate) { early = bootEarly.ToArray(); bootEarly.Clear(); }
                foreach (string t in early) Log("boot", t);
                Task.Run(Loop);
            }
            catch (Exception)
            {
                // Sonde indisponible : l'application continue sans elle.
                listener = null;
            }
#endif
        }

        // ---- Repères du démarrage (module 16) ----
        // Une ligne « boot » par étape, une seule fois : millisecondes depuis le lancement du
        // processus par la TV (chargement de .NET compris), puis depuis le premier repère (Main).
        // Les repères pris avant la mise en route de la sonde sont versés au journal ensuite.
        static readonly HashSet<string> bootSeen = new HashSet<string>();
        static readonly List<string> bootEarly = new List<string>();
        static DateTime procStart;
        static bool procKnown, procTried;

        [Conditional("PROBE")]
        public static void Boot(string step)
        {
#if PROBE
            lock (gate) { if (!bootSeen.Add(step)) return; }
            if (!procTried)
            {
                procTried = true;
                try { procStart = Process.GetCurrentProcess().StartTime; procKnown = true; }
                catch (Exception) { }
            }
            string proc = procKnown ? ((long)(DateTime.Now - procStart).TotalMilliseconds).ToString() : "?";
            string text = $"{proc} ms depuis le processus, {Now} ms depuis Main : {step}";
            if (!on) { lock (gate) bootEarly.Add(text); return; }
            Log("boot", text);
#endif
        }

        // Repère à la prochaine image affichée par la TV.
        [Conditional("PROBE")]
        public static void BootFrame(string step)
        {
#if PROBE
            lock (gate) { if (bootSeen.Contains(step)) return; }
            try { Window.Instance.AddFramePresentedCallback(id => Boot(step), 0); }
            catch (Exception) { }
#endif
        }

        // Ajoute une ligne au journal. Sans effet si la sonde est éteinte.
        [Conditional("PROBE")]
        public static void Log(string kind, string text)
        {
            if (!on) return;
            text = (text ?? "").Replace("\r", "").Replace('\n', '|').Replace('\t', ' ');
            lock (gate)
            {
                seq++;
                events.Enqueue($"{seq}\t{Now}\t{kind}\t{text}");
                while (events.Count > MaxEvents) events.Dequeue();
            }
        }

        // Texte entre guillemets, échappé pour JSON.
        public static string Js(string s)
        {
            if (s == null) return "null";
            var b = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            return b.Append('"').ToString();
        }

        static async Task Loop()
        {
            while (listener != null && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { return; }
                _ = Task.Run(() => Serve(ctx));
            }
        }

        static async Task Serve(HttpListenerContext ctx)
        {
            HttpListenerResponse res = ctx.Response;
            try
            {
                var q = ctx.Request.QueryString;
                switch (ctx.Request.Url.AbsolutePath)
                {
                    case "/metrics":
                        await Send(res, await OnUi(() => App.Current.ProbeMetrics()), "application/json");
                        break;
                    case "/events":
                        long.TryParse(q["since"], out long since);
                        await Send(res, Events(since), "text/plain");
                        break;
                    case "/key":
                        string k = q["k"];
                        if (string.IsNullOrEmpty(k)) { res.StatusCode = 400; await Send(res, "paramètre k manquant"); break; }
                        await OnUi(() => { App.Current.ProbeKey(k); return ""; });
                        await Send(res, "ok");
                        break;
                    case "/shot":
                        await Shot(res);
                        break;
                    case "/api":
                        // Lecture seule sur le serveur de la session (ex. /MediaSegments/{id}) : le
                        // chemin doit commencer par « / », jamais une adresse complète (le jeton
                        // de connexion ne part que vers le serveur).
                        string path = q["path"];
                        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.StartsWith("//")) { res.StatusCode = 400; await Send(res, "paramètre path manquant ou invalide"); break; }
                        byte[] body = await Session.Client.GetBytes(path);
                        await Send(res, System.Text.Encoding.UTF8.GetString(body ?? new byte[0]), "application/json");
                        break;
                    case "/gc":
                        // Grand ménage forcé de la mémoire, pour distinguer une fuite de la mémoire
                        // pas encore récupérée (module 16). Les vues libérées par le ménage sont
                        // détruites ensuite par NUI sur le fil d'interface : mesurer après une pause.
                        long before = GC.GetTotalMemory(false);
                        await OnUi(() =>
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                            return "";
                        });
                        await Send(res, $"géré : {before / 1048576} Mo avant, {GC.GetTotalMemory(false) / 1048576} Mo après");
                        break;
                    case "/reset":
                        await OnUi(() => { App.Current.ProbeReset(); return ""; });
                        lock (gate) events.Clear();
                        await Send(res, "ok");
                        break;
                    case "/open":
                        string oid = q["id"];
                        if (string.IsNullOrEmpty(oid)) { res.StatusCode = 400; await Send(res, "paramètre id manquant"); break; }
                        await Send(res, await OnUiAsync(() => App.Current.ProbeOpen(oid)));
                        break;
                    case "/play":
                        string pid = q["id"];
                        if (string.IsNullOrEmpty(pid)) { res.StatusCode = 400; await Send(res, "paramètre id manquant"); break; }
                        if (!long.TryParse(q["max"], out long max) || max <= 0) max = 10000000;
                        await Send(res, await OnUiAsync(() => App.Current.ProbePlay(pid, max)));
                        break;
                    case "/home":
                        await Send(res, await OnUi(() => App.Current.ProbeHome()));
                        break;
                    case "/crash":
                        // Essai du rapport de plantage (module 18) : erreur .NET non interceptée.
                        await Send(res, "ok");
                        ui.Post(_ => throw new InvalidOperationException("essai du rapport de plantage (sonde)"), null);
                        break;
                    case "/quit":
                        await Send(res, "ok");
                        ui.Post(_ => App.Current.Exit(), null);
                        break;
                    default:
                        res.StatusCode = 404;
                        await Send(res, "/metrics /events?since=N /key?k=Down /shot /gc /reset /quit /open?id= /play?id=&max= /home /crash");
                        break;
                }
            }
            catch (Exception e)
            {
                try { res.StatusCode = 500; await Send(res, e.GetType().Name + " : " + e.Message); }
                catch (Exception) { }
            }
        }

        // Exécute une tâche sur le fil d'interface (ses suites y restent) et rend son résultat.
        static Task<string> OnUiAsync(Func<Task<string>> f)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(async _ =>
            {
                try { tcs.SetResult(await f()); }
                catch (Exception e) { tcs.SetException(e); }
            }, null);
            return tcs.Task;
        }

        // Exécute f sur le fil d'interface et rend son résultat.
        static Task<string> OnUi(Func<string> f)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(_ =>
            {
                try { tcs.SetResult(f()); }
                catch (Exception e) { tcs.SetException(e); }
            }, null);
            return tcs.Task;
        }

        static string Events(long since)
        {
            var b = new StringBuilder();
            lock (gate)
            {
                foreach (string line in events)
                {
                    int tab = line.IndexOf('\t');
                    if (long.Parse(line.Substring(0, tab)) > since) b.Append(line).Append('\n');
                }
            }
            return b.ToString();
        }

        // Capture de toute la fenêtre NUI (la vidéo, dessinée dessous, n'y est pas).
        static async Task Shot(HttpListenerResponse res)
        {
            string path = System.IO.Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.Data, "probe-shot.jpg");
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await OnUi(() =>
            {
                if (capture != null) throw new InvalidOperationException("capture déjà en cours");
                try { File.Delete(path); } catch (Exception) { }
                capture = new Capture();
                capture.Finished += (s, e) => done.TrySetResult(e.Success);
                capture.Start(Window.Instance.GetDefaultLayer(), new Size(1920, 1080), path, new Color(0f, 0f, 0f, 1f), 80);
                return "";
            });
            bool finished = await Task.WhenAny(done.Task, Task.Delay(5000)) == done.Task;
            await OnUi(() => { capture?.Dispose(); capture = null; return ""; });
            if (!finished || !done.Task.Result || !File.Exists(path))
            {
                res.StatusCode = 500;
                await Send(res, finished ? "capture échouée" : "capture sans réponse après 5 s");
                return;
            }
            byte[] data = File.ReadAllBytes(path);
            res.ContentType = "image/jpeg";
            res.ContentLength64 = data.Length;
            await res.OutputStream.WriteAsync(data, 0, data.Length);
            res.Close();
        }

        static async Task Send(HttpListenerResponse res, string text, string type = "text/plain")
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? "");
            res.ContentType = type + "; charset=utf-8";
            res.ContentLength64 = data.Length;
            await res.OutputStream.WriteAsync(data, 0, data.Length);
            res.Close();
        }
    }
}
