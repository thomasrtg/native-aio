// Bande-annonce en plein écran, depuis la page d'un titre (comme pages/trailer.tsx de la
// version web, avec les demandes de l'auteur) :
// - image entière, bandes noires comprises : aucun recadrage ;
// - H.264 en 1080p, le plus haut que le lecteur de la TV accepte (AV1 et VP9 refusés) ;
// - écran de chargement du lecteur (fond du titre assombri, logo, traînée blanche) ;
// - barre simple, montrée à chaque appui et cachée après 5 s de lecture : titre, nom de la
//   bande-annonce, temps, avancement. OK : pause / lecture ; Gauche, Droite : 10 s ;
// - à la fin, retour à la page du titre.
// Un seul lecteur de bande-annonce à la fois (CardTrailer.Claim). Appels au lecteur hors du
// fil d'interface, les uns après les autres.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tizen.Multimedia;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;
using Timer = Tizen.NUI.Timer;

namespace NativeAIO.Screens
{
    class TrailerScreen : Screen
    {
        const int Margin = 60, BarY = 980, BarW = 1920 - 2 * Margin, HideAfter = 5000, SeekDelay = 700, Step = 10000;
        const int LogoW = 760, LogoH = 340, LogoMidY = 470, TrackW = 220, StreakPeriod = 2200, Fade = 400;
        const int ShownAfterMs = 300;

        readonly Item item;
        readonly MediaUrl trailer;
        readonly SynchronizationContext ui = SynchronizationContext.Current;
        Guarded guard;
        View root, cover, logoBox, controls, barPlayed;
        TextLabel title, sub, times, timesTotal, message;
        int timesLength = -1; // longueur du temps écoulé à la dernière mesure de sa largeur
        ImageView stateIcon;
        Streak streak;
        Animation outAnim, controlsAnim; // gardées dans des champs
        Timer poll, hideTimer, seekTimer; // gardés dans des champs, sinon ils s'arrêtent

        Player player;
        TaskCompletionSource<bool> done;
        Task ops = Task.CompletedTask;
        bool started, shown, playing, seeking, polling, loaderUp = true, failed;
        int positionMs, durationMs, seekTarget = -1;
        // Manifestes proposés, dans l'ordre, et celui en cours d'essai.
        readonly List<Trailers.Choice> choices = new List<Trailers.Choice>();
        int choice = -1;
        string kind, tried = "";

        // Mesures pour le panneau de la touche rouge.
        string tResolve = "-", tPrepare = "-", tFirst = "-", lastError, videoSize = "-", playedKind = "-";
        long tOpen;

        public TrailerScreen(Item item, MediaUrl trailer)
        {
            this.item = item;
            this.trailer = trailer;
        }

        string Icon(string name) => Tizen.Applications.Application.Current.DirectoryInfo.Resource + "icons/" + name + ".png";

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080) };
            guard = new Guarded(root);
            // La vidéo passe sous la fenêtre : rien n'est dessiné là où elle se voit.
            Window.Instance.BackgroundColor = Color.Transparent;
            BuildLoader();
            BuildControls();
            message = new TextLabel
            {
                Position = new Position(260, 470),
                Size = new Size(1400, 140),
                PointSize = Theme.SizeHeading,
                MultiLine = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextColor = Color.White,
            };
            message.Hide();
            root.Add(message);
            tOpen = Probe.Now;
            Start();
            return root;
        }

        // Écran de chargement repris du lecteur : fond du titre assombri, logo, traînée.
        void BuildLoader()
        {
            cover = new View { Size = new Size(1920, 1080), BackgroundColor = Color.Black };
            root.Add(cover);
            string back = Library.Backdrop(item, 1920) ?? Library.Landscape(item, 1920);
            if (back != null)
                ImageCache.Request(back, () => Closed ? int.MaxValue : 0, path => guard.Run("Backdrop", () =>
                {
                    if (Closed || path == null) return;
                    View v = Pictures.Cover(path, 1920, 1080, out _);
                    if (v == null) return;
                    v.Opacity = 0.35f;
                    cover.Add(v);
                    v.LowerToBottom();
                }));
            logoBox = new View { Size = new Size(1920, 1080), PivotPoint = PivotPoint.Center };
            cover.Add(logoBox);
            string logo = Library.Logo(item, LogoW);
            if (logo == null) LoaderTitle();
            else
                ImageCache.Request(logo, () => Closed ? int.MaxValue : 0, path => guard.Run("Logo", () =>
                {
                    if (Closed) return;
                    Pictures.ImageSize(path ?? "", out int iw, out int ih);
                    if (path == null || iw <= 0 || ih <= 0) { LoaderTitle(); return; }
                    float k = Math.Min(LogoW / (float)iw, LogoH / (float)ih);
                    int fw = Math.Max(1, (int)(iw * k)), fh = Math.Max(1, (int)(ih * k));
                    ImageView v = Pictures.Decoded(path, fw, fh);
                    if (v == null) { LoaderTitle(); return; }
                    v.Position = new Position(960 - fw / 2, LogoMidY - fh / 2);
                    logoBox.Add(v);
                }));
            streak = new Streak(TrackW, 2, StreakPeriod);
            streak.View.Position = new Position(960 - TrackW / 2, LogoMidY + LogoH / 2 + 60);
            logoBox.Add(streak.View);
            streak.Play();
        }

        void LoaderTitle()
        {
            logoBox.Add(new TextLabel
            {
                Position = new Position(160, LogoMidY - 80),
                Size = new Size(1600, 160),
                PointSize = 48,
                MultiLine = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextColor = Color.White,
                Text = Library.Title(item),
            });
        }

        void HideLoader()
        {
            if (!loaderUp) return;
            loaderUp = false;
            streak.Stop();
            outAnim?.Stop();
            outAnim = new Animation(Fade);
            outAnim.AnimateTo(logoBox, "Scale", new Vector3(1.25f, 1.25f, 1f), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            outAnim.AnimateTo(logoBox, "Opacity", 0f);
            outAnim.AnimateTo(cover, "Opacity", 0f);
            outAnim.Finished += (s, e) => { if (!Closed && !loaderUp) cover.Hide(); };
            outAnim.Play();
        }

        void BuildControls()
        {
            controls = new View { Size = new Size(1920, 1080), Opacity = 0f };
            root.Add(controls);
            controls.Add(Gradient.Make(new Position(0, 0), new Size(1920, 220), false, t => 0.75f * (1 - t)));
            controls.Add(Gradient.Make(new Position(0, 1080 - 260), new Size(1920, 260), false, t => 0.85f * t));
            controls.Add(new ImageView(Icon("arrow_left")) { Position = new Position(Margin, 52), Size = new Size(40, 40) });
            // Comme le lecteur (module 14) : titre demi-gras, nom en gris clair, temps écoulé en
            // blanc demi-gras et « / durée » en gris, barre fine à l'accent éclairci du thème.
            title = new TextLabel { Position = new Position(Margin + 64, 40), Size = new Size(1700, 46), PointSize = Theme.SizeHeading, TextColor = Color.White, FontStyle = Theme.SemiBold() };
            sub = new TextLabel { Position = new Position(Margin + 64, 88), Size = new Size(1700, 36), PointSize = Theme.SizeBody, TextColor = Theme.Soft };
            stateIcon = new ImageView(Icon("pause")) { Position = new Position(Margin, BarY - 58), Size = new Size(30, 30), Color = Theme.White(0.85f) };
            times = new TextLabel { Position = new Position(Margin + 48, BarY - 62), Size = new Size(300, 40), PointSize = Theme.SizeBody, TextColor = Color.White, FontStyle = Theme.SemiBold(), VerticalAlignment = VerticalAlignment.Center };
            timesTotal = new TextLabel { Position = new Position(Margin + 128, BarY - 62), Size = new Size(600, 40), PointSize = Theme.SizeBody, TextColor = Theme.Grey, VerticalAlignment = VerticalAlignment.Center };
            controls.Add(title);
            controls.Add(sub);
            controls.Add(stateIcon);
            controls.Add(times);
            controls.Add(timesTotal);
            var track = new View { Position = new Position(Margin, BarY), Size = new Size(BarW, 4), CornerRadius = 2, BackgroundColor = Theme.White(0.2f) };
            barPlayed = new View { Size = new Size(0, 4), CornerRadius = 2, BackgroundColor = Theme.AccentText };
            track.Add(barPlayed);
            controls.Add(track);
        }

        public override void ApplyTexts()
        {
            title.Text = Library.Title(item);
            sub.Text = string.IsNullOrEmpty(trailer.Name) ? Lang.T("trailer.default_name") : trailer.Name;
            Note();
        }

        public override void OnShow() => Note();
        public override void OnHide() { Measure.Note = null; }

        public override void OnClose()
        {
            poll?.Stop();
            hideTimer?.Stop();
            seekTimer?.Stop();
            guard.Stop();
            Trailers.End();
            Release(player, done);
            player = null;
            Window.Instance.BackgroundColor = App.Background;
            Measure.Note = null;
        }

        public override void OnAppPause() => guard.Run("AppPause", () => { if (playing) TogglePause(); });

        // ---- Lecture ----

        async void Start()
        {
            string id = Trailers.YouTubeId(trailer.Url);
            if (id != null)
            {
                Trailers.Stream s = null;
                try { s = await Trailers.Resolve(id, true); }
                catch (Exception e) { lastError = e.Message; }
                if (Closed) return;
                if (s == null) { Fail(Lang.T("trailer.no_link")); return; }
                tResolve = $"{s.Ms} ms, {s.Client}";
                kind = s.Kind;
                if (s.Choices.Count > 0) choices.AddRange(s.Choices);
                else choices.Add(new Trailers.Choice { Address = s.Address, Kind = s.Kind });
            }
            else
            {
                kind = "lien direct";
                choices.Add(new Trailers.Choice { Address = trailer.Url, Kind = kind });
            }
            Note();
            await PlayNext();
        }

        // Essaie les choix suivants jusqu'à ce que l'un démarre.
        async Task PlayNext()
        {
            while (!Closed && ++choice < choices.Count)
            {
                Trailers.Choice c = choices[choice];
                tried += (tried == "" ? "" : ", ") + c.Kind;
                if (await Play(c.Address, c.Kind)) return;
                Probe.Log("trailer", $"{c.Kind} refusé par le lecteur");
            }
            if (!Closed && player == null) Fail(Lang.T("trailer.failed", lastError ?? ""));
        }

        // Vrai si la lecture a démarré.
        async Task<bool> Play(string address, string what)
        {
            await CardTrailer.Claim(out TaskCompletionSource<bool> mine);
            if (Closed) { mine.TrySetResult(true); return true; }
            Player p = null;
            long t0 = Probe.Now;
            try
            {
                p = new Player();
                p.Display = new Display(Window.Instance);
                p.DisplaySettings.Mode = PlayerDisplayMode.LetterBox;
                Player pp = p;
                p.ErrorOccurred += (s, e) => Post(() => { if (pp == player) PlayError("lecteur : " + e.Error); });
                p.PlaybackCompleted += (s, e) => Post(() => { if (pp == player && !Closed) Nav.Back(); });
                p.VideoStreamChanged += (s, e) => Post(() => { videoSize = $"{e.Size.Width}×{e.Size.Height}, {e.BitRate / 1000} kb/s"; Note(); });
                try { p.UserAgent = Trailers.UA; } catch (Exception) { }
                try { p.BufferingTime = new PlayerBufferingTime(2000, 4000); } catch (Exception) { }
                p.SetSource(new MediaUriSource(address));
                Trailers.Begin();
                await On(() =>
                {
                    pp.PrepareAsync().GetAwaiter().GetResult();
                    try { durationMs = pp.StreamInfo.GetDuration(); } catch (Exception) { durationMs = 0; }
                    try { var v = pp.StreamInfo.GetVideoProperties(); videoSize = $"{v.Size.Width}×{v.Size.Height}"; } catch (Exception) { }
                });
                if (Closed) { Release(p, mine); return true; }
                tPrepare = (Probe.Now - t0) + " ms";
                await On(() => pp.Start());
            }
            catch (Exception e)
            {
                Release(p, mine);
                lastError = what + " : " + e.Message;
                Probe.Log("trailer", "plein écran, " + lastError);
                return false;
            }
            if (Closed) { Release(p, mine); return true; }
            player = p;
            done = mine;
            playedKind = what;
            started = playing = true;
            Probe.Log("trailer", $"plein écran : {what}, {videoSize}, prêt en {tPrepare}");
            poll = new Timer(250);
            poll.Tick += (s, e) => { Poll(); return true; };
            poll.Start();
            Note();
            return true;
        }

        // Erreur du lecteur : avant la première image, le choix suivant prend le relais.
        void PlayError(string text)
        {
            lastError = playedKind + " : " + text;
            Probe.Log("trailer", "plein écran, " + lastError);
            if (!shown && choice + 1 < choices.Count)
            {
                StopPlayer();
                _ = PlayNext();
                return;
            }
            Fail(Lang.T("trailer.failed", text));
        }

        void StopPlayer()
        {
            poll?.Stop();
            poll = null;
            started = playing = false;
            Release(player, done);
            player = null;
            done = null;
        }

        void Fail(string text)
        {
            failed = true;
            StopPlayer();
            streak.Stop();
            message.Text = text + "\n\n" + Lang.T("trailer.back");
            message.Show();
            message.RaiseToTop();
            Note();
        }

        // Position relue hors du fil d'interface : rythme des morceaux, première image, barre.
        void Poll()
        {
            Player p = player;
            if (polling || seeking || p == null) return;
            polling = true;
            On(() => p.GetPlayPosition()).ContinueWith(t => Post(() =>
            {
                polling = false;
                if (p != player || t.IsFaulted || seeking) return;
                positionMs = t.Result;
                Trailers.Played(positionMs);
                if (!shown && positionMs >= ShownAfterMs)
                {
                    shown = true;
                    tFirst = (Probe.Now - tOpen) + " ms";
                    HideLoader();
                    Note();
                }
                UpdateBar();
            }));
        }

        void UpdateBar()
        {
            int pos = seekTarget >= 0 ? seekTarget : positionMs;
            string elapsed = Library.Clock(pos * 10000L);
            times.Text = elapsed;
            // Largeur du temps écoulé mesurée seulement quand son nombre de caractères change.
            if (elapsed.Length != timesLength)
            {
                timesLength = elapsed.Length;
                timesTotal.PositionX = Margin + 48 + (int)times.NaturalSize2D.Width + 6;
            }
            timesTotal.Text = durationMs > 0 ? "/ " + Library.Clock(durationMs * 10000L) : "";
            barPlayed.Size = new Size(durationMs > 0 ? (int)(BarW * Math.Min(1.0, pos / (double)durationMs)) : 0, 4);
        }

        void TogglePause()
        {
            Player p = player;
            if (p == null) return;
            playing = !playing;
            stateIcon.ResourceUrl = Icon(playing ? "pause" : "play");
            if (playing) On(() => p.Start()); else On(() => p.Pause());
            ShowControls();
        }

        // Gauche / Droite cumulables : le saut part 0,7 s après le dernier appui.
        void SeekBy(int by)
        {
            if (player == null || !started) return;
            int from = seekTarget >= 0 ? seekTarget : positionMs;
            seekTarget = Math.Max(0, durationMs > 0 ? Math.Min(durationMs - 1000, from + by) : from + by);
            UpdateBar();
            ShowControls();
            seekTimer?.Stop();
            seekTimer = new Timer(SeekDelay);
            seekTimer.Tick += (s, e) => { guard.Run("Seek", DoSeek); return false; };
            seekTimer.Start();
        }

        async void DoSeek()
        {
            Player p = player;
            int to = seekTarget;
            if (p == null || to < 0) return;
            seeking = true;
            // Le relais retient les morceaux à plus de 25 s de la lecture : il doit savoir où on va.
            Trailers.Played(to);
            try { await On(() => p.SetPlayPositionAsync(to, false).GetAwaiter().GetResult()); }
            catch (Exception e) { Probe.Log("trailer", "saut refusé : " + e.Message); }
            if (Closed || p != player) return;
            positionMs = to;
            seeking = false;
            if (seekTarget == to) seekTarget = -1;
            UpdateBar();
        }

        void ShowControls()
        {
            if (!started) return;
            UpdateBar();
            if (controls.Opacity < 1f)
            {
                controlsAnim?.Stop();
                controlsAnim = new Animation(200);
                controlsAnim.AnimateTo(controls, "Opacity", 1f);
                controlsAnim.Play();
            }
            hideTimer?.Stop();
            hideTimer = new Timer(HideAfter);
            hideTimer.Tick += (s, e) =>
            {
                guard.Run("Hide", () =>
                {
                    if (!playing || seekTarget >= 0) { ShowControls(); return; }
                    controlsAnim?.Stop();
                    controlsAnim = new Animation(300);
                    controlsAnim.AnimateTo(controls, "Opacity", 0f);
                    controlsAnim.Play();
                });
                return false;
            };
            hideTimer.Start();
        }

        // ---- Touches ----

        public override bool OnKey(string key)
        {
            bool handled = true;
            guard.Run("Key " + key, () => handled = Keys(key));
            return handled;
        }

        bool Keys(string key)
        {
            if (failed)
            {
                if (key == "Return" || key == "KP_Enter") { Nav.Back(); return true; }
                return false;
            }
            switch (key)
            {
                case "Return":
                case "KP_Enter":
                case "XF86PlayBack":
                    if (started && controls.Opacity < 1f && playing) ShowControls();
                    else TogglePause();
                    return true;
                case "Left": SeekBy(-Step); return true;
                case "Right": SeekBy(Step); return true;
                case "Up":
                case "Down": ShowControls(); return true;
            }
            return false;
        }

        // ---- Lecteur hors du fil d'interface ----

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

        void Post(Action a) => ui.Post(_ => { if (!Closed) guard.Run("Trailer", a); }, null);

        void Note() => Measure.Note = () => Closed ? null : Lang.T("trailer.panel", kind ?? "-", tried == "" ? "-" : tried, playedKind, videoSize,
            tResolve, tPrepare, tFirst, Trailers.SegOk, Trailers.SegHeld, Trailers.SegFailed, Trailers.SegLast, lastError ?? Lang.T("home.none"));
    }
}
