// Bande-annonce d'une carte ouverte (module 8), comme OpenTrailer de la version web
// (components/open-card.tsx) : chargée en pause, démarrée quand on le lui dit, montrée
// (Shown) une fois que la vidéo avance, finie (Gone) à sa fin ou sur une erreur. Une
// bande-annonce qui ne se prépare pas laisse la place à la suivante du titre.
// Le lecteur affiche dans un rectangle de l'écran (mode Roi), sous la fenêtre : c'est à
// l'écran de laisser ce rectangle transparent.
// Un seul lecteur à la fois dans l'application : chacun attend que le précédent soit fermé
// (Idle), le lecteur des vidéos aussi. Tous les appels au lecteur se font hors du fil
// d'interface (ils bloquent jusqu'à 0,5 s), les uns après les autres.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tizen.Multimedia;
using Tizen.NUI;
using NativeAIO.Core;
using Timer = Tizen.NUI.Timer;
using Rectangle = Tizen.Multimedia.Rectangle;

namespace NativeAIO.Ui
{
    class CardTrailer
    {
        // Fermé quand le dernier lecteur de bande-annonce est fermé.
        public static Task Idle => idle;
        static Task idle = Task.CompletedTask;

        // Tour d'un nouveau lecteur de bande-annonce (aussi celui du plein écran) : attendre
        // « before », puis terminer « mine » une fois le lecteur fermé.
        public static Task Claim(out TaskCompletionSource<bool> mine)
        {
            Task before = idle;
            mine = new TaskCompletionSource<bool>();
            idle = mine.Task;
            return before;
        }

        const int ShownAfterMs = 300; // position à partir de laquelle la vidéo avance vraiment

        public Action Shown;
        public Action<bool> Gone; // vrai : fin de la bande-annonce ; faux : erreur

        // Mesures pour le panneau.
        public int Loaded, Started, ShownCount, Failures;
        public string LastResolve = "-", LastPrepare = "-", LastFirst = "-", LastCrop = "-", LastError;

        readonly System.Threading.SynchronizationContext ui = System.Threading.SynchronizationContext.Current;
        readonly Action<string, Action> guard;
        Task ops = Task.CompletedTask;
        int gen;
        Player player;
        TaskCompletionSource<bool> done;
        bool prepared, want, started, shown, polling;
        Task<double> crop;
        int videoW, videoH;
        long tPlay;
        Timer poll; // gardé dans un champ, sinon il s'arrête

        public CardTrailer(Action<string, Action> guard) { this.guard = guard; }

        public bool Active => player != null || prepared || want;

        // Charge la première bande-annonce qui se prépare, sans la démarrer, dans le rectangle donné.
        public void Load(List<string> urls, int x, int y, int w, int h)
        {
            Stop();
            Loaded++;
            boxRatio = h > 0 ? w / (double)h : 16.0 / 9;
            Run(urls, new Rectangle(x, y, w, h), ++gen);
        }

        // Démarre dès que c'est prêt.
        public void Play()
        {
            want = true;
            tPlay = Probe.Now;
            if (prepared && !started) StartPlay();
        }

        // Arrêt immédiat ; le lecteur est fermé en arrière-plan.
        public void Stop()
        {
            gen++;
            prepared = want = started = shown = false;
            poll?.Stop();
            poll = null;
            if (player != null) Release(player, done);
            player = null;
            done = null;
            Trailers.End();
        }

        async void Run(List<string> urls, Rectangle rect, int g)
        {
            foreach (string url in urls)
            {
                string id = Trailers.YouTubeId(url), address;
                try
                {
                    if (id != null)
                    {
                        Trailers.Stream s = await Trailers.Resolve(id);
                        if (g != gen) return;
                        if (s == null) { Fail("YouTube : aucun lien pour " + id); continue; }
                        LastResolve = $"{s.Ms} ms, {s.Client}, {s.Kind}";
                        address = s.Address;
                        crop = Trailers.Crop(id);
                    }
                    else
                    {
                        // Autre vidéo que YouTube : lue telle quelle, comme le web.
                        address = url;
                        crop = null;
                    }
                }
                catch (Exception e) { if (g != gen) return; Fail("résolution : " + e.Message); continue; }
                if (!address.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                // Au tour de ce lecteur : le précédent est fermé avant.
                await Claim(out TaskCompletionSource<bool> mine);
                if (g != gen) { mine.TrySetResult(true); return; }
                if (await Prepare(address, id, rect, g, mine)) return;
            }
        }

        // Vrai si le lecteur est prêt, ou si la bande-annonce n'est plus voulue.
        async Task<bool> Prepare(string address, string id, Rectangle rect, int g, TaskCompletionSource<bool> mine)
        {
            Player p = null;
            long t0 = Probe.Now;
            try
            {
                p = new Player();
                p.Display = new Display(Window.Instance);
                p.DisplaySettings.Mode = PlayerDisplayMode.Roi;
                p.DisplaySettings.SetRoi(rect);
                Player pp = p;
                p.ErrorOccurred += (s, e) => Post(() => { if (pp == player) Finish(false, "lecteur : " + e.Error); });
                p.PlaybackCompleted += (s, e) => Post(() => { if (pp == player) Finish(true, null); });
                try { p.UserAgent = Trailers.UA; } catch (Exception) { }
                // Courte : la bande-annonce est petite et peut être quittée à tout moment.
                try { p.BufferingTime = new PlayerBufferingTime(1000, 3000); } catch (Exception) { }
                p.SetSource(new MediaUriSource(address));
                Trailers.Begin();
                await On(() =>
                {
                    pp.PrepareAsync().GetAwaiter().GetResult();
                    try { var v = pp.StreamInfo.GetVideoProperties(); videoW = v.Size.Width; videoH = v.Size.Height; }
                    catch (Exception) { videoW = videoH = 0; }
                });
            }
            catch (Exception e)
            {
                Release(p, mine);
                if (g != gen) return true;
                Fail("préparation : " + e.Message);
                return false;
            }
            if (g != gen) { Release(p, mine); return true; }
            player = p;
            done = mine;
            prepared = true;
            LastPrepare = $"{Probe.Now - t0} ms, {videoW}×{videoH}";
            Probe.Log("trailer", "prêt : " + LastPrepare);
            if (want) StartPlay();
            return true;
        }

        async void StartPlay()
        {
            started = true;
            Started++;
            Player p = player;
            int g = gen;
            // Bandes noires : 1,5 s d'attente au plus (déjà connues d'habitude).
            double c = 0;
            if (crop != null)
            {
                Task<double> cr = crop;
                if (await Task.WhenAny(cr, Task.Delay(1500)) == cr && !cr.IsFaulted) c = cr.Result;
                if (g != gen) return;
            }
            ScaleRectangle? roi = Roi(c);
            LastCrop = c < 0 ? "images illisibles" : roi == null ? "aucun" : $"{c * 100:0.0} % ({videoW}×{videoH})";
            try
            {
                await On(() =>
                {
                    if (roi != null) { try { p.SetVideoRoi(roi.Value); } catch (Exception e) { Probe.Log("trailer", "recadrage refusé : " + e.Message); } }
                    p.Start();
                });
            }
            catch (Exception e)
            {
                if (g == gen && p == player) Finish(false, "démarrage : " + e.Message);
                return;
            }
            if (g != gen || p != player) return;
            poll = new Timer(250);
            poll.Tick += (s, e) => { Poll(p, g); return true; };
            poll.Start();
        }

        // Format du rectangle donné : 16:9 pour une carte, 2,39:1 pour la bannière qui suit la carte.
        double boxRatio = 16.0 / 9;

        // Partie de l'image à montrer : sans les bandes noires pour une vidéo du format du
        // rectangle, et recadrée à ce format pour une vidéo d'un autre format (comme « cover »).
        ScaleRectangle? Roi(double c)
        {
            if (videoW <= 0 || videoH <= 0) return null;
            double v = videoW / (double)videoH, card = boxRatio;
            if (Math.Abs(v - card) < 0.05)
                return c > 0 ? new ScaleRectangle(c, c, 1 - 2 * c, 1 - 2 * c) : (ScaleRectangle?)null;
            if (v > card) { double k = (1 - card / v) / 2; return new ScaleRectangle(k, 0, 1 - 2 * k, 1); }
            double m = (1 - v / card) / 2;
            return new ScaleRectangle(0, m, 1, 1 - 2 * m);
        }

        // Position relue hors du fil d'interface : rythme des morceaux et première image.
        void Poll(Player p, int g)
        {
            if (polling) return;
            polling = true;
            On(() => p.GetPlayPosition()).ContinueWith(t => Post(() =>
            {
                polling = false;
                if (g != gen || p != player || t.IsFaulted) return;
                int pos = t.Result;
                Trailers.Played(pos);
                if (!shown && pos >= ShownAfterMs)
                {
                    shown = true;
                    ShownCount++;
                    LastFirst = (Probe.Now - tPlay) + " ms";
                    Probe.Log("trailer", "image après " + LastFirst);
                    if (poll != null) poll.Interval = 1000;
                    Shown?.Invoke();
                }
            }));
        }

        void Finish(bool ended, string error)
        {
            if (error != null) Fail(error);
            bool wasShown = shown;
            Stop();
            if (wasShown) Gone?.Invoke(ended);
        }

        void Fail(string text)
        {
            Failures++;
            LastError = text;
            Probe.Log("trailer", text);
        }

        Task On(Action a)
        {
            Task t = ops.ContinueWith(_ => a(), TaskScheduler.Default);
            ops = t;
            return t;
        }

        Task<T> On<T>(Func<T> a)
        {
            Task<T> t = ops.ContinueWith(_ => a(), TaskScheduler.Default);
            ops = t;
            return t;
        }

        void Release(Player p, TaskCompletionSource<bool> mine)
        {
            if (p == null) { mine?.TrySetResult(true); return; }
            On(() =>
            {
                try { p.Stop(); } catch (Exception) { }
                try { p.Unprepare(); } catch (Exception) { }
                try { p.Dispose(); } catch (Exception) { }
            }).ContinueWith(_ => mine?.TrySetResult(true), TaskScheduler.Default);
        }

        void Post(Action a) => ui.Post(_ => guard("Trailer", a), null);
    }
}
