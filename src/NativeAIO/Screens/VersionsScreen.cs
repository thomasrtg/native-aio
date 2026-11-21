// Choix du lien : les versions d'un film ou d'un épisode, demandées au serveur
// (/Items/{id}/PlaybackInfo), présentées comme la version web (components/version-picker.tsx,
// module 14, demande de l'auteur) : panneau centré, image du titre fondue en haut, titre, nombre de
// versions, « Rechercher à nouveau », choix « Reprendre à / Depuis le début », puis une carte par
// version (rond lecture, nom, description) et les messages des addons : liste commune avec le
// tiroir du lecteur (Ui/VersionList.cs).
// Fond du panneau comme les écrans : couleur de fond du thème, ou la partie de l'arrière-plan
// d'après l'image qui se trouve derrière (l'auteur, 2026-11-18). L'en-tête est posé par-dessus la
// liste : les cartes glissent dessous.
// Choisir une version ouvre le lecteur à la place du panneau (Retour ramène à la page du titre).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;
using Path = System.IO.Path;

namespace NativeAIO.Screens
{
    class VersionsScreen : Screen
    {
        // Panneau : 55 % de la largeur et 85 % de la hauteur, comme le web (max-w-4xl, 85vh).
        const int PW = 1056, PH = 918, PX = (1920 - PW) / 2, PY = (1080 - PH) / 2, Pad = 48;
        const int IW = PW - 2 * Pad, Left = PX + Pad;
        // Bouton rond « Rechercher à nouveau ».
        const int Circle = Theme.PillHeight, IconSize = 26;
        // Capsule à deux choix : 4 px autour des deux pastilles, 4 px entre elles (comme le web).
        const int CapH = Theme.PillHeight + 8, Half = (IW - 12) / 2;

        enum Zone { List, Toggle, Refresh }

        readonly Item target, show;
        readonly long resumeTicks;
        long startTicks;
        View root, art, refresh, capsule, capRing;
        ImageView refreshIcon;
        Animation spin; // icône qui tourne pendant le chargement, gardée dans un champ
        View[] halves;
        TextLabel heading, sub, status;
        TextLabel[] halfTexts;
        VersionList vlist;
        Source[] sources = new Source[0]; // réponse entière (messages compris), passée au lecteur
        int gen, listTop, listH;
        bool loading, failed, lastRefresh, canRefresh;
        Zone zone = Zone.List;
        Guarded guard;
        readonly Stopwatch clock = new Stopwatch();
        string tList = "-", playSession;

        // La page du dessous reste visible sous le voile, même avec l'arrière-plan d'après l'image
        // (comme le web).
        public override bool Overlay => true;
        public override bool ArtBackdrop => true;
        public override bool FadeOut => true;

        // straight : à la reprise, la dernière version lue de ce titre est relancée sans attendre
        // un choix (LastVersions, comme le web) ; sinon la première, si le réglage « Lancer
        // automatiquement la première version » est actif. Jamais quand le lecteur ouvre la liste
        // faute d'une version qui continue la précédente, ni pour « Choisir une version ».
        readonly bool straight, first;
        readonly string last;
        bool reused;
        // Lancement direct (dernière version retenue, ou la première si le réglage le demande ;
        // demandes de l'auteur, 2026-11-21) : la liste ne se
        // voit pas. L'écran de chargement du lecteur (image du titre assombrie, logo, barre) est
        // affiché tout de suite, opaque, et le lecteur le reprend sans fondu (Seamless) ; le
        // panneau n'apparaît (en fondu) que si cette version n'est plus là ou si la demande
        // échoue. Pendant l'attente, seule Retour agit.
        bool waiting;
        View outer, waitCover;
        Streak waitStreak;
        Animation reveal; // gardée dans un champ, sinon elle s'arrête

        // now : « Lire immédiatement » du menu d'une carte (held du web) : la première version,
        // sans liste, même sans le réglage.
        public VersionsScreen(Item target, long startTicks, Item show, bool straight = true, bool now = false)
        {
            this.target = target;
            this.startTicks = resumeTicks = startTicks;
            this.show = show ?? target;
            last = straight && startTicks > 0 ? LastVersions.Get(target.Id) : null;
            first = now || (straight && Prefs.AutoPlayFirst);
            this.straight = first || last != null;
        }

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.Veil };
            guard = new Guarded(root);
            ArtLayer.Show(show);
            root.Add(ArtLayer.Piece(PX, PY, PW, PH, Theme.RadiusLarge));

            // Hauteur de l'en-tête : titre, précision, ligne du nombre de versions, capsule.
            int countY = Pad + 76, headH = countY + Circle + Theme.Gap;
            if (resumeTicks > 0) headH += CapH + Theme.Gap;
            listTop = PY + headH + Theme.Gap;
            listH = PY + PH - Pad - listTop;

            // Liste sous l'en-tête, curseur de défilement dans la marge de droite du panneau.
            vlist = new VersionList(root, Left, IW, listTop, listH, PY, PY + PH, PX + PW - 12, guard, () => Closed);

            // En-tête posé par-dessus la liste : fond du panneau, image du titre à 25 % fondue
            // dans le fond (de 30 à 100 %, comme le web), trait de séparation (blanc à 5 % du web).
            // Il s'arrête au-dessus du contour de la première carte grossie (débord d'environ 12 px) ;
            // la carte d'au-dessus finit 16 px plus haut, encore cachée.
            root.Add(ArtLayer.Piece(PX, PY, PW, listTop - 14 - PY, Theme.RadiusLarge));
            art = new View { Position = new Position(PX, PY), Size = new Size(PW, headH) };
            root.Add(art);
            Func<float, float> fade = t => t < 0.5f ? 0.3f + 0.8f * t : 0.7f + 0.6f * (t - 0.5f);
            View veil = ArtLayer.Piece(PX, PY, PW, headH, Theme.RadiusLarge, HeadMask(headH, fade));
            root.Add(veil ?? Gradient.Make(new Position(PX, PY), new Size(PW, headH), false, fade, Theme.Background, Theme.RadiusLarge));
            root.Add(new View { Position = new Position(PX, PY + headH), Size = new Size(PW, 1), BackgroundColor = Theme.Faint });
            string back = Library.Backdrop(show, PW) ?? Library.Landscape(show, PW);
            if (back != null)
                ImageCache.Request(back, () => Closed ? int.MaxValue : 0, path => guard.Run("Backdrop", () =>
                {
                    if (Closed || path == null) return;
                    View v = Pictures.Cover(path, PW, headH, out ImageView img);
                    if (v == null) return;
                    img.Rounded(Theme.RadiusLarge);
                    v.Opacity = 0.25f;
                    art.Add(v);
                }));

            heading = new TextLabel { Position = new Position(Left, PY + Pad), Size = new Size(IW, 36), PointSize = Theme.SizeHeading, TextColor = Color.White, FontStyle = Theme.SemiBold(), Ellipsis = true };
            sub = new TextLabel { Position = new Position(Left, PY + Pad + 40), Size = new Size(IW, 24), PointSize = Theme.SizeInfo, TextColor = Theme.Grey, Ellipsis = true };
            status = new TextLabel { Position = new Position(Left, PY + countY), Size = new Size(IW - Circle - Theme.Gap, Circle), PointSize = Theme.SizeInfo, TextColor = Theme.Grey, VerticalAlignment = VerticalAlignment.Center };
            refresh = new View { Position = new Position(Left + IW - Circle, PY + countY), Size = new Size(Circle, Circle), BackgroundColor = Theme.Surface }.Rounded(Circle / 2f);
            refreshIcon = new ImageView { ResourceUrl = Theme.IconPath("refresh"), Size = new Size(IconSize, IconSize), Position = new Position((Circle - IconSize) / 2, (Circle - IconSize) / 2) };
            refresh.Add(refreshIcon);
            refresh.Hide();
            root.Add(heading);
            root.Add(sub);
            root.Add(status);
            root.Add(refresh);
            if (resumeTicks > 0)
            {
                int y = PY + countY + Circle + Theme.Gap;
                capRing = Theme.HollowRing(IW, CapH, CapH / 2);
                capRing.Position = new Position(Left - Theme.RingOut, y - Theme.RingOut);
                capRing.Hide();
                capsule = new View { Position = new Position(Left, y), Size = new Size(IW, CapH), BackgroundColor = Theme.Capsule }.Rounded(CapH / 2f);
                halves = new View[2];
                halfTexts = new TextLabel[2];
                for (int i = 0; i < 2; i++)
                {
                    halves[i] = new View { Position = new Position(4 + i * (Half + 4), 4), Size = new Size(Half, Theme.PillHeight) }.Rounded(Theme.PillHeight / 2f);
                    halfTexts[i] = new TextLabel { Size = new Size(Half, Theme.PillHeight), PointSize = Theme.SizeBody, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextColor = Color.White };
                    halves[i].Add(halfTexts[i]);
                    capsule.Add(halves[i]);
                }
                root.Add(capsule);
                root.Add(capRing); // contour au centre transparent, par-dessus la capsule
                PaintToggle();
            }
            AskRefreshable();
            View result = root;
            if (straight) // lancement direct attendu : dernière version retenue, ou la première
            {
                waiting = true;
                outer = new View { Size = new Size(1920, 1080) };
                root.Hide();
                outer.Add(root);
                BuildWait();
                result = outer;
            }
            return result; // liste demandée à la première apparition (OnShow)
        }

        // Copie de l'écran de chargement du lecteur (PlayerScreen.BuildLoader) : mêmes images,
        // mêmes places, pour que le passage au lecteur ne se voie pas.
        void BuildWait()
        {
            waitCover = new View { Size = new Size(1920, 1080), BackgroundColor = Color.Black };
            outer.Add(waitCover);
            string back = Library.Backdrop(show, 1920) ?? Library.Landscape(target, 1920);
            if (back != null)
                ImageCache.Request(back, () => Closed ? int.MaxValue : 0, path => guard.Run("WaitBackdrop", () =>
                {
                    if (Closed || path == null || !waiting) return;
                    View v = Pictures.Cover(path, 1920, 1080, out _);
                    if (v == null) return;
                    v.Opacity = 0.35f;
                    waitCover.Add(v);
                    v.LowerToBottom();
                }));
            const int W = PlayerScreen.LogoW, H = PlayerScreen.LogoH, MidY = PlayerScreen.LogoMidY;
            string logo = Library.Logo(target, W) ?? (show != target ? Library.Logo(show, W) : null);
            void Title() => waitCover.Add(new TextLabel
            {
                Position = new Position(160, MidY - 80), Size = new Size(1600, 160), PointSize = 48, MultiLine = true,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextColor = Color.White,
                Text = target.Type == "Episode" ? target.SeriesName ?? show.Name ?? "" : target.Name ?? "",
            });
            if (logo == null) Title();
            else
                ImageCache.Request(logo, () => Closed ? int.MaxValue : 0, path => guard.Run("WaitLogo", () =>
                {
                    if (Closed || !waiting) return;
                    ImageView v = PlayerScreen.Fitted(path, W, H, out int fw, out int fh);
                    if (v == null) { Title(); return; }
                    v.Position = new Position(960 - fw / 2, MidY - fh / 2);
                    waitCover.Add(v);
                }));
            waitStreak = new Streak(PlayerScreen.TrackW, 2, PlayerScreen.StreakPeriod);
            waitStreak.View.Position = new Position(960 - PlayerScreen.TrackW / 2, MidY + H / 2 + 60);
            waitCover.Add(waitStreak.View);
            waitStreak.Play();
        }

        // Dernière version introuvable ou échec : l'attente s'efface, le panneau apparaît en
        // fondu (0,2 s) sur l'écran de dessous.
        void Reveal()
        {
            if (!waiting) return;
            waiting = false;
            waitStreak.Stop();
            root.Opacity = 0f;
            root.Show();
            reveal = new Animation(200);
            reveal.AnimateTo(root, "Opacity", 1f);
            reveal.AnimateTo(waitCover, "Opacity", 0f);
            reveal.Finished += (s, e) => waitCover.Hide();
            reveal.Play();
        }

        // Masque de l'en-tête avec l'arrière-plan d'après l'image, en coordonnées de l'écran (le
        // masque s'applique à toute l'image) : opacité alpha(t) sur la hauteur de l'en-tête.
        static readonly Dictionary<int, string> masks = new Dictionary<int, string>();

        static string HeadMask(int headH, Func<float, float> alpha)
        {
            if (!ArtWash.On) return null;
            if (masks.TryGetValue(headH, out string p)) return p;
            const int w = 2, h = 1080;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                float t = (y + 0.5f - PY) / headH;
                byte a = (byte)Math.Round(255 * (t < 0 || t > 1 ? 1f : Math.Max(0f, Math.Min(1f, alpha(t)))));
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    px[o] = px[o + 1] = px[o + 2] = 255;
                    px[o + 3] = a;
                }
            }
            string d = Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.Cache, "wash-masks");
            Directory.CreateDirectory(d);
            p = Path.Combine(d, "versions-" + headH + ".png");
            Png.Write(p, w, h, 4, px);
            masks[headH] = p;
            return p;
        }

        public override void ApplyTexts()
        {
            heading.Text = target.Type == "Episode" ? target.SeriesName ?? show.Name ?? "" : target.Name ?? "";
            var parts = new List<string>();
            if (target.Type == "Episode")
            {
                string code = Library.Code(target);
                if (code != null) parts.Add(code);
                if (!string.IsNullOrEmpty(target.Name)) parts.Add(target.Name);
            }
            else if (target.ProductionYear != null) parts.Add(target.ProductionYear.ToString());
            sub.Text = string.Join(" · ", parts);
            if (halfTexts != null)
            {
                halfTexts[0].Text = Lang.T("versions.resume", Library.Clock(resumeTicks));
                halfTexts[1].Text = Lang.T("versions.start");
            }
            if (!failed) status.Text = StatusText();
            Note();
        }

        string StatusText()
        {
            if (loading) return Lang.T("versions.loading");
            int versions = vlist.Versions;
            return versions == 0 ? Lang.T("versions.none") : Lang.T(versions == 1 ? "versions.one" : "versions.count", versions);
        }

        // Ouverture en fondu de 0,2 s (l'auteur, 2026-11-18).
        Animation appear; // gardée dans un champ
        bool appeared;

        public override void OnShow()
        {
            Note();
            if (appeared) return;
            appeared = true;
            View shown = outer ?? root;
            shown.Opacity = 0f;
            appear = new Animation(Theme.Slide);
            appear.AnimateTo(shown, "Opacity", 1f);
            appear.Play();
            // Demandée ici, une fois l'écran empilé : une liste déjà préchargée arrive tout de
            // suite, et le lecteur lancé directement doit remplacer cet écran, pas celui de
            // dessous (vu le 2026-11-21 : panneau d'attente resté par-dessus le lecteur, accueil
            // fermé, Retour sortait de l'application).
            Load(false);
        }
        public override void OnHide() { Measure.Note = null; }

        public override void OnClose()
        {
            gen++;
            spin?.Stop();
            vlist.Stop();
            waitStreak?.Stop();
            guard.Stop();
            Measure.Note = null;
        }

        // ---- Chargement ----

        // Bouton « Rechercher à nouveau » seulement si le serveur le permet (comme le web).
        async void AskRefreshable()
        {
            bool ok;
            try { ok = await Library.CanRefresh(); }
            catch (Exception) { return; }
            if (Closed || !ok) return;
            guard.Run("Refreshable", () =>
            {
                canRefresh = true;
                refresh.Show();
                Spinning(loading);
            });
        }

        async void Load(bool again)
        {
            int g = ++gen;
            loading = true;
            failed = false;
            lastRefresh = again;
            status.TextColor = Theme.Grey;
            status.Text = Lang.T("versions.loading");
            Spinning(true);
            clock.Restart();
            PlaybackInfo info;
            try { info = again ? await Library.Versions(target.Id, true) : await Library.VersionsOrPreloaded(target.Id, out reused); }
            catch (ApiError e) { if (!Closed && g == gen) Fail(SignInScreen.ErrorText(e)); return; }
            catch (Exception) { if (!Closed && g == gen) Fail(Lang.T("error.unknown")); return; }
            if (Closed || g != gen) return;
            loading = false;
            Spinning(false);
            if (again) Library.Remember(target.Id, info);
            tList = (clock.ElapsedMilliseconds / 1000.0).ToString("0.0");
            sources = info?.MediaSources ?? new Source[0];
            playSession = info?.PlaySessionId;
            Probe.Log("versions", $"{sources.Length} versions en {tList} s" + (again ? " (recherche à nouveau)" : reused ? " (préchargées)" : "") + (straight && !again ? ", lancement direct" : ""));
            if (straight && !again)
            {
                var playable = new List<Source>();
                foreach (Source src in sources) if (src.Type != "Placeholder") playable.Add(src);
                Source pick = last != null ? playable.Find(src => src.Id == last) : null;
                if (last != null)
                {
                    Probe.Log("versions", pick != null ? $"dernière version retrouvée ({playable.IndexOf(pick) + 1}/{playable.Count})" : "dernière version introuvable");
                    if (pick == null) LastVersions.Forget(target.Id);
                }
                if (pick == null && first && playable.Count > 0) pick = playable[0];
                if (pick != null)
                {
                    guard.Run("Straight", () => Nav.Replace(new PlayerScreen(target, show, pick, playable, playSession, startTicks, all: sources) { Seamless = waiting }));
                    return;
                }
            }
            guard.Run("Versions", () =>
            {
                Reveal();
                vlist.Place(sources);
                status.Text = StatusText();
                if (vlist.Focus < 0 && zone == Zone.List && canRefresh) Enter(Zone.Refresh);
                vlist.Start();
            });
        }

        void Fail(string message)
        {
            Reveal();
            loading = false;
            Spinning(false);
            failed = true;
            status.TextColor = Theme.Error;
            status.Text = message + "  " + Lang.T("versions.retry");
        }

        // Icône « Rechercher à nouveau » qui tourne pendant le chargement des liens, comme le web
        // (demande de l'auteur, 2026-11-19) ; à l'arrêt, elle finit son tour.
        void Spinning(bool on)
        {
            if (!canRefresh) return;
            if (!on) { if (spin != null) spin.Looping = false; return; }
            spin?.Stop();
            spin = Theme.Spin(refreshIcon);
            spin.Play();
        }

        // « Rechercher à nouveau » : liste vidée, puis redemandée au serveur.
        void SearchAgain()
        {
            if (loading) { Ignored(); return; }
            vlist.Clear();
            Load(true);
        }

        // ---- En-tête ----

        void PaintToggle()
        {
            bool resume = startTicks > 0;
            for (int i = 0; i < 2; i++)
            {
                bool chosen = (i == 0) == resume;
                halves[i].BackgroundColor = chosen ? Color.White : Color.Transparent;
                halfTexts[i].TextColor = chosen ? Theme.Ink : Color.White;
            }
        }

        void Enter(Zone z)
        {
            zone = z;
            if (capRing != null) { if (z == Zone.Toggle) capRing.Show(); else capRing.Hide(); }
            refresh.BackgroundColor = z == Zone.Refresh ? Color.White : Theme.Surface;
            refreshIcon.Color = z == Zone.Refresh ? Theme.Ink : Color.White;
            vlist.Active = z == Zone.List;
            vlist.Refocus();
        }

        // ---- Touches ----

        public override bool OnKey(string key)
        {
            bool done = true;
            guard.Run("Key " + key, () => done = Keys(key));
            return done;
        }

        bool Keys(string key)
        {
            if (waiting) { Ignored(); return true; }
            switch (key)
            {
                case "Up":
                    if (zone == Zone.List)
                    {
                        if (vlist.Up()) { }
                        else if (capsule != null) Enter(Zone.Toggle);
                        else if (canRefresh) Enter(Zone.Refresh);
                        else Ignored();
                    }
                    else if (zone == Zone.Toggle && canRefresh) Enter(Zone.Refresh);
                    else Ignored();
                    return true;
                case "Down":
                    if (zone == Zone.Refresh && capsule != null) Enter(Zone.Toggle);
                    else if (zone != Zone.List && vlist.Focus >= 0) Enter(Zone.List);
                    else if (zone == Zone.List && vlist.Down()) { }
                    else Ignored();
                    return true;
                case "Left":
                case "Right":
                    if (zone != Zone.Toggle) return false;
                    long want = key == "Left" ? resumeTicks : 0;
                    if (want == startTicks) { Ignored(); return true; }
                    startTicks = want;
                    PaintToggle();
                    return true;
                case "Return":
                case "KP_Enter":
                    if (zone == Zone.Refresh) { SearchAgain(); return true; }
                    if (zone == Zone.Toggle)
                    {
                        startTicks = startTicks > 0 ? 0 : resumeTicks;
                        PaintToggle();
                        return true;
                    }
                    if (failed) { Load(lastRefresh); return true; }
                    if (vlist.Focus < 0) { Ignored(); return true; }
                    Nav.Replace(new PlayerScreen(target, show, vlist.Focused, vlist.Playable(), playSession, startTicks, all: sources));
                    return true;
            }
            return false;
        }

        static void Ignored() => App.Current.KeyIgnored();

        // ---- Mesures ----

        void Note() => Measure.Note = () => Closed ? null : Lang.T("versions.panel", tList, vlist.Versions, vlist.RowCount - vlist.Versions, vlist.Built, guard.SlowCount, guard.Slowest());
    }
}
