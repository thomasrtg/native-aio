// Rapport de plantage (module 18). Journal à part, sans lien avec la sonde (il reste quand
// la sonde est retirée) :
// - les 20 dernières actions : touches, écrans (nom technique), arrière-plan, erreurs
//   interceptées (type seulement) ; jamais de titre, d'adresse ni de nom ;
// - un fichier « session » dans le dossier de l'application : état « open » pendant
//   l'utilisation, « pause » en arrière-plan (la TV peut alors fermer l'application sans
//   que ce soit un plantage), effacé à la fermeture normale. Récrit au plus une fois par
//   seconde, hors du traitement des touches ;
// - une erreur .NET qui ferme l'application est écrite tout de suite (type, message, début
//   de la pile). Un plantage de la TV elle-même (lecteur) tue le processus sans prévenir :
//   il ne reste que l'état « open » et les dernières actions.
// Au lancement suivant, un fichier resté « open » donne le rapport (Pending), proposé par
// Screens/ReportScreen.cs sous forme de QR code : un ticket GitHub prérempli, que la
// personne relit et envoie depuis son téléphone. Rien n'est envoyé par la TV.
// Rapport en anglais (dépôt public, et moins de place dans le QR code), nettoyé : liens,
// adresses IP, adresse du serveur, jeton et noms d'utilisateurs remplacés.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tizen.Applications;

namespace NativeAIO.Core
{
    static class Crash
    {
        const string Repo = "https://github.com/thomasrtg/native-aio";
        const int MaxActions = 20;
        const int MaxStack = 8;
        const int MaxUrl = 1600; // octets : QR code de version ~28 au plus, lisible sur la TV (à vérifier)
        const string AskKey = "crash.ask";

        static readonly object gate = new object();
        static readonly List<string> actions = new List<string>();
        static readonly List<string> error = new List<string>();
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static string path, state = "open", screen = "";
        static string lastAction;
        static int lastCount = 1;
        static bool flushing;

        // Rapport de la session précédente, si elle ne s'est pas fermée normalement (null sinon).
        public static Report Pending { get; private set; }

        // Réglage « Proposer de signaler les plantages » (À propos), propre à la TV, actif par défaut.
        public static bool Ask
        {
            get { try { return !Preference.Contains(AskKey) || Preference.Get<int>(AskKey) != 0; } catch (Exception) { return true; } }
            set => Store.Set(AskKey, value ? 1 : 0);
        }

        // À appeler au tout début (Main) : erreurs .NET qui ferment l'application.
        public static void Hook()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Fatal(e.ExceptionObject as Exception);
        }

        // À appeler dans OnCreate : lit la session précédente, puis ouvre la nouvelle.
        public static void Init()
        {
            try
            {
                path = Path.Combine(Application.Current.DirectoryInfo.Data, "session.txt");
                if (File.Exists(path)) Pending = Read(File.ReadAllLines(path));
            }
            catch (Exception) { Pending = null; }
            Write();
        }

        // ---- Journal ----

        public static void Note(string action)
        {
            lock (gate)
            {
                string at = "[" + (clock.ElapsedMilliseconds / 1000) + "s] ";
                // Même action répétée (touche) : une seule ligne « xN », pour garder plus d'histoire.
                if (actions.Count > 0 && action == lastAction)
                {
                    lastCount++;
                    actions[actions.Count - 1] = at + action + " x" + lastCount;
                }
                else
                {
                    lastAction = action;
                    lastCount = 1;
                    actions.Add(at + action);
                    if (actions.Count > MaxActions) actions.RemoveAt(0);
                }
            }
            Later();
        }

        public static void Screen(Screen top)
        {
            string name = top?.GetType().Name ?? "";
            if (name == screen) return;
            screen = name;
            Note("screen " + name);
        }

        public static void Pause() { state = "pause"; Note("app pause"); Write(); }

        // La TV appelle aussi OnResume au lancement : noté seulement après une vraie pause.
        public static void Resume()
        {
            if (state != "pause") return;
            state = "open";
            Note("app resume");
            Write();
        }

        // Fermeture normale : plus rien à signaler.
        public static void End()
        {
            lock (gate)
            {
                state = "closed";
                try { if (path != null && File.Exists(path)) File.Delete(path); } catch (Exception) { }
            }
        }

        static void Fatal(Exception e)
        {
            if (e == null) return;
            lock (gate)
            {
                error.Clear();
                // Nettoyé ici : la session (adresse, jeton, noms) n'est connue que dans ce processus.
                error.Add(Clean(e.GetType().Name + ": " + e.Message));
                for (Exception inner = e.InnerException; inner != null; inner = inner.InnerException)
                    error.Add(Clean("-> " + inner.GetType().Name + ": " + inner.Message));
                string[] stack = (e.StackTrace ?? "").Split('\n');
                for (int i = 0, n = 0; i < stack.Length && n < MaxStack; i++)
                {
                    string line = stack[i].Trim();
                    if (line.Length == 0) continue;
                    error.Add(Clean(line));
                    n++;
                }
            }
            Write(); // tout de suite : le processus s'arrête après
        }

        static void Later()
        {
            lock (gate)
            {
                if (flushing) return;
                flushing = true;
            }
            Task.Delay(1000).ContinueWith(_ =>
            {
                lock (gate) flushing = false;
                Write();
            });
        }

        // Format : « S état », « C écran », « E ligne d'erreur », « A action » (une par ligne).
        static void Write()
        {
            lock (gate)
            {
                if (path == null || state == "closed") return;
                var b = new StringBuilder();
                b.Append("S ").Append(state).Append('\n');
                b.Append("C ").Append(screen).Append('\n');
                foreach (string l in error) b.Append("E ").Append(OneLine(l)).Append('\n');
                foreach (string l in actions) b.Append("A ").Append(OneLine(l)).Append('\n');
                try
                {
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, b.ToString());
                    File.Move(tmp, path, true);
                }
                catch (Exception) { }
            }
        }

        static string OneLine(string s) => (s ?? "").Replace("\r", "").Replace('\n', ' ');

        static Report Read(string[] lines)
        {
            var r = new Report { Crashed = true };
            string st = "";
            foreach (string l in lines)
            {
                if (l.Length < 2) continue;
                string v = l.Substring(2);
                switch (l[0])
                {
                    case 'S': st = v; break;
                    case 'C': r.Screen = v; break;
                    case 'E': r.Error.Add(v); break;
                    case 'A': r.Actions.Add(v); break;
                }
            }
            return st == "open" ? r : null; // « pause » : fermée par la TV en arrière-plan, normal
        }

        // Rapport demandé depuis À propos : état en cours, sans erreur.
        public static Report Manual()
        {
            var r = new Report { Crashed = false, Screen = screen };
            lock (gate) r.Actions.AddRange(actions);
            return r;
        }

        // ---- Rapport ----

        public class Report
        {
            public bool Crashed;
            public string Screen = "";
            public readonly List<string> Error = new List<string>();
            public readonly List<string> Actions = new List<string>();

            // Adresse du ticket GitHub prérempli (titre et texte), ramenée sous MaxUrl en
            // retirant d'abord les actions les plus anciennes, puis la fin de la pile.
            public string IssueUrl()
            {
                int nActions = Actions.Count, nError = Error.Count;
                for (; ; )
                {
                    string url = Repo + "/issues/new?title=" + Encode(Title()) + "&body=" + Encode(Body(nActions, nError));
                    if (Encoding.UTF8.GetByteCount(url) <= MaxUrl) return url;
                    if (nActions > 5) nActions--;
                    else if (nError > 2) nError--;
                    else if (nActions > 0) nActions--;
                    else if (nError > 1) nError--;
                    else return url.Substring(0, MaxUrl); // ne devrait pas arriver (message très long)
                }
            }

            string Title()
            {
                if (!Crashed) return "Problem report";
                string where = Screen.Length > 0 ? " on " + Screen : "";
                if (Error.Count == 0) return "Unexpected shutdown" + where;
                string first = Error[0];
                int colon = first.IndexOf(':');
                return "Crash: " + (colon > 0 ? first.Substring(0, colon) : first) + where;
            }

            string Body(int nActions, int nError)
            {
                var b = new StringBuilder();
                b.Append("**Native AIO** ").Append(App.AppVersion).Append('\n');
                b.Append("**TV**: ").Append(Tv()).Append('\n');
                b.Append("**Screen**: ").Append(Screen.Length > 0 ? Screen : "?").Append('\n');
                if (Crashed)
                {
                    b.Append("**What happened**: ").Append(Error.Count > 0
                        ? "the app closed on a .NET error."
                        : "the app did not close normally (no .NET error: the TV player or system may have crashed).").Append('\n');
                }
                if (Crashed && nError > 0)
                {
                    b.Append("\n**Error**\n```\n");
                    for (int i = 0; i < nError; i++) b.Append(Error[i]).Append('\n');
                    b.Append("```\n");
                }
                if (nActions > 0)
                {
                    b.Append("\n**Last actions** (oldest first)\n```\n");
                    for (int i = Actions.Count - nActions; i < Actions.Count; i++) b.Append(Actions[i]).Append('\n');
                    b.Append("```\n");
                }
                b.Append(Crashed ? "\n**What were you doing?** (optional)\n\n" : "\n**Describe the problem:**\n\n");
                return b.ToString();
            }
        }

        // Modèle de la TV et version de Tizen, lus une fois.
        static string tv;
        static string Tv()
        {
            if (tv != null) return tv;
            string model = null, platform = null;
            try { Tizen.System.Information.TryGetValue("http://tizen.org/system/model_name", out model); } catch (Exception) { }
            try { Tizen.System.Information.TryGetValue("http://tizen.org/feature/platform.version", out platform); } catch (Exception) { }
            return tv = (string.IsNullOrEmpty(model) ? "?" : model) + ", Tizen " + (string.IsNullOrEmpty(platform) ? "?" : platform);
        }

        // Retire ce qui pourrait être personnel d'une ligne d'erreur.
        static readonly Regex Url = new Regex(@"[a-zA-Z][a-zA-Z0-9+.-]*://\S+");
        static readonly Regex Ip = new Regex(@"\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b");
        static readonly Regex FilePath = new Regex(@" in \S*[\\/]");

        static string Clean(string s)
        {
            s = Url.Replace(s, "<url>");
            s = Ip.Replace(s, "<ip>");
            s = FilePath.Replace(s, " in "); // chemins des fichiers sources du PC : nom seul
            foreach (string secret in new[] { Session.Base, Session.Token, Session.ServerName, Session.UserName, Session.UserId })
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 3) s = s.Replace(secret, "<hidden>");
            return s;
        }

        // Encodage d'une valeur de l'adresse : espaces en « + » (plus court que %20), lettres,
        // chiffres et signes courants des piles laissés tels quels, le reste en %XX (UTF-8).
        static string Encode(string s)
        {
            var b = new StringBuilder();
            foreach (byte c in Encoding.UTF8.GetBytes(s))
            {
                if (c == ' ') b.Append('+');
                else if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || "-._~()*!:,/".IndexOf((char)c) >= 0) b.Append((char)c);
                else b.Append('%').Append(c.ToString("X2"));
            }
            return b.ToString();
        }
    }
}
