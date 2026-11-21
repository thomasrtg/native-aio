// Lecteur : une version lue par le lecteur natif de la TV (Tizen.Multimedia.Player), sous
// la fenêtre NUI rendue transparente. Présentation reprise du lecteur de la version web :
// titre en haut à gauche, heure en haut à droite, temps et heure de fin au-dessus de la
// barre d'avancement, boutons en dessous avec leur nom dans une bulle.
//
// Touches : OK pause ou reprise (sur la barre) ; Gauche / Droite -10 s / +10 s, cumulables,
// le saut part 0,7 s après le dernier appui ; Bas descend aux boutons (lecture, -10, +10, épisodes précédent et suivant,
// versions, audio, sous-titres, statistiques), Haut remonte à la barre ; Retour ferme le
// panneau ouvert, sinon arrête la lecture. Touches Lecture/Pause de la télécommande si elles
// arrivent.
//
// Chargement (au départ, après un saut ou quand l'image s'arrête) : logo et traînée animée,
// puis zoom en fondu. En pause depuis 5 s : logo et résumé du titre (écran de pause).
// L'avancement est envoyé au serveur (Core/Playback.cs).
//
// Sous-titres (module 6) : ceux du fichier sont envoyés en texte par le lecteur ; ceux du
// serveur sont téléchargés et affichés par l'application d'après la position de lecture,
// avec un décalage réglable et un recalage automatique (Core/Subtitles.cs, SubtitleSync.cs).
//
// Fin d'épisode (module 7, comme components/player/segments.ts et next-episode.tsx du web) :
// bouton « Passer l'intro » (récap, générique…) pendant les segments du serveur ; carte
// « Épisode suivant » au début du générique qui finit l'épisode, sinon 30 s avant la fin,
// avec un décompte de 15 s ; « Vous regardez toujours ? » au 3e épisode enchaîné sans appui.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
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
    class PlayerScreen : Screen
    {
        const int Margin = 60, BarY = 968, BarW = 1920 - 2 * Margin, BtnY = 998, BtnD = 52, BtnIcon = 30, HideAfter = 5000, SeekDelay = 700, PauseAfter = 5000;
        const int PanelW = 440, StatsW = 340, RowsShown = 9;
        // Menus (pistes, réglages) comme le web : intitulé gris en tête, lignes sans fond au repos ;
        // hauteur au plus 60 % de l'écran (9 lignes), largeur d'après le texte. Fond presque noir
        // neutre à 95 % : le gris du web (bg-gray-950, #030712) paraissait bleu très foncé sur la
        // TV (l'auteur, 2026-11-19).
        const int MenuMinW = 260;
        static readonly Color MenuBack = Theme.Menu; // fond commun des menus
        const int PreBuffer = 3000, ReBuffer = 5000;
        // Écran de chargement, repris tel quel par l'attente de VersionsScreen (reprise directe).
        internal const int LogoW = 760, LogoH = 340, LogoMidY = 470, TrackW = 220, StreakPeriod = 2200;
        const int Fade = 400;
        static readonly Color Dim = new Color(0f, 0f, 0f, 0.55f);
        // Icône d'un bouton au repos : blanc à 85 % (text-white/85 du web).
        static readonly Color IconRest = Theme.White(0.85f);

        enum Zone { Skip, Bar, Buttons }
        enum Btn { Play, Back, Forward, Prev, Next, Versions, Audio, Subtitles, Stats }
        static readonly Btn[] BtnOrder = { Btn.Play, Btn.Back, Btn.Forward, Btn.Prev, Btn.Next, Btn.Versions, Btn.Audio, Btn.Subtitles, Btn.Stats };
        static readonly string[] BtnIcons = { "play_lu", "back10", "forward", "previous", "next", "layers", "audio", "captions", "stats" };
        const int LeftButtons = 5;
        // Réglages de la page Lecture (Core/Prefs.cs), lus à l'ouverture du lecteur.
        readonly int Step = Prefs.SeekStep * 1000; // avance et recul (10 s par défaut, demande de l'auteur, 2026-11-10)
        readonly int CountdownMs = Prefs.NextCountdown * 1000, StillAfter = Prefs.StillWatching, PromptLead = Prefs.NextLead * 1000;
        readonly bool nextAuto = Prefs.NextAuto, fallbackFirst = Prefs.NextFallbackFirst, chapterSkips = Prefs.ChapterSkips;
        readonly string nextPrompt = Prefs.NextPrompt;
        readonly Dictionary<string, string> segmentAction = SegmentActions();

        static Dictionary<string, string> SegmentActions()
        {
            var d = new Dictionary<string, string>();
            foreach (string t in Prefs.SegmentTypes) d[t] = Prefs.Segment(t);
            return d;
        }

        // ask : bouton Passer ; skip : passé seul ; none : rien (type inconnu : bouton).
        string ActionOf(Skippable s) => segmentAction.TryGetValue(s.Type, out string a) ? a : "ask";

        readonly Item target, show;
        readonly List<Source> versions;
        Source source;
        readonly string playSession;
        readonly long startMs;
        readonly SynchronizationContext ui;
        readonly Stopwatch clock = Stopwatch.StartNew();

        Player player;
        Reporter reporter;
        Guarded guard;

        View root, controls, cover, backdrop, logoBox, tip, pauseView;
        TextLabel title, subtitleLine, now, times, timesTotal, endsAt, center, stats, tipLabel, noticeLabel;
        View noticeBox;
        View barTrack, barPlayed, barThumb;
        readonly View[] btnBox = new View[9];
        ImageView playIcon;
        readonly ImageView[] btnImg = new ImageView[9];
        int timesLength = -1; // longueur du temps écoulé à la dernière mesure de sa largeur
        Streak loaderStreak;
        Animation outAnim, pauseAnim; // gardées dans des champs

        Timer ticker, hideTimer, seekTimer, subtitleHide, seekShow, pauseTimer, warm, subTimer, noticeTimer; // gardés dans des champs, sinon ils s'arrêtent
        Zone zone = Zone.Bar;
        int btnFocus;
        bool controlsShown, started, ended, failed, interrupted, seeking, playing, loaderUp, pauseShown;
        int statsPage; // 0 : désactivé, 1 : lecture, 2 : cache, 3 : pistes
        int durationMs, positionMs, basePos = int.MinValue, stillTicks, lastPos = -1;
        long lastSent;
        int pendingSeek = -1; // position visée par les appuis Gauche/Droite, -1 sinon
        int subtitleChoice = -1; // piste de sous-titres du lecteur, -1 : aucune ; ServerBase + n : n-ième piste du serveur
        bool subtitleTouched;    // sous-titres choisis dans le menu pour cette version : plus de choix automatique
        int bufferPercent = 100, lastBufferLog = -1;
        SubtitleLook subtitle; // sous-titre affiché, au style choisi (Ui/SubtitleLook.cs)

        // Panneau : « audio », « subtitles » ou « versions », null s'il est fermé.
        string panelKind;
        int panelFocus, panelSelected;
        List<PanelItem> panelItems = new List<PanelItem>();
        // Menus sur la boîte de choix commune (Ui/Picker, module 14) : intitulé, place et note
        // décidés à l'ouverture (OpenPanel), dessinés par ShowPanel.
        readonly Picker menu = new Picker();
        string panelTitleText;
        int panelX, keepFocus = -1; // keepFocus : ligne regardée gardée à la réouverture (réglage, « Autres langues »)
        bool othersOpen; // sous-titres : « Autres langues » dépliées

        // Ligne d'un menu : nom, détail en petit dessous (comme la version web), valeur choisie
        // (piste du lecteur, -1 : aucun sous-titre, -2 : « Autres langues », page des statistiques).
        class PanelItem
        {
            public string Name, Detail;
            public int Value;
            public Action Choose;     // OK sur la ligne (au lieu d'un choix de piste)
            public Action<int> Step;  // Gauche / Droite sur la ligne : -1 / +1
            public bool Sub;          // ouvre un autre menu : › à droite, comme le web
        }

        const int OffValue = -1, OthersValue = -2;
        // Tiroir des versions (comme le web) : à droite, décollé de 8 px des bords, fond du thème,
        // vidéo assombrie à gauche ; cartes de la liste des liens (Ui/VersionList.cs).
        const int DrawerW = 600, DrawerX = 1920 - 8 - DrawerW, DrawerY = 8, DrawerH = 1080 - 16, DrawerPad = 32;
        readonly Source[] allSources;
        View drawer;
        VersionList vlist;
        Animation drawerAnim; // gardée dans un champ

        // Mesures.
        long tOpen = -1, tPrepared = -1, tFirstFrame = -1, readMax;
        readonly List<long> seekTimes = new List<long>();
        int bufferEvents, autoPauses, subtitleEvents, stalls;
        long lastBufferAt = -10000;
        string videoInfo = "-", lastError = "";

        // chain : épisodes passés seuls, sans appui, juste avant celui-ci (« Vous regardez toujours ? »).
        // all : réponse entière du serveur (messages des addons compris), pour le tiroir des versions.
        public PlayerScreen(Item target, Item show, Source source, List<Source> versions, string playSession, long startTicks, int chain = 0, Source[] all = null)
        {
            this.chain = chain;
            allSources = all;
            this.target = target;
            this.show = show ?? target;
            this.source = source;
            this.versions = versions ?? new List<Source> { source };
            this.playSession = playSession;
            startMs = Math.Max(0, startTicks / Playback.TicksPerMs);
            ui = SynchronizationContext.Current;
        }

        // Les rappels du lecteur arrivent sur ses propres fils : repasser sur celui de l'interface.
        void Ui(string where, Action a) => ui.Post(_ => { if (!Closed) guard.Run(where, a); }, null);

        string Icon(string name) => Tizen.Applications.Application.Current.DirectoryInfo.Resource + "icons/" + name + ".png";

        string LogoUrl(int w) => Library.Logo(target, w) ?? (show != target ? Library.Logo(show, w) : null);

        string ShowName => target.Type == "Episode" ? target.SeriesName ?? show.Name ?? "" : target.Name ?? "";

        // ---- Construction ----

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080) };
            guard = new Guarded(root);

            BuildLoader();

            // Sous-titres : bas du texte à (97 - hauteur) % de l'écran, comme la version web.
            subtitle = new SubtitleLook(1600, SubH, 1f);
            subtitle.View.PositionX = 160;
            subtitle.Apply();
            ApplySubStyle(false);
            subtitle.SetText(null);
            root.Add(subtitle.View);

            BuildPause();

            center = new TextLabel
            {
                Position = new Position(260, 470),
                Size = new Size(1400, 140),
                PointSize = Theme.SizeHeading,
                MultiLine = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextColor = Color.White,
            };
            center.Hide();
            root.Add(center);

            BuildControls();
            BuildEnding();
            BuildPanel();

            // Message bref (décalage, recalage), en haut à gauche sous le titre.
            noticeBox = new View { BackgroundColor = Theme.Panel }.Rounded(Theme.RadiusLarge);
            noticeLabel = new TextLabel { PointSize = Theme.SizeInfo, TextColor = Color.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            noticeBox.Add(noticeLabel);
            noticeBox.Hide();
            root.Add(noticeBox);

            stats = new TextLabel
            {
                Position = new Position(Margin, 170),
                Size = new Size(1100, 560),
                PointSize = 15,
                MultiLine = true,
                TextColor = Color.White,
                BackgroundColor = new Color(0f, 0f, 0f, 0.65f),
                VerticalAlignment = VerticalAlignment.Top,
            };
            stats.Hide();
            root.Add(stats);
            return root;
        }

        // Écran de chargement : fond, logo au centre (le titre écrit s'il n'y en a pas) et traînée
        // blanche dans une barre fine (demandes de l'auteur). Au départ, le fond est l'image du titre
        // assombrie ; pendant la lecture, c'est l'image du film figée, assombrie.
        void BuildLoader()
        {
            cover = new View { Size = new Size(1920, 1080), BackgroundColor = Color.Black };
            root.Add(cover);
            string back = Library.Backdrop(show, 1920) ?? Library.Landscape(target, 1920);
            if (back != null)
                ImageCache.Request(back, () => Closed ? int.MaxValue : 0, path => guard.Run("Backdrop", () =>
                {
                    if (Closed || path == null) return;
                    View v = Pictures.Cover(path, 1920, 1080, out _);
                    if (v == null) return;
                    v.Opacity = 0.35f;
                    backdrop = v;
                    cover.Add(v);
                    v.LowerToBottom();
                    if (started) v.Hide();
                }));
            // Vue plein écran : un agrandissement autour de son centre est un zoom centré sur l'écran.
            logoBox = new View { Size = new Size(1920, 1080), PivotPoint = PivotPoint.Center };
            cover.Add(logoBox);
            string logo = LogoUrl(LogoW);
            if (logo == null) LoaderTitle();
            else
                ImageCache.Request(logo, () => Closed ? int.MaxValue : 0, path => guard.Run("Logo", () =>
                {
                    if (Closed) return;
                    ImageView v = Fitted(path, LogoW, LogoH, out int fw, out int fh);
                    if (v == null) { LoaderTitle(); return; }
                    v.Position = new Position(960 - fw / 2, LogoMidY - fh / 2);
                    logoBox.Add(v);
                }));
            loaderStreak = new Streak(TrackW, 2, StreakPeriod);
            loaderStreak.View.Position = new Position(960 - TrackW / 2, LogoMidY + LogoH / 2 + 60);
            logoBox.Add(loaderStreak.View);
        }

        // Image entière dans un cadre w×h, sans déformation ; null si illisible.
        internal static ImageView Fitted(string path, int w, int h, out int fw, out int fh)
        {
            fw = fh = 0;
            if (path == null) return null;
            Pictures.ImageSize(path, out int iw, out int ih);
            if (iw <= 0 || ih <= 0) return null;
            float k = Math.Min(w / (float)iw, h / (float)ih);
            fw = Math.Max(1, (int)(iw * k));
            fh = Math.Max(1, (int)(ih * k));
            return Pictures.Decoded(path, fw, fh);
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
                Text = ShowName,
            });
        }

        // Chargement montré. initial : au départ (fond du titre) ; sinon par-dessus l'image figée.
        // La traînée est animée par la TV elle-même (aucun travail du fil d'interface).
        // Seamless : le même écran de chargement est déjà affiché par VersionsScreen (reprise
        // directe), qui s'efface par-dessus : pas de fondu d'apparition.
        public bool Seamless;

        void ShowLoader(bool initial)
        {
            outAnim?.Stop();
            loaderUp = true;
            cover.BackgroundColor = initial ? Color.Black : Dim;
            if (backdrop != null) { if (initial) backdrop.Show(); else backdrop.Hide(); }
            logoBox.Scale = new Vector3(1f, 1f, 1f);
            logoBox.Opacity = 1f;
            if (initial && Seamless)
            {
                Seamless = false;
                cover.Opacity = 1f;
                cover.Show();
                loaderStreak.Play();
                return;
            }
            // Apparition en fondu, même durée que la disparition (0,4 s, demande de l'auteur).
            cover.Opacity = 0f;
            cover.Show();
            loaderStreak.Play();
            outAnim = new Animation(Fade);
            outAnim.AnimateTo(cover, "Opacity", 1f);
            outAnim.Play();
        }

        // Le logo grossit en s'effaçant, le fond s'efface avec lui.
        void HideLoader()
        {
            if (!loaderUp) return;
            loaderUp = false;
            loaderStreak.Stop();
            outAnim?.Stop();
            outAnim = new Animation(Fade);
            outAnim.AnimateTo(logoBox, "Scale", new Vector3(1.25f, 1.25f, 1f), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            outAnim.AnimateTo(logoBox, "Opacity", 0f);
            outAnim.AnimateTo(cover, "Opacity", 0f);
            outAnim.Finished += (s, e) => { if (!Closed && !loaderUp) cover.Hide(); };
            outAnim.Play();
        }

        // Écran de pause (comme la version web) : image assombrie, plus fort à gauche, logo et
        // résumé du titre, texte avec une ombre.
        void BuildPause()
        {
            pauseView = new View { Size = new Size(1920, 1080), Opacity = 0f };
            pauseView.Add(Gradient.Make(new Position(0, 0), new Size(1920, 1080), true, t => 0.82f - 0.5f * t));
            const int x = 96, w = 860, logoW = 620, logoH = 240, logoTop = 230;
            int y = logoTop + logoH + 28;
            string logo = LogoUrl(logoW);
            // Tailles de la charte : titre sans logo 48 pt, épisode 22 pt demi-gras (text-xl
            // font-semibold du web), résumé 18 pt gris clair (text-gray-200).
            if (logo == null) pauseView.Add(ShadowLabel(new Position(x, logoTop + logoH - 120), new Size(w, 120), Theme.SizeHuge, ShowName, true));
            else
                ImageCache.Request(logo, () => Closed ? int.MaxValue : 0, path => guard.Run("PauseLogo", () =>
                {
                    if (Closed) return;
                    ImageView v = Fitted(path, logoW, logoH, out int fw, out int fh);
                    if (v == null) { pauseView.Add(ShadowLabel(new Position(x, logoTop + logoH - 120), new Size(w, 120), Theme.SizeHuge, ShowName, true)); return; }
                    v.Position = new Position(x, logoTop + logoH - fh); // logo posé sur la ligne du texte
                    pauseView.Add(v);
                }));
            if (target.Type == "Episode")
            {
                var parts = new List<string>();
                string code = Library.Code(target);
                if (code != null) parts.Add(code);
                if (!string.IsNullOrEmpty(target.Name)) parts.Add(target.Name);
                TextLabel ep = ShadowLabel(new Position(x, y), new Size(w, 40), Theme.SizeHeading, string.Join(" · ", parts), false);
                ep.FontStyle = Theme.SemiBold();
                pauseView.Add(ep);
                y += 48;
            }
            string overview = target.Overview ?? (target.Type == "Episode" ? null : show.Overview);
            if (!string.IsNullOrEmpty(overview))
            {
                TextLabel ov = ShadowLabel(new Position(x, y), new Size(w, 7 * 31), Theme.SizeBody, Pictures.Cut(overview.Trim(), 7 * 78), true);
                ov.TextColor = Theme.Soft;
                pauseView.Add(ov);
            }
            pauseView.Hide();
            root.Add(pauseView);
        }

#pragma warning disable CS0618 // ShadowOffset/ShadowColor : dépréciés en API 8, toujours présents (comme l'accueil)
        static TextLabel ShadowLabel(Position p, Size s, float size, string text, bool multi) => new TextLabel
        {
            Position = p,
            Size = s,
            PointSize = size,
            MultiLine = multi,
            TextColor = Color.White,
            ShadowOffset = new Vector2(2, 2),
            ShadowColor = new Color(0f, 0f, 0f, 0.9f),
            VerticalAlignment = VerticalAlignment.Top,
            Text = text ?? "",
        };
#pragma warning restore CS0618

        void ShowPause()
        {
            if (pauseShown || playing || !started || failed || panelKind != null || loaderUp || asking || leaving) return;
            pauseShown = true;
            controls.Hide();
            controlsShown = false;
            UpdateLift();
            hideTimer?.Stop();
            pauseView.Show();
            pauseAnim?.Stop();
            pauseAnim = new Animation(400);
            pauseAnim.AnimateTo(pauseView, "Opacity", 1f);
            pauseAnim.Play();
        }

        void HidePause()
        {
            pauseTimer?.Stop();
            if (!pauseShown) return;
            pauseShown = false;
            // Même fondu qu'à l'apparition (demande de l'auteur).
            pauseAnim?.Stop();
            pauseAnim = new Animation(400);
            pauseAnim.AnimateTo(pauseView, "Opacity", 0f);
            pauseAnim.Finished += (s, e) => { if (!Closed && !pauseShown) pauseView.Hide(); };
            pauseAnim.Play();
        }

        void BuildControls()
        {
            controls = new View { Size = new Size(1920, 1080) };
            root.Add(controls);
            // Ombres en haut et en bas : dégradé dessiné par la TV (les bandes faisaient des paliers).
            controls.Add(Gradient.Make(new Position(0, 0), new Size(1920, 220), false, t => 0.75f * (1 - t)));
            controls.Add(Gradient.Make(new Position(0, 1080 - 300), new Size(1920, 300), false, t => 0.85f * t));

            controls.Add(new ImageView(Icon("arrow_left")) { Position = new Position(Margin, 52), Size = new Size(40, 40) });
            // Comme le web : titre demi-gras, épisode en gris clair (text-gray-300), heure demi-gras.
            title = new TextLabel { Position = new Position(Margin + 64, 40), Size = new Size(1400, 46), PointSize = Theme.SizeHeading, TextColor = Color.White, FontStyle = Theme.SemiBold() };
            subtitleLine = new TextLabel { Position = new Position(Margin + 64, 88), Size = new Size(1400, 36), PointSize = Theme.SizeBody, TextColor = Theme.Soft };
            now = new TextLabel { Position = new Position(1920 - Margin - 200, 44), Size = new Size(200, 40), PointSize = Theme.SizeHeading, TextColor = Color.White, HorizontalAlignment = HorizontalAlignment.End, FontStyle = Theme.SemiBold() };
            // Temps : écoulé en blanc demi-gras, « / durée » en gris à sa suite (deux textes).
            times = new TextLabel { Position = new Position(Margin, BarY - 52), Size = new Size(300, 38), PointSize = Theme.SizeBody, TextColor = Color.White, FontStyle = Theme.SemiBold() };
            timesTotal = new TextLabel { Position = new Position(Margin + 80, BarY - 52), Size = new Size(700, 38), PointSize = Theme.SizeBody, TextColor = Theme.Grey };
            endsAt = new TextLabel { Position = new Position(1920 - Margin - 600, BarY - 52), Size = new Size(600, 38), PointSize = Theme.SizeBody, TextColor = Theme.Grey, HorizontalAlignment = HorizontalAlignment.End };
            controls.Add(title);
            controls.Add(subtitleLine);
            controls.Add(now);
            controls.Add(times);
            controls.Add(timesTotal);
            controls.Add(endsAt);

            // Barre fine comme le web : rail blanc à 20 %, partie lue à l'accent éclairci (--brand) ;
            // rond blanc seulement quand la barre est regardée (posés par Refresh).
            barTrack = new View { Position = new Position(Margin, BarY), Size = new Size(BarW, 4), BackgroundColor = Theme.White(0.2f) }.Rounded(2);
            barPlayed = new View { Position = new Position(0, 0), Size = new Size(0, 4), BackgroundColor = Theme.AccentText }.Rounded(2);
            barTrack.Add(barPlayed);
            controls.Add(barTrack);
            barThumb = new View { Size = new Size(16, 16), BackgroundColor = Color.White }.Rounded(8);
            controls.Add(barThumb);

            // Boutons : lecture, -10, +10, épisodes précédent et suivant à gauche ; versions, audio, sous-titres,
            // statistiques à droite. Positions posées par LayoutButtons. Comme le web : icône seule,
            // regardé = rond blanc et icône teintée en foncé (Refresh).
            for (int i = 0; i < BtnOrder.Length; i++)
            {
                var box = new View { Position = new Position(0, BtnY), Size = new Size(BtnD, BtnD) }.Rounded(BtnD / 2);
                int o = (BtnD - BtnIcon) / 2;
                var img = new ImageView(Icon(BtnIcons[i])) { Position = new Position(o, o), Size = new Size(BtnIcon, BtnIcon), Color = IconRest };
                box.Add(img);
                if (i == 0) playIcon = img;
                btnImg[i] = img;
                controls.Add(box);
                btnBox[i] = box;
            }
            LayoutButtons();

            // Bulle au-dessus du bouton choisi : son nom, comme la bulle de la barre latérale.
            tip = new View { Size = new Size(200, 44), BackgroundColor = Theme.Panel }.Rounded(Theme.RadiusLarge);
            tipLabel = new TextLabel { Size = new Size(200, 44), PointSize = Theme.SizeBody, TextColor = Color.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tip.Add(tipLabel);
            tip.Hide();
            controls.Add(tip);
            controls.Hide();
        }

        // Épisodes précédent et suivant : montrés seulement quand il y en a un (connus au début
        // de la lecture).
        bool ButtonShown(int i) => BtnOrder[i] == Btn.Next ? next != null : BtnOrder[i] == Btn.Prev ? prev != null : true;

        void LayoutButtons()
        {
            int x = Margin - (BtnD - BtnIcon) / 2; // icône alignée sur la marge, comme le texte
            for (int i = 0; i < LeftButtons; i++)
            {
                if (!ButtonShown(i)) { btnBox[i].Hide(); continue; }
                btnBox[i].PositionX = x;
                btnBox[i].Show();
                x += BtnD + 12;
            }
            for (int i = LeftButtons; i < BtnOrder.Length; i++)
                btnBox[i].PositionX = 1920 - Margin + (BtnD - BtnIcon) / 2 - (BtnOrder.Length - i) * (BtnD + 12) + 12;
            if (!ButtonShown(btnFocus)) btnFocus = 0;
        }

        // Bouton voisin montré, dans la direction dir ; -1 s'il n'y en a pas.
        int NextButton(int from, int dir)
        {
            for (int i = from + dir; i >= 0 && i < BtnOrder.Length; i += dir)
                if (ButtonShown(i)) return i;
            return -1;
        }

        void BuildPanel()
        {
            root.Add(menu.View);
        }

        public override void ApplyTexts()
        {
            title.Text = ShowName;
            if (target.Type == "Episode")
            {
                var parts = new List<string>();
                string code = Library.Code(target);
                if (code != null) parts.Add(code);
                if (!string.IsNullOrEmpty(target.Name)) parts.Add(target.Name);
                subtitleLine.Text = string.Join(" · ", parts);
            }
            else subtitleLine.Text = target.ProductionYear?.ToString() ?? "";
            if (panelKind != null) OpenPanel(panelKind);
            Refresh();
            Note();
        }

        public override void OnShow()
        {
            Window.Instance.BackgroundColor = Color.Transparent;
            Note();
            if (player == null && !failed) Open(startMs, true);
        }

        public override void OnHide()
        {
            Window.Instance.BackgroundColor = App.Background;
            Measure.Note = null;
        }

        public override void OnClose()
        {
            guard.Run("Close", () =>
            {
                subCheck?.Stop();
                StopReport();
                ticker?.Stop();
                hideTimer?.Stop();
                seekTimer?.Stop();
                seekShow?.Stop();
                subtitleHide?.Stop();
                subTimer?.Stop();
                noticeTimer?.Stop();
                holdTimer?.Stop();
                pauseTimer?.Stop();
                countTimer?.Stop();
                vlist?.Stop();
                Release();
            });
            guard.Stop();
            Window.Instance.BackgroundColor = App.Background;
            Measure.Note = null;
        }

        // ---- Lecture ----

        // initial : premier lancement (écran de chargement sur le fond du titre) ; sinon reprise
        // après un changement de version ou un retour d'arrière-plan.
        async void Open(long fromMs, bool initial)
        {
            failed = false;
            started = false;
            basePos = int.MinValue;
            stillTicks = 0;
            lastPos = -1;
            tOpen = clock.ElapsedMilliseconds;
            tPrepared = tFirstFrame = -1;
            HidePause();
            ShowLoader(initial);
            SetSubText(null);
            center.Hide();
            controls.Hide();
            controlsShown = false;
            UpdateLift();
            // Chapitres lus avant d'ouvrir le lecteur, pendant l'écran de chargement : jamais deux
            // connexions au même lien (idée de l'auteur). 3 s au plus.
            if (chapterSkips && chaptersFor != source)
            {
                Source want = source;
                await ReadChapters();
                if (Closed || want != source || player != null) return;
            }
            // Un seul lecteur à la fois : la bande-annonce de l'accueil est fermée avant.
            if (!CardTrailer.Idle.IsCompleted)
            {
                Source want = source;
                await CardTrailer.Idle;
                if (Closed || want != source || player != null) return;
            }
            Player p;
            try
            {
                p = new Player();
                player = p;
                p.Display = new Display(Window.Instance);
                p.DisplaySettings.Mode = PlayerDisplayMode.LetterBox;
                p.ErrorOccurred += (s, e) => Ui("Error", () => { if (p == player) Fail(e.Error.ToString()); });
                p.BufferingProgressChanged += (s, e) => Ui("Buffering", () => Buffering(e.Percent));
                p.PlaybackCompleted += (s, e) => Ui("Completed", () => { if (p == player) Completed(); });
                p.PlaybackInterrupted += (s, e) => Ui("Interrupted", () => { interrupted = true; Probe.Log("player", "interrompu " + e.Reason); });
                // Moment d'arrivée noté sur le fil du lecteur : le passage au fil d'interface
                // ajouterait son attente au début de la réplique (recalage automatique).
                p.SubtitleUpdated += (s, e) => { long at = clock.ElapsedMilliseconds; Ui("Subtitle", () => { if (p == player) OnPlayerSubtitle(e.Text, (int)e.Duration, at); }); };
                p.VideoStreamChanged += (s, e) => Ui("VideoChanged", () => videoInfo = VideoText(e.Size.Width, e.Size.Height, e.Fps, e.BitRate));
                p.SetSource(new MediaUriSource(Playback.StreamUrl(target.Id, source)));
                // Mémoire tampon : avec le réglage de la TV, ~10 s avant la première image (4K, 27 Mb/s) ;
                // 3 s au départ et 5 s après un manque : 4,4 s (mesuré le 2026-11-08).
                try { p.BufferingTime = new PlayerBufferingTime(PreBuffer, ReBuffer); }
                catch (Exception e) { Probe.Log("player", "tampons non réglés : " + e.GetType().Name + " " + e.Message); }
                // Les appels au lecteur bloquent jusqu'à sa réponse (0,2 à 0,5 s mesurés) : hors
                // du fil d'interface.
                await Task.Run(() => p.PrepareAsync());
                if (Closed || p != player) { Dispose(p); return; }
                tPrepared = clock.ElapsedMilliseconds;
                Probe.Log("player", $"prêt après {tPrepared - tOpen} ms");
                guard.Run("Prepared", () =>
                {
                    durationMs = SafeDuration();
                    ReadVideo();
                    if (fileChapters != null && durationMs > 0) MergeSegments();
                });
                // Piste audio choisie selon les réglages, avant le démarrage si le lecteur l'accepte.
                AudioInputs audioIn = ReadAudioInputs();
                int audioLater = await Task.Run(() => ChooseAudio(p, audioIn));
                if (Closed || p != player) { Dispose(p); return; }
                // Sous-titres choisis selon les réglages (piste du fichier posée après le démarrage).
                SubtitlePick.Inputs subIn = SubtitlePick.Read(SeriesOf(target));
                int subPick = await Task.Run(() => PickSubtitle(p, subIn, audioLater));
                if (Closed || p != player) { Dispose(p); return; }
                guard.Run("SubPick", () => ApplySubPick(subPick));
                if (fromMs > 0)
                {
                    // Reprise : saut avant le démarrage (s'il est refusé, on démarre puis on saute).
                    try { await Task.Run(() => p.SetPlayPositionAsync((int)fromMs, false)); }
                    catch (Exception e) { Probe.Log("player", "saut avant démarrage refusé : " + e.Message); fromMs = -fromMs; }
                    if (Closed || p != player) { Dispose(p); return; }
                }
                await Task.Run(() => p.Start());
                playing = true;
                Probe.Log("player", $"démarré après {clock.ElapsedMilliseconds - tOpen} ms");
                if (fromMs < 0) await Task.Run(() => p.SetPlayPositionAsync((int)-fromMs, false));
                if (Closed || p != player) return;
                if (audioLater >= 0)
                {
                    try { await Task.Run(() => p.AudioTrackInfo.Selected = audioLater); Probe.Log("audio", $"piste {audioLater} posée après le démarrage"); }
                    catch (Exception e) { Probe.Log("audio", "piste refusée après le démarrage : " + e.Message); }
                    if (Closed || p != player) return;
                }
                interrupted = false;
                positionMs = (int)Math.Abs(fromMs);
                Anchor(positionMs);
                guard.Run("Tracks", ReapplySubtitle);
                if (reporter == null)
                {
                    reporter = new Reporter(target.Id, source.Id, playSession);
                    reporter.Start(positionMs, false, AudioIndex(), SubtitleIndex());
                    LastVersions.Note(target.Id, source); // relancée telle quelle à la reprise
                }
                lastSent = clock.ElapsedMilliseconds;
                StartTicker();
                AskSubtitles(source);
                if (!endingAsked) { endingAsked = true; LoadEnding(); }
            }
            catch (Exception e)
            {
                if (!Closed) guard.Run("Open", () => Fail(e.GetType().Name + " : " + e.Message));
            }
        }

        // Fichier refusé par le lecteur de la TV (format, codec), pas une panne : réessayer ne
        // sert à rien. -26476495 n'a pas de nom dans l'API 8 (« Unknown error ») : même code que
        // l'AV1 et le VP9 refusés, et qu'un MP4 iTunes à 47 pistes (LECONS.md, 2026-11-15).
        static bool Refused(string message) =>
            message != null && (message.Contains("-26476495") || message.Contains("NotSupportedFile")
                || message.Contains("VideoCodecNotSupported") || message.Contains("AudioCodecNotSupported"));

        // Versions refusées pendant cette lecture, et la suivante proposée par OK.
        readonly HashSet<Source> refused = new HashSet<Source>();
        Source refusedNext;

        // Version suivante dans la liste qui n'a pas déjà été refusée (null : aucune).
        Source NextUnrefused()
        {
            for (int i = versions.IndexOf(source) + 1; i < versions.Count; i++)
                if (!refused.Contains(versions[i])) return versions[i];
            return null;
        }

        void Fail(string message)
        {
            if (Closed) return;
            failed = true;
            lastError = message;
            Probe.Log("player", "erreur " + message);
            refusedNext = null;
            bool refusal = Refused(message);
            if (refusal)
            {
                refused.Add(source);
                refusedNext = NextUnrefused();
                Probe.Log("player", $"version {versions.IndexOf(source) + 1}/{versions.Count} refusée par la TV ; suivante proposée : "
                    + (refusedNext != null ? (versions.IndexOf(refusedNext) + 1).ToString() : "aucune"));
            }
            HideCard();
            PaintEnding();
            loaderStreak.Stop();
            loaderUp = false;
            outAnim?.Stop();
            HidePause();
            cover.BackgroundColor = Color.Black;
            if (backdrop != null) backdrop.Show();
            cover.Opacity = 1f;
            logoBox.Opacity = 0f;
            cover.Show();
            center.Text = !refusal ? Lang.T("player.error", message)
                : refusedNext != null ? Lang.T("player.refused_next", versions.IndexOf(refusedNext) + 1, versions.Count)
                : Lang.T("player.refused");
            center.Show();
            controls.Hide();
            controlsShown = false;
            UpdateLift();
        }

        void Buffering(int percent)
        {
            bufferEvents++;
            bufferPercent = percent;
            // Journal : au plus un message par seconde après le démarrage (ces événements arrivent par
            // dizaines pendant une lecture normale et noyaient le journal de la sonde).
            long nowMs = clock.ElapsedMilliseconds;
            if (!started ? percent / 25 != lastBufferLog / 25 : nowMs - lastBufferAt >= 1000)
            {
                Probe.Log("player", $"tampon {percent} % à {nowMs - tOpen} ms");
                lastBufferLog = percent;
                lastBufferAt = nowMs;
            }
            // L'affichage ne suit pas ces événements : pendant une lecture normale ils passent de
            // 0 à 84 puis 100 % toutes les 2 s environ (mesuré). Le chargement suit l'arrêt de l'image (Tick).
        }

        void Completed()
        {
            ended = true;
            positionMs = durationMs > 0 ? durationMs : positionMs;
            Probe.Log("player", "fin de la vidéo");
            if (leaving) return; // épisode suivant déjà demandé
            // Fin du fichier : l'épisode suivant démarre (comme playOn du web), avec l'écran de
            // chargement pendant la recherche de ses versions.
            if (next != null && nextAuto)
            {
                HideCard();
                skipBox.Hide();
                controls.Hide();
                controlsShown = false;
                // Avant PlayOn : si les versions sont déjà là, l'écran est remplacé dans l'appel.
                if (!AsksFirst) ShowLoader(false);
                PlayOn(true);
                return;
            }
            Nav.Back(); // OnClose envoie l'arrêt au serveur ; sans lancement automatique, comme le web
        }

        void StartTicker()
        {
            ticker?.Stop();
            ticker = new Timer(500);
            ticker.Tick += (s, e) => { guard.Run("Tick", Tick); return !Closed; };
            ticker.Start();
        }

        // Toutes les 500 ms : position, première image, chargement, affichage, envoi au serveur
        // toutes les 10 s.
        void Tick()
        {
            if (player == null || failed) return;
            // Pendant un saut, le lecteur ne répond qu'à la fin (0,6 s de blocage mesuré) : on attend.
            if (seeking) return;
            long t0 = clock.ElapsedMilliseconds;
            int pos = SafePosition();
            readMax = Math.Max(readMax, clock.ElapsedMilliseconds - t0);
            if (pos >= 0)
            {
                positionMs = pos;
                Anchor(pos);
            }
            bool wanted = playing;
            try { playing = player.State == PlayerState.Playing; } catch (Exception) { }
            // En pause sans appui : noté pour comprendre (vu une fois le 2026-11-08, cause inconnue).
            if (wanted && !playing && started)
            {
                autoPauses++;
                Probe.Log("player", $"pause sans appui à {positionMs} ms, tampon {bufferPercent} %");
            }
            if (!started)
            {
                // Première image : la position bouge après le démarrage.
                if (pos >= 0 && basePos == int.MinValue) basePos = pos;
                if (pos > 0 && pos != basePos && playing)
                {
                    started = true;
                    tFirstFrame = clock.ElapsedMilliseconds;
                    HideLoader();
                    // Barre mise en page une première fois, invisible : son premier affichage sur
                    // un appui coûtait 420 ms (mesuré le 2026-11-09).
                    if (!controlsShown)
                    {
                        Refresh();
                        controls.Opacity = 0f;
                        controls.Show();
                        warm = new Timer(300);
                        warm.Tick += (s, e) => { if (!controlsShown) controls.Hide(); controls.Opacity = 1f; return false; };
                        warm.Start();
                    }
                    Probe.Log("player", $"première image après {tFirstFrame - tOpen} ms");
                    guard.Run("SubFirst", RecheckSubtitle);
                }
            }
            else
            {
                // Image arrêtée en lecture (deux relevés identiques, soit ~1 s) : écran de chargement
                // par-dessus l'image figée ; il s'efface dès que l'image repart.
                if (playing && pos >= 0 && pos == lastPos) stillTicks++; else stillTicks = 0;
                if (stillTicks >= 2 && !loaderUp) { stalls++; Probe.Log("player", $"image arrêtée à {pos} ms"); ShowLoader(false); }
                else if (loaderUp && ((pos >= 0 && pos != lastPos && lastPos >= 0) || !playing)) HideLoader();
            }
            lastPos = pos;
            if (durationMs <= 0)
            {
                durationMs = SafeDuration();
                if (durationMs > 0 && fileChapters != null) MergeSegments();
            }
            if (clock.ElapsedMilliseconds - lastSent >= 10000)
            {
                lastSent = clock.ElapsedMilliseconds;
                reporter?.Progress("TimeUpdate", positionMs, !playing, AudioIndex(), SubtitleIndex());
            }
            Ending();
            if (controlsShown) Refresh();
            UpdateLift();
            if (statsPage > 0) stats.Text = StatsText();
        }

        // État tenu par l'application (relu sur le lecteur toutes les 500 ms) : lire l'état du
        // lecteur à chaque affichage coûterait un appel au système.
        bool IsPlaying() => player != null && playing;

        // Appel au lecteur hors du fil d'interface ; une erreur est notée dans les statistiques.
        void Background(string what, Action<Player> a)
        {
            Player p = player;
            if (p == null) return;
            long t0 = clock.ElapsedMilliseconds;
            Task.Run(() =>
            {
                try { a(p); Probe.Log("player", $"{what} en {clock.ElapsedMilliseconds - t0} ms"); }
                catch (Exception e) { Ui("BackgroundError", () => lastError = what + " : " + e.Message); }
            });
        }

        int SafePosition()
        {
            try { return player.GetPlayPosition(); } catch (Exception) { return -1; }
        }

        int SafeDuration()
        {
            try { return player.StreamInfo.GetDuration(); } catch (Exception) { return 0; }
        }

        void ReadVideo()
        {
            try
            {
                VideoStreamProperties v = player.StreamInfo.GetVideoProperties();
                videoInfo = VideoText(v.Size.Width, v.Size.Height, v.Fps, v.BitRate);
            }
            catch (Exception e) { videoInfo = e.Message; }
        }

        string VideoText(int w, int h, int fps, int bitrate)
        {
            string codec = "?";
            try { codec = player.StreamInfo.GetVideoCodec(); } catch (Exception) { }
            return $"{w}×{h}, {fps} i/s, {codec}" + (bitrate > 0 ? $", {bitrate / 1000000.0:0.0} Mb/s" : "");
        }

        void TogglePause()
        {
            if (player == null || failed) return;
            if (playing) PauseNow();
            else
            {
                playing = true;
                Anchor(anchorPos);
                Background("reprise", p => p.Start());
                reporter?.Progress("Unpause", positionMs, false, AudioIndex(), SubtitleIndex());
            }
            ShowControls();
        }

        void PauseNow()
        {
            if (player == null || !playing) return;
            Anchor(Now());
            playing = false;
            Background("pause", p => p.Pause());
            reporter?.Progress("Pause", positionMs, true, AudioIndex(), SubtitleIndex());
            if (loaderUp) HideLoader();
        }

        // Gauche / Droite : les appuis s'additionnent, le saut part après une courte pause.
        void SeekBy(int deltaMs)
        {
            if (player == null || failed) return;
            int from = pendingSeek >= 0 ? pendingSeek : positionMs;
            int max = durationMs > 0 ? durationMs - 1000 : int.MaxValue;
            pendingSeek = Math.Max(0, Math.Min(max, from + deltaMs));
            seekTimer?.Stop();
            seekTimer = new Timer(SeekDelay);
            seekTimer.Tick += (s, e) => { guard.Run("SeekGo", () => SeekGo()); return false; };
            seekTimer.Start();
            ShowControls();
        }

        async void SeekGo()
        {
            if (seeking || pendingSeek < 0 || player == null) return;
            int to = pendingSeek;
            Player p = player;
            seeking = true;
            // Saut de plus de 0,4 s : écran de chargement par-dessus l'image figée.
            seekShow?.Stop();
            seekShow = new Timer(400);
            seekShow.Tick += (s, e) => { if (seeking && !Closed && !loaderUp) ShowLoader(false); return false; };
            seekShow.Start();
            long t0 = clock.ElapsedMilliseconds;
            try { await Task.Run(() => p.SetPlayPositionAsync(to, false)); }
            catch (Exception e) { lastError = "saut : " + e.Message; }
            if (Closed || p != player) return;
            seeking = false;
            seekShow?.Stop();
            stillTicks = 0;
            lastPos = -1;
            if (!playing) HideLoader(); // en pause, l'image ne repartira pas d'elle-même
            seekTimes.Add(clock.ElapsedMilliseconds - t0);
            Probe.Log("player", $"saut vers {to} ms en {clock.ElapsedMilliseconds - t0} ms");
            positionMs = to;
            Anchor(to);
            SetSubText(null);
            if (pendingSeek == to) pendingSeek = -1;
            else guard.Run("SeekGo", () => SeekGo()); // d'autres appuis sont arrivés pendant le saut
            reporter?.Progress("TimeUpdate", positionMs, !playing, AudioIndex(), SubtitleIndex());
            if (controlsShown) Refresh();
        }

        // Autre version choisie pendant la lecture : arrêt signalé sur l'ancienne, puis lecture de
        // la nouvelle à la même position.
        void SwitchTo(Source next, bool initial = false)
        {
            if (next == null || next == source) return;
            int pos = pendingSeek >= 0 ? pendingSeek : positionMs;
            if (initial && pos <= 0) pos = (int)startMs; // rien n'a été lu : point de reprise demandé
            Probe.Log("player", $"changement de version à {pos} ms");
            reporter?.Stop(pos, AudioIndex(), SubtitleIndex());
            reporter = null;
            ticker?.Stop();
            seekTimer?.Stop();
            pendingSeek = -1;
            seeking = false;
            subtitleChoice = -1;
            subtitleTouched = false;
            StopSync();
            cues = null;
            syncStatus = null;
            refTrack = refHeardFor = -1;
            heardLines.Clear();
            SetSubText(null);
            Release();
            source = next;
            videoInfo = "-";
            Open(pos, initial);
        }

        void StopReport()
        {
            if (reporter == null) return;
            int pos = ended && durationMs > 0 ? durationMs : positionMs;
            Playback.LastStop = reporter.Stop(pos, AudioIndex(), SubtitleIndex());
            Playback.LastItem = target;
            // Page du titre : même règle que Jellyfin (au-delà de 90 % le titre est vu, en deçà
            // de 5 % la position n'est pas gardée).
            Playback.LastItemId = target.Id;
            bool played = ended || (durationMs > 0 && pos >= durationMs * 0.9);
            Playback.LastPlayed = played;
            Playback.LastPositionTicks = played || (durationMs > 0 && pos < durationMs * 0.05) ? 0 : pos * Playback.TicksPerMs;
        }

        void Release()
        {
            Player p = player;
            player = null;
            if (p != null) Task.Run(() => Dispose(p));
        }

        static void Dispose(Player p)
        {
            if (p == null) return;
            try { p.Stop(); } catch (Exception) { }
            try { p.Unprepare(); } catch (Exception) { }
            try { p.Dispose(); } catch (Exception) { }
        }

        // ---- Arrière-plan (touche Home, TV éteinte) ----

        public override void OnAppPause()
        {
            guard.Run("AppPause", () =>
            {
                if (IsPlaying()) { try { player.Pause(); } catch (Exception) { } playing = false; }
                reporter?.Progress("Pause", positionMs, true, AudioIndex(), SubtitleIndex());
            });
        }

        public override void OnAppResume()
        {
            guard.Run("AppResume", () =>
            {
                PlayerState st = PlayerState.Idle;
                try { if (player != null) st = player.State; } catch (Exception) { }
                Probe.Log("player", "retour, état " + st + (interrupted ? ", interrompu" : ""));
                // Lecteur libéré par le système pendant l'absence : on rouvre à la dernière position.
                if (!failed && (interrupted || player == null || st == PlayerState.Idle))
                {
                    ticker?.Stop();
                    Release();
                    Open(positionMs, false);
                    return;
                }
                ShowControls();
            });
        }

        public override void OnAppTerminate()
        {
            if (reporter == null) return;
            int pos = positionMs;
            try { reporter.Stop(pos, AudioIndex(), SubtitleIndex()).Wait(2000); } catch (Exception) { }
        }

        // ---- Pistes ----

        // Pistes du serveur pour un type, dans l'ordre du fichier (sans les sous-titres externes).
        List<MediaStream> ServerTracks(string type)
        {
            var l = new List<MediaStream>();
            foreach (MediaStream m in source.MediaStreams ?? new MediaStream[0])
                if (m.Type == type && !m.IsExternal) l.Add(m);
            return l;
        }

        int? AudioIndex()
        {
            try
            {
                if (player == null) return null;
                PlayerTrackInfo a = player.AudioTrackInfo;
                List<MediaStream> server = ServerTracks("Audio");
                int n = a.GetCount();
                return server.Count == n && a.Selected >= 0 && a.Selected < n ? server[a.Selected].Index : (int?)null;
            }
            catch (Exception) { return null; }
        }

        int? SubtitleIndex()
        {
            if (subtitleChoice < 0) return -1;
            if (subtitleChoice >= ServerBase) return CurrentServerSub()?.Index;
            List<MediaStream> server = ServerTracks("Subtitle");
            return subtitleChoice < server.Count ? server[subtitleChoice].Index : (int?)null;
        }

        // ---- Panneau (pistes, versions) ----

        void OpenPanel(string kind)
        {
            int keep = keepFocus;
            keepFocus = -1;
            if (player == null && kind != "versions") return;
            if (kind != panelKind) othersOpen = false;
            panelKind = kind;
            panelItems = new List<PanelItem>();
            panelNoteText = null;
            wantFocus = -1;
            int current = 0;
            if (kind == "versions")
            {
                menu.Close();
                tip.Hide();
                hideTimer?.Stop();
                OpenDrawer();
                return;
            }
            {
                if (kind == "stats")
                {
                    // Comme la version web : Désactivées, Lecture, Cache, Pistes.
                    for (int i = 0; i < 4; i++) panelItems.Add(new PanelItem { Name = Lang.T("player.stats_item" + i), Value = i });
                    current = statsPage;
                    panelTitleText = Lang.T("player.stats_title");
                }
                else if (kind == "audio")
                {
                    try
                    {
                        PlayerTrackInfo a = player.AudioTrackInfo;
                        int n = a.GetCount();
                        for (int i = 0; i < n; i++) panelItems.Add(TrackItem("Audio", i, n, a.GetLanguageCode));
                        current = a.Selected;
                    }
                    catch (Exception e) { lastError = "pistes : " + e.Message; }
                    panelTitleText = Lang.T("player.audio");
                }
                else if (kind == "subtitles")
                {
                    SubtitleItems();
                    current = subtitleChoice;
                    panelTitleText = Lang.T("player.subtitles");
                }
                else
                {
                    current = int.MinValue;
                    SubPanelItems(kind);
                }
                panelSelected = current;
                int at = panelItems.FindIndex(x => x.Value == current);
                panelFocus = keep >= 0 ? Math.Min(keep, Math.Max(0, panelItems.Count - 1)) : wantFocus >= 0 ? wantFocus : Math.Max(0, at);
                // Réglages des sous-titres : au-dessus du bouton Sous-titres, comme les autres menus
                // (demande de l'auteur, 2026-11-10 : en haut de l'écran, le panneau semblait mal placé).
                if (IsTop(kind)) Popover(kind == "lines" ? LinesW : SubPanelW, Btn.Subtitles);
                else Popover(kind == "stats" ? StatsW : MenuWidth(), kind == "audio" ? Btn.Audio : kind == "stats" ? Btn.Stats : Btn.Subtitles);
            }
            ShowPanel();
            tip.Hide();
            hideTimer?.Stop();
            SetSubText(realText); // exemple de sous-titre montré ou retiré (Style)
            ApplySubStyle(true);  // hauteur réelle pendant le réglage Style, relevée sinon
        }

        // Sous-titres : Désactivés, ceux de la langue de l'interface, puis les autres repliés sous
        // « Autres langues (n) » (comme la version web). La piste choisie reste visible.
        void SubtitleItems()
        {
            panelItems.Add(new PanelItem { Name = Lang.T("player.subtitles_off"), Value = OffValue });
            var mine = new List<PanelItem>();
            var others = new List<PanelItem>();
            try
            {
                PlayerTrackInfo t = player.SubtitleTrackInfo;
                int n = t.GetCount();
                for (int i = 0; i < n; i++)
                {
                    PanelItem it = TrackItem("Subtitle", i, n, t.GetLanguageCode, out string code);
                    if (Mine(code, i)) mine.Add(it); else others.Add(it);
                }
            }
            catch (Exception e) { lastError = "pistes : " + e.Message; }
            // Pistes du serveur (fichiers à part), après celles du fichier.
            List<MediaStream> subs = ServerSubs();
            for (int k = 0; k < subs.Count; k++)
            {
                MediaStream m = subs[k];
                string name = LanguageName(m.Language) ?? m.DisplayTitle ?? m.Title ?? Lang.T("player.track", k + 1);
                string detail = !string.IsNullOrEmpty(m.Title) && m.Title != name ? m.Title + " · " + Lang.T("sub.server") : Lang.T("sub.server");
                var it = new PanelItem { Name = name, Detail = detail, Value = ServerBase + k };
                if (Mine(m.Language, ServerBase + k)) mine.Add(it); else others.Add(it);
            }
            panelItems.AddRange(mine);
            if (others.Count > 0)
            {
                if (mine.Count == 0 || othersOpen) panelItems.AddRange(others);
                else panelItems.Add(new PanelItem { Name = Lang.T("player.other_languages", others.Count), Value = OthersValue });
            }
            // Comme la version web : Synchronisation (piste du serveur affichée) et Style.
            if (subtitleChoice >= ServerBase)
            {
                string status = SyncStatusText();
                panelItems.Add(new PanelItem { Name = Lang.T("sub.sync"), Detail = DelayLabel() + (status != null ? " · " + status : ""), Value = ActionValue, Sub = true, Choose = () => OpenPanel("sync") });
            }
            panelItems.Add(new PanelItem { Name = Lang.T("sub.style"), Detail = Lang.T("sub.size" + SubSizeIndex) + " · " + Prefs.SubtitlePosition + " %", Value = ActionValue, Sub = true, Choose = () => OpenPanel("style") });
        }

        // Montrée sans déplier « Autres langues » (web : splitByLanguage) : tout si « Lister
        // uniquement mes langues » est désactivé ou s'il n'y a pas de langue choisie ; sinon les
        // pistes dans ces langues, celles sans langue et celle affichée.
        bool Mine(string code, int value)
        {
            if (Prefs.SubtitleOnlyMineKnown && !Prefs.SubtitleOnlyMine) return true;
            List<string> langs = Prefs.SubtitleLanguages;
            if (langs.Count == 0 || value == subtitleChoice || Languages.Real(code) == null) return true;
            return langs.Exists(l => Languages.Same(l, code));
        }

        PanelItem TrackItem(string type, int i, int count, Func<int, string> code) => TrackItem(type, i, count, code, out _);

        // Nom : la langue ; détail : le titre de la piste donné par le serveur (« BTM DDP5.1 FR »),
        // quand le nombre de pistes du serveur et du lecteur concorde.
        PanelItem TrackItem(string type, int i, int count, Func<int, string> code, out string lang)
        {
            lang = null;
            try { lang = code(i); } catch (Exception) { }
            List<MediaStream> server = ServerTracks(type);
            MediaStream m = server.Count == count ? server[i] : null;
            if (m != null && !string.IsNullOrEmpty(m.Language)) lang = m.Language;
            string name = LanguageName(lang);
            string detail = m?.Title;
            if (name == null) { name = m?.DisplayTitle ?? (string.IsNullOrEmpty(lang) ? Lang.T("player.track", i + 1) : lang); if (detail == name) detail = null; }
            return new PanelItem { Name = name, Detail = string.IsNullOrEmpty(detail) ? null : detail, Value = i };
        }

        static string LanguageName(string code) => Languages.Name(code);

        // ---- Piste audio selon les réglages (Core/AudioPick.cs) ----

        class AudioInputs
        {
            public List<string> Languages, Order;
            public bool PreferDefault;
            public string ShowPick;
        }

        // Réglages lus sur le fil d'interface (Prefs n'est pas fait pour être lu ailleurs).
        AudioInputs ReadAudioInputs()
        {
            var a = new AudioInputs { Order = Prefs.AudioTypes };
            a.Languages = AudioPick.Languages(SeriesOf(target), out a.PreferDefault, out a.ShowPick);
            return a;
        }

        static string SeriesOf(Item it) => it != null && it.Type == "Episode" ? it.SeriesId : null;

        // Pistes audio du lecteur, décrites par le serveur (langue, titre, par défaut) quand les
        // nombres de pistes concordent ; sinon la seule langue donnée par le lecteur.
        List<AudioPick.Track> AudioTracks(Player p)
        {
            var l = new List<AudioPick.Track>();
            PlayerTrackInfo a = p.AudioTrackInfo;
            int n = a.GetCount();
            List<MediaStream> server = ServerTracks("Audio");
            for (int i = 0; i < n; i++)
            {
                MediaStream m = server.Count == n ? server[i] : null;
                string lang = null;
                try { lang = a.GetLanguageCode(i); } catch (Exception) { }
                if (m != null && !string.IsNullOrEmpty(m.Language)) lang = m.Language;
                l.Add(new AudioPick.Track { Index = i, Lang = lang, Title = m?.Title ?? m?.DisplayTitle, IsDefault = m != null && m.IsDefault });
            }
            return l;
        }

        // Hors du fil d'interface (appels au lecteur). Renvoie la piste à poser après le
        // démarrage si le lecteur l'a refusée avant, sinon -1.
        int ChooseAudio(Player p, AudioInputs inputs)
        {
            long t0 = clock.ElapsedMilliseconds;
            AudioPick.Track t;
            int cur;
            string log, why;
            try
            {
                List<AudioPick.Track> tracks = AudioTracks(p);
                cur = p.AudioTrackInfo.Selected;
                List<string> codes = AudioPick.Codes(inputs.Languages, source);
                t = AudioPick.Choose(tracks, cur, codes, inputs.PreferDefault, inputs.Order, out why);
                var langs = new List<string>();
                foreach (AudioPick.Track x in tracks) langs.Add((x.IsDefault ? "*" : "") + (x.Lang ?? "?"));
                log = $"{tracks.Count} pistes [{string.Join(" ", langs)}] ({ServerTracks("Audio").Count} au serveur), lecteur {cur}, "
                    + $"langues [{string.Join(", ", codes)}]{(inputs.ShowPick != null ? " (série : " + inputs.ShowPick + ")" : "")}"
                    + (inputs.PreferDefault ? ", défaut d'abord" : "");
            }
            catch (Exception e) { Probe.Log("audio", "pistes illisibles : " + e.Message); return -1; }
            if (t == null || t.Index == cur) { Probe.Log("audio", log + $" : piste {cur} gardée ({why})"); return -1; }
            try
            {
                p.AudioTrackInfo.Selected = t.Index;
                Probe.Log("audio", log + $" : piste {t.Index} ({why}), posée avant le démarrage en {clock.ElapsedMilliseconds - t0} ms");
                return -1;
            }
            catch (Exception e)
            {
                Probe.Log("audio", log + $" : piste {t.Index} ({why}) refusée avant le démarrage : " + e.Message);
                return t.Index;
            }
        }

        // Piste choisie à la main sur un épisode : sa langue devient celle de la série (compte,
        // comme le web), sauf si c'est celle que les réglages auraient prise (choix oublié).
        void RememberAudio(int index)
        {
            string series = SeriesOf(target);
            if (series == null || !Prefs.RememberAudio || player == null) return;
            try
            {
                List<AudioPick.Track> tracks = AudioTracks(player);
                string lang = Languages.Real(tracks.Find(x => x.Index == index)?.Lang);
                if (lang == null) return;
                string settings = AudioPick.SettingsLanguage(tracks, source);
                Prefs.SetShowAudio(series, Languages.Same(lang, settings) ? null : lang);
            }
            catch (Exception e) { lastError = "mémoire audio : " + e.Message; }
        }

        // ---- Sous-titres selon les réglages (Core/SubtitlePick.cs) ----

        // Détails du serveur pour les pistes de sous-titres du fichier, quand on sait les faire
        // correspondre à celles du lecteur (toutes, ou celles en texte) ; sinon null.
        List<MediaStream> FileSubFacts(int n)
        {
            List<MediaStream> server = ServerTracks("Subtitle");
            if (server.Count == n) return server;
            List<MediaStream> text = server.FindAll(x => !IsPicture(x.Codec));
            return text.Count == n ? text : null;
        }

        // Pistes du fichier (sans celles en image, sauf pictures). Appels au lecteur.
        List<SubtitlePick.Track> FileSubTracks(Player p, bool pictures)
        {
            var l = new List<SubtitlePick.Track>();
            PlayerTrackInfo t = p.SubtitleTrackInfo;
            int n = t.GetCount();
            List<MediaStream> facts = FileSubFacts(n);
            for (int i = 0; i < n; i++)
            {
                MediaStream m = facts?[i];
                if (!pictures && m != null && IsPicture(m.Codec)) continue;
                string lang = m?.Language;
                if (string.IsNullOrEmpty(lang)) { try { lang = t.GetLanguageCode(i); } catch (Exception) { } }
                l.Add(new SubtitlePick.Track { Value = i, Lang = lang, Title = m?.Title ?? m?.DisplayTitle, Forced = m?.IsForced ?? false, Sdh = m?.IsHearingImpaired ?? false });
            }
            return l;
        }

        List<SubtitlePick.Track> ServerSubTracks()
        {
            var l = new List<SubtitlePick.Track>();
            List<MediaStream> subs = ServerSubs();
            for (int k = 0; k < subs.Count; k++)
            {
                MediaStream m = subs[k];
                l.Add(new SubtitlePick.Track { Value = ServerBase + k, Server = true, Lang = m.Language, Title = m.Title ?? m.DisplayTitle, Forced = m.IsForced, Sdh = m.IsHearingImpaired });
            }
            return l;
        }

        static string Describe(SubtitlePick.Track t)
        {
            string type = SubtitlePick.TypeOf(t);
            return (t.Lang ?? "?") + (type != "normal" ? "/" + type : "");
        }

        // Hors du fil d'interface (appels au lecteur) : la valeur du menu à montrer, -1 : aucune.
        // audioIndex : piste audio choisie, -1 pour celle du lecteur.
        int PickSubtitle(Player p, SubtitlePick.Inputs inputs, int audioIndex)
        {
            try
            {
                List<SubtitlePick.Track> file = FileSubTracks(p, false);
                List<SubtitlePick.Track> server = ServerSubTracks();
                int a = audioIndex >= 0 ? audioIndex : p.AudioTrackInfo.Selected;
                string audioLang = AudioTracks(p).Find(x => x.Index == a)?.Lang;
                SubtitlePick.Track t = SubtitlePick.Choose(file, server, audioLang, inputs, out string why);
                Probe.Log("subs", $"choix : mode {inputs.Mode}, langues [{string.Join(", ", inputs.Languages)}]"
                    + (inputs.ShowPick != null ? $" (série : {inputs.ShowPick})" : "")
                    + $", audio {audioLang ?? "?"}, fichier [{string.Join(" ", file.ConvertAll(Describe))}], {server.Count} au serveur : "
                    + (t == null ? "aucun" : (t.Server ? "serveur " : "piste ") + (t.Server ? t.Value - ServerBase : t.Value) + " " + Describe(t)) + $" ({why})");
                return t?.Value ?? OffValue;
            }
            catch (Exception e) { Probe.Log("subs", "choix impossible : " + e.Message); return OffValue; }
        }

        // Choix automatique appliqué, sauf si l'utilisateur a choisi dans le menu. Avant le
        // démarrage, une piste du fichier est posée par ReapplySubtitle juste après.
        void ApplySubPick(int value, bool beforeStart = true)
        {
            if (subtitleTouched || value == subtitleChoice) return;
            if (beforeStart && value >= 0 && value < ServerBase) { subtitleChoice = value; return; }
            ChooseSubtitle(value);
            if (panelKind == "subtitles") RefreshPanel();
        }

        // Sous-titres du serveur arrivés après le démarrage : choix refait.
        async void RepickSubtitle()
        {
            Player p = player;
            if (p == null || subtitleTouched) return;
            SubtitlePick.Inputs inputs = SubtitlePick.Read(SeriesOf(target));
            int v = await Task.Run(() => PickSubtitle(p, inputs, -1));
            if (Closed || p != player) return;
            guard.Run("SubRepick", () => ApplySubPick(v, false));
        }

        // Sous-titres choisis ou coupés à la main sur un épisode : gardés pour la série (compte,
        // comme le web), sauf si c'est ce que les réglages auraient montré (choix oublié).
        void RememberSubtitle(int value)
        {
            string series = SeriesOf(target);
            if (series == null || !Prefs.RememberSubtitles || player == null) return;
            try
            {
                List<SubtitlePick.Track> all = FileSubTracks(player, true);
                all.AddRange(ServerSubTracks());
                SubtitlePick.Track t = value >= 0 ? all.Find(x => x.Value == value) : null;
                if (value >= 0 && t == null) return;
                string pick = SubtitlePick.ShowPickFor(t, all, out bool keep);
                if (keep) Prefs.SetShowSubtitle(series, pick);
            }
            catch (Exception e) { lastError = "mémoire sous-titres : " + e.Message; }
        }

        int panelW = PanelW;

        // Largeur d'un menu de pistes d'après son plus long texte (estimée : 10 px par caractère à
        // 18 pt, 8,5 à 15 pt), comme le web qui s'ajuste entre 12 et 22 rem.
        int MenuWidth()
        {
            int text = 0;
            foreach (PanelItem it in panelItems)
            {
                text = Math.Max(text, (it.Name?.Length ?? 0) * 10 + (it.Sub ? 32 : 0));
                text = Math.Max(text, (int)((it.Detail?.Length ?? 0) * 8.5));
            }
            text = Math.Max(text, (panelTitleText?.Length ?? 0) * 9);
            return Math.Max(MenuMinW, Math.Min(PanelW, 12 + 48 + text + 24 + 12));
        }

        // Bord droit du menu sur celui du bouton, comme le web ; le bas est posé par ShowPanel.
        void Popover(int w, Btn anchor)
        {
            panelW = w;
            int b = Array.IndexOf(BtnOrder, anchor);
            int x = (int)btnBox[b].PositionX + BtnD - w;
            panelX = Math.Max(Margin, Math.Min(1920 - Margin - w, x));
        }

        // Menu sur la boîte de choix commune (Ui/Picker) : ✓ à gauche pour le choix en cours, nom
        // 18 pt, détail 15 pt gris dessous, › à droite si elle ouvre un autre menu ; fond blanc
        // qui glisse d'une ligne à l'autre. Rappelé après un réglage : focus gardé.
        void ShowPanel()
        {
            if (panelKind == "versions") return; // tiroir à part (OpenDrawer)
            var items = new List<Picker.Item>();
            int selected = -1;
            for (int i = 0; i < panelItems.Count; i++)
            {
                PanelItem it = panelItems[i];
                items.Add(new Picker.Item { Text = it.Name, Detail = it.Detail, Sub = it.Sub, Unfold = it.Value == OthersValue });
                if (it.Value == panelSelected && it.Value != OthersValue) selected = i;
            }
            if (items.Count == 0) items.Add(new Picker.Item { Text = Lang.T("player.no_tracks"), Muted = true });
            // Bas du menu au-dessus de la ligne des temps (on devinait « Se termine à » au travers).
            menu.OpenAbove(items, selected, panelFocus, panelX, panelW, BarY - 64, panelTitleText, panelNoteText, RowsShown);
            panelFocus = panelItems.Count > 0 ? menu.Focus : 0;
        }

        static string VersionName(Source s)
        {
            string name = s.Aio?.Name;
            if (string.IsNullOrEmpty(name)) name = s.Name ?? "";
            return name.Trim();
        }

        // ---- Tiroir des versions ----

        // Créé à chaque ouverture (la version en cours a pu changer), ouvert en fondu de 0,2 s comme
        // la liste des liens. Titre « N versions » en haut, posé par-dessus la liste (les cartes
        // glissent dessous), curseur de défilement dans la marge de droite.
        void OpenDrawer()
        {
            CloseDrawer(false);
            drawer = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.Veil, Opacity = 0f };
            Color back = Theme.Background;
            drawer.Add(new View { Position = new Position(DrawerX, DrawerY), Size = new Size(DrawerW, DrawerH), BackgroundColor = back }.Rounded(Theme.RadiusLarge));
            const int headH = 36, listTop = DrawerY + DrawerPad + headH + 24;
            int listH = DrawerY + DrawerH - 24 - listTop;
            vlist = new VersionList(drawer, DrawerX + DrawerPad, DrawerW - 2 * DrawerPad, listTop, listH, DrawerY, DrawerY + DrawerH, DrawerX + DrawerW - 14, guard, () => Closed || drawer == null);
            vlist.Current = source;
            // En-tête par-dessus la liste, arrêté 14 px au-dessus (contour de la carte grossie) :
            // coins du haut arrondis comme le tiroir, bas droit.
            int coverH = listTop - 14 - DrawerY;
            drawer.Add(new View { Position = new Position(DrawerX, DrawerY), Size = new Size(DrawerW, coverH), BackgroundColor = back }.Rounded(Theme.RadiusLarge));
            drawer.Add(new View { Position = new Position(DrawerX, DrawerY + Theme.RadiusLarge), Size = new Size(DrawerW, coverH - Theme.RadiusLarge), BackgroundColor = back });
            var heading = new TextLabel { Position = new Position(DrawerX + DrawerPad, DrawerY + DrawerPad), Size = new Size(DrawerW - 2 * DrawerPad, headH), PointSize = Theme.SizeHeading, TextColor = Color.White, FontStyle = Theme.SemiBold(), VerticalAlignment = VerticalAlignment.Center };
            drawer.Add(heading);
            vlist.Place(allSources ?? versions.ToArray());
            heading.Text = Lang.T(vlist.Versions == 1 ? "versions.one" : "versions.count", vlist.Versions);
            root.Add(drawer);
            vlist.Start();
            drawerAnim?.Stop();
            drawerAnim = new Animation(Theme.Slide);
            drawerAnim.AnimateTo(drawer, "Opacity", 1f);
            drawerAnim.Play();
        }

        // Fermé en fondu de 0,2 s (fade), puis détruit.
        void CloseDrawer(bool fade)
        {
            if (drawer == null) return;
            View d = drawer;
            drawer = null;
            vlist?.Stop();
            vlist = null;
            drawerAnim?.Stop();
            if (!fade) { d.Unparent(); d.Dispose(); return; }
            drawerAnim = new Animation(Theme.Slide);
            drawerAnim.AnimateTo(d, "Opacity", 0f);
            drawerAnim.Finished += (s, e) => { d.Unparent(); d.Dispose(); };
            drawerAnim.Play();
        }

        void PanelChoose()
        {
            if (panelKind == "versions")
            {
                Source next = vlist?.Focused;
                panelKind = null;
                CloseDrawer(true);
                if (next != null && next != source) SwitchTo(next); else ShowControls();
                return;
            }
            if (panelItems.Count == 0) return;
            PanelItem it = panelItems[panelFocus];
            if (it.Choose != null)
            {
                string k = panelKind;
                it.Choose();
                if (panelKind != null && panelKind == k) RefreshPanel();
                return;
            }
            if (panelKind == "stats")
            {
                statsPage = it.Value;
                if (statsPage > 0) { stats.Text = StatsText(); stats.Show(); } else stats.Hide();
                ClosePanel();
                return;
            }
            if (it.Value == OthersValue)
            {
                // « Autres langues » : la liste se déplie, le focus reste à cet endroit.
                keepFocus = panelFocus;
                othersOpen = true;
                OpenPanel("subtitles");
                return;
            }
            try
            {
                if (panelKind == "audio")
                {
                    player.AudioTrackInfo.Selected = it.Value;
                    reporter?.Progress("AudioTrackChange", positionMs, !playing, AudioIndex(), SubtitleIndex());
                    RememberAudio(it.Value);
                }
                else
                {
                    subtitleTouched = true;
                    ChooseSubtitle(it.Value);
                    RememberSubtitle(it.Value);
                }
                panelSelected = it.Value;
            }
            catch (Exception e) { lastError = "piste : " + e.Message; }
            ClosePanel();
        }

        void ClosePanel()
        {
            panelKind = null;
            menu.Close();
            CloseDrawer(true);
            SetSubText(realText);
            ApplySubStyle(true); // réglage Style fermé : de nouveau au-dessus de la barre
            ShowControls();
        }

        // Liste reconstruite (après un réglage), focus gardé.
        void RefreshPanel()
        {
            keepFocus = panelFocus;
            OpenPanel(panelKind);
        }

        // ---- Sous-titres affichés par l'application (module 6) ----
        // Pistes du fichier : texte envoyé par le lecteur, affiché tel quel. Pistes du serveur :
        // fichier téléchargé, répliques affichées d'après la position de lecture et le décalage.
        // Recalage automatique : une piste texte du fichier sert de référence ; le lecteur
        // l'envoie, l'application ne l'affiche pas.

        const int ServerBase = 1000, ActionValue = -100;
        const int SubH = 480, SubLiftPct = 19;
        const int SubPanelW = 600, LinesW = 980;
        static readonly string[] PictureCodecs = { "pgssub", "hdmv_pgs_subtitle", "pgs", "dvdsub", "dvd_subtitle", "dvbsub", "dvb_subtitle", "vobsub" };

        List<Cue> cues;              // piste du serveur affichée (temps recalés à la vitesse de la vidéo)
        int subDelay;                // décalage en ms (positif : sous-titres plus tard)
        bool retimed, syncAsked, syncFirst, syncBusy, lifted;
        string syncStatus;           // null, hand, synced, retimed, unmatched
        SubtitleSync sync;
        Align.Curve curveA, curveB;  // courbes du recalage, créées une fois (près d'1 Mo chacune)
        List<Span> syncPending = new List<Span>();
        readonly List<Span> heardLines = new List<Span>(); // répliques de la référence reçues
        int refTrack = -1, refHeardFor = -1;
        string realText, shownText, loadError, panelNoteText, earNote;
        int anchorPos, heardAt = -1, sawAt = -1, lineHeardAt, wantFocus = -1;
        long anchorAt = -1, cuesLoadMs = -1;
        int syncTries;
        Animation liftAnim;

        // Panneaux de réglage des sous-titres (Retour ramène au panneau précédent).
        static bool IsTop(string kind) => kind == "sync" || kind == "style" || kind == "ear" || kind == "lines";

        // Horloge des sous-titres : la position n'est lue que toutes les 500 ms (Tick) ; entre deux
        // lectures, elle avance avec le temps écoulé pendant la lecture.
        void Anchor(int pos) { anchorPos = pos; anchorAt = clock.ElapsedMilliseconds; }

        int PosAt(long t) => playing && !seeking && anchorAt >= 0 ? anchorPos + (int)((t - anchorAt) * speed) : anchorPos;

        int Now() => PosAt(clock.ElapsedMilliseconds);

        // Pistes de sous-titres du serveur à part du fichier (comme la version web).
        List<MediaStream> ServerSubs()
        {
            var l = new List<MediaStream>();
            foreach (MediaStream m in source.MediaStreams ?? new MediaStream[0])
                if (m.Type == "Subtitle" && m.IsExternal && m.DeliveryMethod == "External" && !string.IsNullOrEmpty(m.DeliveryUrl)) l.Add(m);
            return l;
        }

        MediaStream CurrentServerSub()
        {
            if (subtitleChoice < ServerBase) return null;
            List<MediaStream> l = ServerSubs();
            int k = subtitleChoice - ServerBase;
            return k < l.Count ? l[k] : null;
        }

        // Sous-titres des addons du serveur : demandés pour la version lue, une fois, après le
        // démarrage (la liste des versions ne les contient pas toujours ; le web fait de même).
        // Ajoutés aux pistes de la version ; le menu Sous-titres se met à jour s'il est ouvert.
        async void AskSubtitles(Source s)
        {
            if (s.SubtitlesAsked) return;
            s.SubtitlesAsked = true;
            long t0 = clock.ElapsedMilliseconds;
            PlaybackInfo info = null;
            try { info = await Library.Version(target.Id, s); }
            catch (Exception e) { Probe.Log("subs", "sous-titres des addons non reçus : " + (e is ApiError a ? "HTTP " + a.Status : e.GetType().Name)); s.SubtitlesAsked = false; return; }
            if (Closed) return;
            guard.Run("AskSubtitles", () =>
            {
                Source got = null;
                foreach (Source x in info?.MediaSources ?? new Source[0])
                    if (got == null || (x.Aio?.Id ?? x.Id) == (s.Aio?.Id ?? s.Id)) got = x;
                var streams = new List<MediaStream>(s.MediaStreams ?? new MediaStream[0]);
                int added = 0;
                foreach (MediaStream m in got?.MediaStreams ?? new MediaStream[0])
                {
                    if (m.Type != "Subtitle" || !m.IsExternal) continue;
                    if (streams.Exists(x => x.Type == "Subtitle" && x.IsExternal && (x.Index == m.Index || x.DeliveryUrl == m.DeliveryUrl))) continue;
                    streams.Add(m);
                    added++;
                }
                s.MediaStreams = streams.ToArray();
                Probe.Log("subs", $"{added} sous-titres des addons ajoutés en {clock.ElapsedMilliseconds - t0} ms");
                if (s == source && added > 0) RepickSubtitle();
                if (s == source && panelKind == "subtitles") RefreshPanel();
            });
        }

        static string SubKey(MediaStream m) => "server:" + m.Index;

        static bool IsPicture(string codec) => codec != null && Array.IndexOf(PictureCodecs, codec.ToLowerInvariant()) >= 0;

        void ChooseSubtitle(int value)
        {
            subtitleChoice = value;
            StopSync();
            cues = null;
            retimed = false;
            syncStatus = null;
            loadError = null;
            subDelay = 0;
            cuesLoadMs = -1;
            heardAt = sawAt = -1;
            subtitleHide?.Stop();
            SetSubText(null);
            if (value >= 0 && value < ServerBase) { refTrack = -1; player.SubtitleTrackInfo.Selected = value; }
            else if (value >= ServerBase) LoadServerSub(value);
            else refTrack = -1;
            reporter?.Progress("SubtitleTrackChange", positionMs, !playing, AudioIndex(), SubtitleIndex());
        }

        // Piste du fichier posée juste après le démarrage : sans effet visible (l'auteur, 2026-11-21 :
        // aucune réplique affichée jusqu'à un changement de piste à la main, puis retour sur la
        // même ; journal : bonne piste au lecteur et 2 répliques déjà reçues à la première image).
        // À la première image, on fait donc comme à la main : une autre piste, puis la bonne.
        // Une minute après, le journal dit combien de répliques avec du texte sont arrivées.
        int subTexts; // répliques non vides reçues du lecteur
        Timer subCheck; // gardé dans un champ, sinon il s'arrête

        void RecheckSubtitle()
        {
            if (subtitleTouched || subtitleChoice < 0 || subtitleChoice >= ServerBase) return;
            try
            {
                PlayerTrackInfo t = player.SubtitleTrackInfo;
                int n = t.GetCount(), cur = t.Selected;
                Probe.Log("subs", $"première image : piste {cur} au lecteur sur {n}, {subtitleChoice} voulue, {subtitleEvents} répliques reçues ({subTexts} avec texte) ; piste reposée");
                if (n > 1) t.Selected = subtitleChoice == 0 ? 1 : 0;
                t.Selected = subtitleChoice;
            }
            catch (Exception e) { Probe.Log("subs", "piste reposée refusée : " + e.Message); }
            int before = subTexts;
            subCheck = new Timer(60000);
            subCheck.Tick += (s, e) => { if (!Closed) Probe.Log("subs", $"une minute après : {subTexts - before} répliques avec texte, piste voulue {subtitleChoice}"); return false; };
            subCheck.Start();
        }

        // Après une réouverture du lecteur (retour d'arrière-plan) : piste remise.
        void ReapplySubtitle()
        {
            try
            {
                if (subtitleChoice >= 0 && subtitleChoice < ServerBase) player.SubtitleTrackInfo.Selected = subtitleChoice;
                else if (subtitleChoice >= ServerBase && refTrack >= 0) player.SubtitleTrackInfo.Selected = refTrack;
            }
            catch (Exception e) { lastError = "piste : " + e.Message; }
        }

        async void LoadServerSub(int choice)
        {
            MediaStream m = CurrentServerSub();
            if (m == null) return;
            Subtitles.Saved? saved = Subtitles.SavedDelay(source.Id, SubKey(m));
            subDelay = saved?.Ms ?? 0;
            if (saved?.Fixed == true) syncStatus = "hand";
            long t0 = clock.ElapsedMilliseconds;
            List<Cue> got = null;
            try
            {
                byte[] data = await Session.Client.GetFile(m.DeliveryUrl);
                got = await Task.Run(() => Subtitles.Parse(Subtitles.Decode(data)));
            }
            catch (Exception e) { loadError = e is ApiError a ? "HTTP " + a.Status : e.GetType().Name; }
            if (Closed || subtitleChoice != choice) return;
            guard.Run("SubLoaded", () =>
            {
                cuesLoadMs = clock.ElapsedMilliseconds - t0;
                if (got == null) { Notice(Lang.T("sub.notice_load_failed", loadError), 5000); Probe.Log("subs", "échec du chargement : " + loadError); return; }
                if (got.Count == 0) { loadError = Lang.T("sub.empty"); Notice(Lang.T("sub.notice_empty"), 5000); Probe.Log("subs", "fichier sans réplique"); return; }
                cues = got;
                Probe.Log("subs", $"{got.Count} répliques chargées en {cuesLoadMs} ms, décalage mémorisé {subDelay} ms" + (syncStatus == "hand" ? " (à la main)" : ""));
                if (subTimer == null)
                {
                    subTimer = new Timer(100);
                    subTimer.Tick += (s, e) => { guard.Run("SubTick", SubTick); return !Closed; };
                    subTimer.Start();
                }
                StartAutoSync(false);
                if (panelKind != null) RefreshPanel();
            });
        }

        // Toutes les 100 ms : réplique(s) à montrer pour une piste du serveur.
        void SubTick()
        {
            if (cues == null || subtitleChoice < ServerBase) return;
            if (seeking) { SetSubText(null); return; }
            SetSubText(CueText(Now() - subDelay));
        }

        // Dernière réplique commencée à « at » (recherche par moitiés), -1 s'il n'y en a pas.
        int CueAt(int at)
        {
            int lo = 0, hi = cues.Count - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (cues[mid].Start <= at) { idx = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return idx;
        }

        // Répliques en cours à « at » (plusieurs peuvent se chevaucher).
        string CueText(int at)
        {
            int idx = CueAt(at);
            if (idx < 0) return null;
            string text = null;
            for (int i = Math.Max(0, idx - 30); i <= idx; i++)
                if (cues[i].End > at) text = text == null ? cues[i].Text : text + "\n" + cues[i].Text;
            return text;
        }

        // Texte posé seulement s'il change (poser un texte coûte une mise en page). Pendant le
        // réglage du style, un exemple remplace l'absence de sous-titre.
        void SetSubText(string text)
        {
            realText = text;
            string shown = string.IsNullOrEmpty(text) ? (panelKind == "style" ? Lang.T("sub.sample") : null) : text;
            if (shown == shownText) return;
            shownText = shown;
            subtitle.SetText(shown);
        }

        // Réplique envoyée par le lecteur : affichée (piste du fichier choisie) ou gardée comme
        // référence du recalage (piste du serveur affichée).
        void OnPlayerSubtitle(string text, int duration, long at)
        {
            subtitleEvents++;
            if (!string.IsNullOrEmpty(text)) subTexts++;
            if (subtitleEvents <= 6) Probe.Log("subs", $"réplique {subtitleEvents} du lecteur : {text?.Length ?? 0} caractères, {duration} ms, à {PosAt(at)} ms");
            if (subtitleChoice >= 0 && subtitleChoice < ServerBase)
            {
                SetSubText(text);
                subtitleHide?.Stop();
                if (string.IsNullOrEmpty(text)) return;
                // Un seul minuteur, relancé à chaque réplique avec sa durée (au lieu d'un nouveau
                // par réplique, ~20 par minute).
                if (subtitleHide == null)
                {
                    subtitleHide = new Timer(500);
                    subtitleHide.Tick += (s, e) => { guard.Run("SubHide", () => { if (subtitleChoice >= 0 && subtitleChoice < ServerBase) SetSubText(null); }); return false; };
                }
                subtitleHide.Stop();
                subtitleHide.Interval = (uint)Math.Max(500, duration);
                subtitleHide.Start();
                return;
            }
            if (subtitleChoice < ServerBase || refTrack < 0 || string.IsNullOrEmpty(text) || duration <= 0) return;
            int start = PosAt(at);
            var span = new Span(start, start + duration);
            if (refHeardFor == refTrack) heardLines.Add(span);
            if (sync != null && !sync.Done)
            {
                syncPending.Add(span);
                Pump();
                // Recalage demandé : le message reste affiché avec le nombre de répliques reçues
                // (il en faut ~60, soit 2 à 3 minutes de dialogue).
                if (syncAsked && syncFirst) Notice(Lang.T("sub.notice_syncing_n", heardLines.Count), 600000);
            }
        }

        // Piste du fichier qui sert de référence : texte (pas d'image), complète (ni forcée, ni
        // panneaux, ni commentaire), dans la langue du sous-titre si possible, normale avant
        // malentendants (auto-sync.ts). Les détails viennent du serveur quand on sait faire
        // correspondre ses pistes à celles du lecteur. -1 : aucune.
        int ReferenceTrack(string lang)
        {
            if (player == null) return -1;
            PlayerTrackInfo t;
            int n;
            try { t = player.SubtitleTrackInfo; n = t.GetCount(); }
            catch (Exception) { return -1; }
            List<MediaStream> facts = FileSubFacts(n);
            int best = -1, bestRank = int.MaxValue;
            for (int i = 0; i < n; i++)
            {
                MediaStream m = facts?[i];
                if (m != null && IsPicture(m.Codec)) continue;
                Subtitles.Kind kind = Subtitles.KindOf(m?.Title ?? m?.DisplayTitle, m?.IsForced ?? false, m?.IsHearingImpaired ?? false);
                if (kind != Subtitles.Kind.Normal && kind != Subtitles.Kind.Sdh) continue;
                string code = m?.Language;
                if (string.IsNullOrEmpty(code)) { try { code = t.GetLanguageCode(i); } catch (Exception) { } }
                int rank = (Subtitles.SameLanguage(lang, code) ? 0 : 2) + (kind == Subtitles.Kind.Normal ? 0 : 1);
                if (rank < bestRank) { best = i; bestRank = rank; }
            }
            return best;
        }

        bool CanAutoSync() => cues != null && ReferenceTrack(CurrentServerSub()?.Language) >= 0;

        static List<Span> Spans(List<Cue> l)
        {
            var spans = new List<Span>(l.Count);
            foreach (Cue c in l) spans.Add(new Span(c.Start, c.End));
            return spans;
        }

        // Recalage automatique : lancé au chargement d'une piste du serveur (asked : demandé dans
        // le menu, réponse attendue).
        void StartAutoSync(bool asked)
        {
            StopSync();
            MediaStream m = CurrentServerSub();
            if (m == null || cues == null || player == null) return;
            refTrack = ReferenceTrack(m.Language);
            if (refTrack < 0) { Probe.Log("subs", "pas de piste texte du fichier : pas de recalage automatique"); return; }
            if (refHeardFor != refTrack) { heardLines.Clear(); refHeardFor = refTrack; }
            try { player.SubtitleTrackInfo.Selected = refTrack; }
            catch (Exception e) { lastError = "référence : " + e.Message; refTrack = -1; return; }
            if (curveA == null) { curveA = new Align.Curve(Subtitles.DelayLimit); curveB = new Align.Curve(Subtitles.DelayLimit); }
            sync = new SubtitleSync(Spans(cues), curveA, curveB, asked);
            syncAsked = asked;
            syncFirst = true;
            syncTries = 0;
            syncPending = new List<Span>(heardLines);
            Probe.Log("subs", $"recalage automatique sur la piste {refTrack} du fichier ({heardLines.Count} répliques déjà reçues)" + (asked ? ", demandé" : ""));
            if (asked) Notice(Lang.T("sub.notice_syncing_n", heardLines.Count), 600000);
            Pump();
        }

        void StopSync()
        {
            sync = null;
            syncPending = new List<Span>();
        }

        // Essai de recalage hors du fil d'interface, un à la fois (les courbes sont partagées).
        void Pump()
        {
            if (syncBusy || sync == null || syncPending.Count == 0) return;
            List<Span> batch = syncPending;
            syncPending = new List<Span>();
            SubtitleSync s = sync;
            int pos = Now();
            syncBusy = true;
            long t0 = clock.ElapsedMilliseconds;
            Task.Run(() =>
            {
                SyncResult r = null;
                string error = null;
                int tries = s.Tries;
                try { r = s.Add(batch, pos); }
                catch (Exception e) { error = e.GetType().Name + " " + e.Message; }
                long spent = clock.ElapsedMilliseconds - t0;
                ui.Post(_ =>
                {
                    syncBusy = false;
                    if (Closed) return;
                    guard.Run("Sync", () =>
                    {
                        syncTries++;
                        if (s.Tries != tries) Probe.Log("subs", s.LastTry + $" ({spent} ms)");
                        if (error != null) { lastError = "recalage : " + error; Probe.Log("subs", "erreur du recalage : " + error); }
                        if (s == sync) TakeResult(r);
                        Pump();
                    });
                }, null);
            });
        }

        // Résultat d'un essai (comme use-player.ts et syncNotice de la version web).
        void TakeResult(SyncResult r)
        {
            if (r == null) return;
            MediaStream m = CurrentServerSub();
            if (m == null) return;
            if (r.Ratio != null)
            {
                // Autre cadence d'image : temps étirés, puis recalage du décalage sur la copie.
                bool asked = syncAsked;
                cues = Subtitles.Retime(cues, r.Ratio.Value);
                retimed = true;
                Probe.Log("subs", $"vitesse recalée, rapport {r.Ratio.Value:0.0000}");
                StartAutoSync(false);
                syncAsked = asked;
                return;
            }
            if (r.Unmatched)
            {
                syncStatus = "unmatched";
                sync = null;
                Probe.Log("subs", "ne correspond pas à la piste du fichier");
                if (syncAsked) Notice(Lang.T("sub.notice_unmatched"), 5000);
                syncAsked = false;
                return;
            }
            int ms = Subtitles.Clamp(r.Delay.Value);
            Subtitles.Saved? saved = Subtitles.SavedDelay(source.Id, SubKey(m));
            if (saved?.Fixed == true)
            {
                // Réglé à la main : le recalage automatique n'y touche pas.
                sync = null;
                Probe.Log("subs", $"recalage trouvé {ms} ms, ignoré (réglé à la main)");
                if (retimed && syncFirst) Notice(Lang.T("sub.notice_retimed"), 4000);
                return;
            }
            int moved = ms - subDelay;
            Subtitles.SaveDelay(source.Id, SubKey(m), ms, false);
            subDelay = ms;
            syncStatus = retimed ? "retimed" : "synced";
            Probe.Log("subs", $"recalé à {ms} ms (déplacé de {moved} ms)");
            string text;
            if (retimed && syncFirst) text = moved != 0 && ms != 0 ? Lang.T("sub.notice_retimed_synced", Subtitles.Amount(ms)) : Lang.T("sub.notice_retimed");
            else if (!syncAsked && Math.Abs(moved) < (syncFirst ? 200 : 1000)) text = null;
            else text = ms == 0 ? Lang.T("sub.notice_in_sync") : Lang.T("sub.notice_synced", Subtitles.Amount(ms));
            if (text != null) Notice(text, 4000);
            else if (syncAsked) HideNotice();
            syncFirst = false;
            syncAsked = false;
            if (panelKind != null) RefreshPanel();
        }

        // Décalage réglé à la main (pas à pas, à l'oreille, sur une réplique) : mémorisé, le
        // recalage automatique s'arrête.
        void SetDelayByHand(int ms, string how, bool notice = true)
        {
            MediaStream m = CurrentServerSub();
            if (m == null) return;
            Probe.Log("subs", $"réglé à la main ({how}) : {subDelay} → {Subtitles.Clamp(ms)} ms, position {Now()} ms");
            if (syncAsked) HideNotice();
            syncAsked = false;
            subDelay = Subtitles.Clamp(ms);
            Subtitles.SaveDelay(source.Id, SubKey(m), subDelay, true);
            syncStatus = "hand";
            StopSync();
            if (notice) Notice(subDelay == 0 ? Lang.T("sub.notice_in_sync") : Lang.T("sub.notice_delay", Subtitles.Amount(subDelay)), 2500);
            SubTick();
        }

        // Gauche / Droite sur Décalage : 0,1 s par appui ; touche maintenue (appuis à moins de
        // 200 ms), 0,5 s puis 1 s par appui, pour aller vite vers un grand décalage.
        long lastStepAt = -10000;
        int heldSteps;

        void StepDelay(int d)
        {
            long t = clock.ElapsedMilliseconds;
            heldSteps = t - lastStepAt < 200 ? heldSteps + 1 : 0;
            lastStepAt = t;
            int step = heldSteps >= 12 ? 1000 : heldSteps >= 4 ? 500 : Subtitles.DelayStep;
            SetDelayByHand(subDelay + d * step, "pas à pas", true);
        }

        string DelayLabel() => subDelay == 0 ? Lang.T("sub.in_sync") : Subtitles.Amount(subDelay);

        string SyncStatusText()
        {
            if (syncStatus != null) return Lang.T("sub.status_" + syncStatus);
            if (sync != null && !sync.Done) return Lang.T("sub.status_waiting", heardLines.Count);
            if (cues != null && refTrack < 0) return Lang.T("sub.status_none");
            return null;
        }

        // « Recaler à l'oreille » : Entendu et Vu, dans n'importe quel ordre (delay.ts).
        void Tap(bool heard)
        {
            int at = Now();
            if (heard) heardAt = at; else sawAt = at;
            if (heardAt < 0 || sawAt < 0) { earNote = Lang.T(heard ? "sub.ear_now_saw" : "sub.ear_now_heard"); return; }
            int? d = Subtitles.DelayForTaps(subDelay, heardAt, sawAt);
            heardAt = sawAt = -1;
            if (d == null) { earNote = Lang.T("sub.ear_far"); return; }
            SetDelayByHand(d.Value, "à l'oreille", false);
            earNote = d.Value != 0 ? Lang.T("sub.ear_set", Subtitles.Amount(d.Value)) : Lang.T("sub.ear_zero");
        }

        // Lignes des panneaux Synchronisation, Style, oreille et réplique.
        void SubPanelItems(string kind)
        {
            switch (kind)
            {
                case "sync":
                {
                    panelTitleText = Lang.T("sub.sync");
                    panelItems.Add(new PanelItem { Name = Lang.T("sub.delay_row", DelayLabel()), Detail = SyncStatusText(), Value = ActionValue, Step = StepDelay });
                    bool auto = CanAutoSync();
                    if (auto && syncStatus == "hand")
                        panelItems.Add(new PanelItem
                        {
                            Name = Lang.T("sub.auto"), Value = ActionValue, Choose = () =>
                            {
                                MediaStream m = CurrentServerSub();
                                if (m != null) Subtitles.SaveDelay(source.Id, SubKey(m), subDelay, false);
                                syncStatus = null;
                                ClosePanel();
                                StartAutoSync(true);
                            }
                        });
                    panelItems.Add(new PanelItem { Name = Lang.T("sub.ear"), Value = ActionValue, Sub = true, Choose = () => { heardAt = sawAt = -1; earNote = Lang.T("sub.ear_start"); OpenPanel("ear"); } });
                    if (cues != null) panelItems.Add(new PanelItem { Name = Lang.T("sub.line"), Value = ActionValue, Sub = true, Choose = () => { lineHeardAt = Now(); OpenPanel("lines"); } });
                    if (!auto && subDelay != 0) panelItems.Add(new PanelItem { Name = Lang.T("sub.reset"), Value = ActionValue, Choose = () => SetDelayByHand(0, "remise à zéro") });
                    panelNoteText = Lang.T("sub.sync_hint");
                    break;
                }
                case "style":
                    panelTitleText = Lang.T("sub.style");
                    panelItems.Add(new PanelItem { Name = Lang.T("sub.size_row", Lang.T("sub.size" + SubSizeIndex)), Value = ActionValue, Step = StepSize });
                    panelItems.Add(new PanelItem { Name = Lang.T("sub.height_row", Prefs.SubtitlePosition), Value = ActionValue, Step = StepHeight });
                    panelNoteText = Lang.T("sub.style_hint");
                    break;
                case "ear":
                    panelTitleText = Lang.T("sub.ear_title");
                    panelItems.Add(new PanelItem { Name = Lang.T("sub.heard"), Detail = Lang.T("sub.heard_detail"), Value = ActionValue, Choose = () => Tap(true) });
                    panelItems.Add(new PanelItem { Name = Lang.T("sub.saw"), Detail = Lang.T("sub.saw_detail"), Value = ActionValue, Choose = () => Tap(false) });
                    panelItems.Add(new PanelItem { Name = Lang.T("sub.done"), Value = ActionValue, Choose = () => OpenPanel("sync") });
                    panelNoteText = Lang.T("sub.delay_now", DelayLabel()) + "\n" + earNote;
                    break;
                case "lines":
                {
                    panelTitleText = Lang.T("sub.lines_title", Library.Clock(Math.Max(0, lineHeardAt) * Playback.TicksPerMs));
                    if (cues == null) break;
                    int idx = Math.Max(0, CueAt(lineHeardAt - subDelay));
                    int from = Math.Max(0, idx - 30), to = Math.Min(cues.Count - 1, idx + 30);
                    for (int i = from; i <= to; i++)
                    {
                        Cue c = cues[i];
                        panelItems.Add(new PanelItem
                        {
                            Name = c.Text.Replace('\n', ' '),
                            Detail = Library.Clock(Math.Max(0, c.Start + subDelay) * Playback.TicksPerMs),
                            Value = ActionValue,
                            Choose = () => { SetDelayByHand(Subtitles.DelayForLine(lineHeardAt, c), $"réplique à {c.Start} ms, entendue à {lineHeardAt} ms"); OpenPanel("sync"); },
                        });
                    }
                    wantFocus = idx - from;
                    break;
                }
            }
        }

        // ---- Style (style.ts) : taille et hauteur, gardées sur le compte comme le reste du style ----

        static int SubSizeIndex => Math.Max(0, Array.IndexOf(Prefs.SubtitleSizes, Prefs.SubtitleSize));

        void StepSize(int d)
        {
            int n = Math.Max(0, Math.Min(Prefs.SubtitleSizes.Length - 1, SubSizeIndex + d));
            if (n == SubSizeIndex) { Ignored(); return; }
            Prefs.SubtitleSize = Prefs.SubtitleSizes[n];
            subtitle.Apply();
        }

        void StepHeight(int d)
        {
            int h = Prefs.SubtitlePosition;
            int n = Math.Max(Prefs.PositionMin, Math.Min(Prefs.PositionMax, h + d));
            if (n == h) { Ignored(); return; }
            Prefs.SubtitlePosition = n;
            ApplySubStyle(false);
        }

        // Position ; au-dessus de la barre de contrôle quand elle est affichée (glissement de
        // 0,3 s, comme la version web), sauf pendant le réglage Style : la hauteur choisie est
        // montrée telle quelle (l'auteur, 2026-11-19 : de -3 à 19 %, rien ne bougeait, le sous-titre
        // restait posé au-dessus de la barre).
        void ApplySubStyle(bool animate)
        {
            int height = Prefs.SubtitlePosition;
            int h = lifted && panelKind != "style" ? Math.Max(height, SubLiftPct) : height;
            float y = 1080f * (97 - h) / 100 - SubH;
            liftAnim?.Stop();
            if (!animate) { subtitle.View.PositionY = y; return; }
            liftAnim = new Animation(300);
            liftAnim.AnimateTo(subtitle.View, "PositionY", y, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            liftAnim.Play();
        }

        void UpdateLift()
        {
            if (controlsShown == lifted) return;
            lifted = controlsShown;
            ApplySubStyle(true);
        }


        // ---- Fin d'épisode (module 7) ----

        const int SkipShown = 8000, SkipH = Theme.ButtonHeight, CardW = 440, CardH = 416, CardImgW = 424, CardImgH = 238;
        const int MinPromptDuration = 40000, CreditsTail = 30000, ListLead = 120000;

        // Bouton d'action comme le web sur la TV (PlayerAction, « white-outline ») : capsule à
        // contour blanc de 2 px sur fond sombre (fill, opaque ou presque : le blanc du contour est
        // dessous) ; regardé : blanc plein, texte foncé (PaintAction).
        static View ActionButton(int w, int h, Color fill, out View inner, out TextLabel label)
        {
            var outer = new View { Size = new Size(w, h), BackgroundColor = Color.White }.Rounded(h / 2f);
            inner = new View { Position = new Position(2, 2), Size = new Size(w - 4, h - 4), BackgroundColor = fill }.Rounded((h - 4) / 2f);
            label = new TextLabel { Size = new Size(w, h), PointSize = Theme.SizeBody, TextColor = Color.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            outer.Add(inner);
            outer.Add(label);
            return outer;
        }

        static void PaintAction(View inner, TextLabel label, Color fill, bool on)
        {
            inner.BackgroundColor = on ? Color.White : fill;
            label.TextColor = on ? Theme.Ink : Color.White;
        }

        static void SizeAction(View outer, View inner, TextLabel label, int w)
        {
            int h = (int)outer.SizeHeight;
            outer.Size = new Size(w, h);
            inner.Size = new Size(w - 4, h - 4);
            label.Size = new Size(w, h);
        }

        int chain;
        bool endingAsked, segmentsLoaded, nextLoaded;
        List<Skippable> segments = new List<Skippable>(), serverSegments = new List<Skippable>();
        string segmentsError, nextError;
        // Chapitres du fichier (Core/Chapters.cs), lus à la première image de chaque version.
        List<Chapter> fileChapters;
        Source chaptersFor;
        string chaptersError;
        long chaptersMs = -1;

        // Bouton « Passer » : segment en cours (null sinon), moment où il a commencé.
        Skippable offered;
        long offeredAt;
        bool skipUp;
        string skipText;
        View endingLayer, skipBox, skipInner;
        TextLabel skipLabel;

        // Épisode suivant : carte, décompte, « Vous regardez toujours ? ».
        Item next, prev;
        Task<PlaybackInfo> nextVersions;
        bool cardShown, cardDismissed, onCard, leaving, asking;
        int cardFocus, leftMs, cardSeconds = -1, stillFocus;
        // Segments déjà passés seuls : revenu dedans en arrière, on propose le bouton.
        readonly HashSet<Skippable> autoSkipped = new HashSet<Skippable>();
        View card, cardFill, stillView;
        readonly View[] cardBtn = new View[2], stillBtn = new View[2], cardInner = new View[2], stillInner = new View[2];
        readonly TextLabel[] cardLabel = new TextLabel[2], stillLabel = new TextLabel[2];
        Animation cardAnim, fillAnim; // gardées dans des champs
        bool fillRunning;
        Timer countTimer;

        // Couche sous les panneaux : bouton Passer et carte de l'épisode suivant.
        void BuildEnding()
        {
            endingLayer = new View { Size = new Size(1920, 1080) };
            root.Add(endingLayer);
            skipBox = ActionButton(300, SkipH, Theme.Panel, out skipInner, out skipLabel);
            skipBox.Hide();
            endingLayer.Add(skipBox);
        }

        // Segments et épisode suivant, demandés une fois au début de la lecture.
        async void LoadEnding()
        {
            try
            {
                Segment[] l = await Library.Segments(target.Id);
                if (Closed) return;
                var list = new List<Skippable>();
                foreach (Segment s in l)
                {
                    var seg = new Skippable { Type = s.Type ?? "", Start = (int)(s.StartTicks / Playback.TicksPerMs), End = (int)(s.EndTicks / Playback.TicksPerMs) };
                    if (seg.End > seg.Start) list.Add(seg);
                }
                serverSegments = list;
                MergeSegments();
            }
            catch (Exception e) { segmentsError = Short(e); }
            if (Closed) return;
            segmentsLoaded = true;
            Probe.Log("ending", $"segments du serveur pour {target.Id} ({Library.Code(target) ?? target.Name}) : {serverSegments.Count}" + (segmentsError != null ? ", erreur " + segmentsError : ""));
            if (target.Type != "Episode" || target.SeriesId == null) { nextLoaded = true; return; }
            try { (prev, next) = await Library.Adjacent(target); }
            catch (Exception e) { nextError = Short(e); }
            if (Closed) return;
            nextLoaded = true;
            Probe.Log("ending", "épisode suivant : " + NextText());
            guard.Run("NextButton", () => { LayoutButtons(); if (controlsShown) Refresh(); });
        }

        // Chapitres du fichier : intro, récap, générique nommés, sinon devinés (épisodes). Le lecteur
        // de la TV ne les donne pas : l'application lit l'en-tête du fichier.
        async Task ReadChapters()
        {
            Source s = source;
            if (chaptersFor == s) return;
            chaptersFor = s;
            fileChapters = null;
            chaptersError = null;
            chaptersMs = -1;
            string c = (s.Container ?? "").ToLowerInvariant();
            if (c.Length > 0 && !c.Contains("mkv") && !c.Contains("matroska") && !c.Contains("webm"))
            {
                chaptersError = Lang.T("player.chapters_not_mkv", s.Container);
                MergeSegments();
                return;
            }
            string url = Playback.StreamUrl(target.Id, s); // contient des clés : jamais dans le journal
            long t0 = clock.ElapsedMilliseconds;
            List<Chapter> l = null;
            string err = null;
            using (var cancel = new CancellationTokenSource(3000))
            {
                try { l = await Task.Run(() => Chapters.Read(url, cancel.Token)); }
                catch (Exception e) { err = cancel.IsCancellationRequested ? Lang.T("player.chapters_late") : e is System.Net.Http.HttpRequestException ? Pictures.Cut(e.Message, 80) : e.GetType().Name; }
            }
            if (Closed || s != source) return;
            chaptersMs = clock.ElapsedMilliseconds - t0;
            fileChapters = l;
            chaptersError = err;
            Probe.Log("ending", $"chapitres en {chaptersMs} ms : " + ChaptersText());
            MergeSegments();
        }

        // Serveur d'abord, puis chapitres nommés, puis devinés : un type déjà trouvé n'est pas
        // repris d'une source suivante (comme useShownSegments du web).
        void MergeSegments()
        {
            var all = new List<Skippable>();
            var kinds = new HashSet<string>();
            void Add(List<Skippable> from)
            {
                var added = new List<Skippable>();
                foreach (Skippable x in from) if (!kinds.Contains(x.Type)) added.Add(x);
                foreach (Skippable x in added) { kinds.Add(x.Type); all.Add(x); }
            }
            Add(serverSegments);
            if (chapterSkips && fileChapters != null && durationMs > 0)
            {
                Add(Chapters.Named(fileChapters, durationMs));
                if (target.Type == "Episode") Add(Chapters.Guessed(fileChapters, durationMs));
            }
            all.Sort((a, b) => a.Start.CompareTo(b.Start));
            segments = all;
            offered = null;
            Probe.Log("ending", "segments retenus : " + SegmentsText());
        }

        string ChaptersText()
        {
            if (!chapterSkips) return Lang.T("player.chapters_off");
            if (chaptersError != null) return Lang.T("player.ending_error", chaptersError);
            if (fileChapters == null) return chaptersFor == null ? "-" : "…";
            if (fileChapters.Count == 0) return Lang.T("player.ending_none");
            var names = new List<string>();
            foreach (Chapter ch in fileChapters) names.Add(ch.Title.Length > 0 ? ch.Title : "?");
            return fileChapters.Count + " (" + Pictures.Cut(string.Join(", ", names), 90) + ")" + (chaptersMs >= 0 ? $", {chaptersMs} ms" : "");
        }

        static string Short(Exception e) => Pictures.Cut(e.GetType().Name + " " + e.Message, 80);

        // Un générique qui finit l'épisode : il se termine dans les 10 derniers % et à 30 s de la fin au plus.
        bool EndsEpisode(int end) => end >= durationMs * 0.9 && durationMs - end <= CreditsTail;

        // Moment de la carte (promptAt du web) : début du générique s'il finit l'épisode (réglage
        // « au début du générique »), sinon le délai choisi avant la fin ; jamais : -1.
        int PromptAt()
        {
            if (nextPrompt == "off" || durationMs < MinPromptDuration) return -1;
            if (nextPrompt == "credits")
                foreach (Skippable s in segments)
                    if (s.Type == "Outro") return EndsEpisode(s.End) ? s.Start : durationMs - PromptLead;
            return durationMs - PromptLead;
        }

        // Toutes les 500 ms (Tick) : versions de l'épisode suivant 2 min avant sa carte, carte, bouton Passer.
        void Ending()
        {
            if (!started || leaving || asking || ended) return;
            int pos = positionMs;
            int at = next != null ? PromptAt() : -1;
            if (at >= 0 && nextVersions == null && pos >= at - ListLead)
            {
                nextVersions = Library.Versions(next.Id);
                Probe.Log("ending", $"versions de l'épisode suivant demandées à {pos} ms");
            }
            bool due = at >= 0 && pos >= at;
            if (!due) cardDismissed = false; // revenu avant la carte : elle reviendra
            bool show = due && !cardDismissed;
            if (show && !cardShown) ShowCard();
            else if (!show && cardShown) HideCard();
            // La carte remplace le bouton (comme le web) : passer le générique, c'est elle.
            Skippable inside = null;
            if (!cardShown)
                foreach (Skippable s in segments)
                    if (ActionOf(s) != "none" && pos >= s.Start && pos < s.End - 1000) { inside = s; break; }
            if (inside != offered)
            {
                offered = inside;
                offeredAt = clock.ElapsedMilliseconds;
                if (inside != null && ActionOf(inside) == "skip" && autoSkipped.Add(inside))
                {
                    Probe.Log("ending", $"segment {inside.Type} passé seul à {pos} ms");
                    Skip();
                    return;
                }
                if (inside != null) Probe.Log("ending", $"segment {inside.Type} proposé à {pos} ms");
            }
            PaintEnding();
        }

        // Bouton affiché 8 s au début du segment, puis avec la barre de contrôle. Barre cachée :
        // il est en blanc et OK le prend ; barre affichée : on y monte avec Haut.
        void PaintEnding()
        {
            if (skipBox == null) return;
            bool fresh = offered != null && clock.ElapsedMilliseconds - offeredAt < SkipShown;
            skipUp = offered != null && !failed && !leaving && !asking && !cardShown && panelKind == null && !pauseShown && (fresh || controlsShown);
            if (!skipUp)
            {
                skipBox.Hide();
                if (zone == Zone.Skip) zone = Zone.Bar;
            }
            else
            {
                string text = SkipText(offered.Type);
                if (text != skipText)
                {
                    skipText = text;
                    SizeAction(skipBox, skipInner, skipLabel, 64 + text.Length * 10); // largeur estimée (18 pt)
                    skipLabel.Text = text;
                }
                bool focused = !controlsShown || zone == Zone.Skip;
                PaintAction(skipInner, skipLabel, Theme.Panel, focused);
                skipBox.Position = new Position(1920 - Margin - (int)skipBox.SizeWidth, controlsShown ? BarY - 72 - SkipH : 1080 - 56 - SkipH);
                skipBox.Show();
            }
            if (cardShown) PaintCard();
        }

        static string SkipText(string type)
        {
            switch (type)
            {
                case "Intro": return Lang.T("player.skip_intro");
                case "Recap": return Lang.T("player.skip_recap");
                case "Outro": return Lang.T("player.skip_outro");
                case "Preview": return Lang.T("player.skip_preview");
                case "Commercial": return Lang.T("player.skip_commercial");
                default: return Lang.T("player.skip");
            }
        }

        void Skip()
        {
            Skippable s = offered;
            if (s == null || player == null) return;
            Probe.Log("ending", $"segment {s.Type} passé à {positionMs} ms, vers {s.End} ms");
            offered = null;
            if (zone == Zone.Skip) zone = Zone.Bar;
            PaintEnding();
            seekTimer?.Stop();
            pendingSeek = Math.Min(durationMs > 0 ? durationMs - 1000 : int.MaxValue, s.End);
            SeekGo();
            if (controlsShown) ShowControls();
        }

        string NextName()
        {
            if (next == null) return "";
            var parts = new List<string>();
            string code = Library.Code(next);
            if (code != null) parts.Add(code);
            if (!string.IsNullOrEmpty(next.Name)) parts.Add(next.Name);
            return string.Join(" · ", parts);
        }

        // Image de l'épisode suivant dans un cadre arrondi (fond gris tant qu'elle n'est pas là).
        View NextImage(int x, int y, int w, int h, int radius)
        {
            var box = new View { Position = new Position(x, y), Size = new Size(w, h), BackgroundColor = Theme.Placeholder }.Rounded(radius);
            string url = Library.Landscape(next, w);
            if (url != null)
                ImageCache.Request(url, () => Closed ? int.MaxValue : 0, path => guard.Run("NextImage", () =>
                {
                    if (Closed || path == null) return;
                    View v = Pictures.Cover(path, w, h, out ImageView iv);
                    if (v == null) return;
                    if (iv != null) iv.Rounded(radius);
                    box.Add(v);
                }));
            return box;
        }

        // Carte en bas à droite (au-dessus de la barre quand elle est affichée), comme le web :
        // image, « ÉPISODE SUIVANT » (12 pt majuscules grises), code et titre en demi-gras, Lecture
        // dans 15 s / Masquer en capsules, décompte blanc. Fond des menus (bg-gray-950/95).
        void BuildCard()
        {
            card = new View { Size = new Size(CardW, CardH), BackgroundColor = MenuBack }.Rounded(Theme.RadiusLarge);
            card.Add(NextImage(8, 8, CardImgW, CardImgH, Theme.RadiusSmall));
            card.Add(new TextLabel { Position = new Position(16, 262), Size = new Size(CardW - 32, 20), PointSize = Theme.SizeSmall, TextColor = Theme.Grey, FontStyle = Theme.SemiBold(), Text = Lang.T("player.next_title") });
            card.Add(new TextLabel { Position = new Position(16, 284), Size = new Size(CardW - 32, 32), PointSize = Theme.SizeBody, TextColor = Color.White, FontStyle = Theme.SemiBold(), Ellipsis = true, Text = Pictures.Cut(NextName(), 40) });
            int[] x = { 12, 12 + 276 + 12 }, w = { 276, CardW - 24 - 276 - 12 };
            for (int i = 0; i < 2; i++)
            {
                cardBtn[i] = ActionButton(w[i], Theme.ButtonHeight, MenuBack, out cardInner[i], out cardLabel[i]);
                cardBtn[i].Position = new Position(x[i], 328);
                card.Add(cardBtn[i]);
            }
            cardLabel[1].Text = Lang.T("player.next_hide");
            var track = new View { Position = new Position(12, 400), Size = new Size(CardW - 24, 4), BackgroundColor = Theme.Marked }.Rounded(2);
            card.Add(track);
            // Rétrécie vers la gauche par un agrandissement horizontal autour de son bord gauche.
            cardFill = new View { Position = new Position(12, 400), Size = new Size(CardW - 24, 4), BackgroundColor = Color.White, PivotPoint = PivotPoint.TopLeft }.Rounded(2);
            card.Add(cardFill);
            // Sans lancement automatique, pas de décompte (comme le web).
            if (!nextAuto) { track.Hide(); cardFill.Hide(); }
            card.Hide();
            endingLayer.Add(card);
        }

        void ShowCard()
        {
            if (card == null) BuildCard();
            cardShown = true;
            onCard = true; // la télécommande est dessus dès son arrivée (comme le web sur TV)
            cardFocus = 0;
            leftMs = CountdownMs;
            cardSeconds = -1;
            offered = null;
            if (zone == Zone.Skip) zone = Zone.Bar;
            skipBox.Hide();
            PaintCard();
            card.Opacity = 0f;
            card.Show();
            cardAnim?.Stop();
            cardAnim = new Animation(300);
            cardAnim.AnimateTo(card, "Opacity", 1f);
            cardAnim.Play();
            countTimer?.Stop();
            countTimer = new Timer(250);
            countTimer.Tick += (s, e) => { guard.Run("Countdown", Countdown); return !Closed && cardShown; };
            countTimer.Start();
            Probe.Log("ending", $"carte de l'épisode suivant à {positionMs} ms");
            if (controlsShown) Refresh();
        }

        void HideCard()
        {
            if (!cardShown) return;
            cardShown = false;
            onCard = false;
            countTimer?.Stop();
            cardAnim?.Stop();
            fillAnim?.Stop();
            fillRunning = false;
            card.Hide();
            if (controlsShown) Refresh();
        }

        void DismissCard()
        {
            cardDismissed = true;
            HideCard();
            Probe.Log("ending", "carte masquée");
        }

        // Décompte de 15 s, arrêté en pause ; à zéro, l'épisode suivant démarre.
        void Countdown()
        {
            if (!cardShown || leaving || !nextAuto || !IsPlaying()) { if (cardShown) PaintCard(); return; }
            leftMs -= 250;
            PaintCard();
            if (leftMs <= 0) PlayOn(true);
        }

        void PaintCard()
        {
            if (card == null) return;
            bool counting = nextAuto && IsPlaying();
            int secs = counting ? Math.Max(1, (leftMs + 999) / 1000) : 0;
            if (secs != cardSeconds)
            {
                cardSeconds = secs;
                cardLabel[0].Text = counting ? Lang.T("player.next_in", secs) : Lang.T("player.next_now");
            }
            for (int i = 0; i < 2; i++)
            {
                PaintAction(cardInner[i], cardLabel[i], MenuBack, onCard && cardFocus == i);
            }
            // Barre du décompte animée par la TV elle-même, en continu (demande de l'auteur : elle
            // avançait par saccades de 250 ms). Arrêtée en pause, reprise d'où elle en était.
            bool running = counting && !leaving;
            if (running != fillRunning)
            {
                fillRunning = running;
                fillAnim?.Stop();
                cardFill.Scale = new Vector3(Math.Max(0f, Math.Min(1f, leftMs / (float)CountdownMs)), 1f, 1f);
                if (running)
                {
                    fillAnim = new Animation(Math.Max(1, leftMs));
                    fillAnim.AnimateTo(cardFill, "Scale", new Vector3(0f, 1f, 1f), new AlphaFunction(AlphaFunction.BuiltinFunctions.Linear));
                    fillAnim.Play();
                }
            }
            card.Position = new Position(1920 - Margin - CardW, controlsShown ? BarY - 70 - CardH : 1080 - 48 - CardH);
        }

        bool CardKeys(string key)
        {
            switch (key)
            {
                case "Left": if (cardFocus > 0) { cardFocus = 0; PaintCard(); } else Ignored(); return true;
                case "Right": if (cardFocus < 1) { cardFocus = 1; PaintCard(); } else Ignored(); return true;
                case "Return":
                case "KP_Enter":
                    if (cardFocus == 0) PlayOn(false); else DismissCard();
                    return true;
                case "Down": // vers la barre de contrôle ; Haut depuis la barre revient sur la carte
                    onCard = false;
                    zone = Zone.Bar;
                    ShowControls();
                    return true;
                case "Up": Ignored(); return true;
            }
            return false;
        }

        // Passer seul à l'épisode suivant ferait « stillWatching » épisodes de suite sans appui (0 : jamais).
        bool AsksFirst => StillAfter > 0 && chain + 1 >= StillAfter;

        // auto : passage sans appui (décompte, fin du fichier). Au 3e de suite (réglage), on demande d'abord.
        void PlayOn(bool auto)
        {
            if (next == null || leaving) return;
            if (auto && AsksFirst) { PauseNow(); AskStill(); return; }
            PlayNext(auto ? chain + 1 : 0);
        }

        // Versions de l'épisode suivant (souvent déjà demandées), puis celle qui continue la
        // version en cours ; sans elle, la liste des liens. Le lecteur est remplacé : Retour
        // ramène toujours à la page du titre.
        // which : un autre épisode que le suivant (bouton Épisode précédent).
        async void PlayNext(int nextChain, Item which = null)
        {
            Item ep = which ?? next;
            if (ep == null || leaving) return;
            leaving = true;
            HideCard();
            skipBox.Hide();
            string name = Library.Code(ep) ?? ep.Name ?? "";
            Notice(Lang.T("player.next_finding", name), 600000);
            Probe.Log("ending", $"vers {name} ({(nextChain > 0 ? "sans appui, " + nextChain + " de suite" : "demandé")})");
            PlaybackInfo info = null;
            string error = null;
            try { info = await (which != null ? Library.Versions(ep.Id) : nextVersions ?? (nextVersions = Library.Versions(ep.Id))); }
            catch (Exception e) { error = Short(e); }
            if (Closed) return;
            if (info == null)
            {
                Probe.Log("ending", "versions de l'épisode suivant : " + (error ?? "réponse vide"));
                if (which == null) nextVersions = null;
                leaving = false;
                if (ended) HideLoader();
                Notice(Lang.T("player.next_failed"), 6000);
                return;
            }
            var playable = new List<Source>();
            foreach (Source s in info.MediaSources ?? new Source[0]) if (s.Type != "Placeholder") playable.Add(s);
            Source version = CarryOn(playable);
            long resume = ep.UserData?.Played == true ? 0 : ep.UserData?.PlaybackPositionTicks ?? 0;
            Probe.Log("ending", version == null ? "aucune version : liste des liens"
                : $"version {playable.IndexOf(version) + 1}/{playable.Count}" + (source.Aio?.BingeGroup != null && version.Aio?.BingeGroup == source.Aio.BingeGroup ? ", même groupe" : ", première"));
            Playback.Chained = true;
            guard.Run("PlayNext", () =>
            {
                if (version != null) Nav.Replace(new PlayerScreen(ep, show, version, playable, info.PlaySessionId, resume, nextChain, info.MediaSources));
                else Nav.Replace(new VersionsScreen(ep, resume, show, straight: false));
            });
        }

        // La version qui continue celle-ci : même groupe (bingeGroup d'AIOStreams), sinon la seule,
        // sinon la première si le réglage « fallbackFirst » le permet ; null : la liste des liens.
        Source CarryOn(List<Source> l)
        {
            string g = source.Aio?.BingeGroup;
            if (!string.IsNullOrEmpty(g))
                foreach (Source s in l) if (s.Aio?.BingeGroup == g) return s;
            return l.Count == 1 || (fallbackFirst && l.Count > 0) ? l[0] : null;
        }

        // « Vous regardez toujours ? » : par-dessus tout, lecture en pause.
        void AskStill()
        {
            asking = true;
            HideCard();
            HidePause();
            skipBox.Hide();
            controls.Hide();
            controlsShown = false;
            UpdateLift();
            if (stillView == null) BuildStill();
            stillFocus = 0;
            PaintStill();
            stillView.Show();
            stillView.RaiseToTop();
            Probe.Log("ending", "vous regardez toujours ?");
        }

        void BuildStill()
        {
            const int imgW = 560, imgH = 315, top = 180;
            // Comme le web : voile noir à 80 %, image arrondie, titre demi-gras, texte gris, capsules.
            stillView = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.Veil };
            stillView.Add(NextImage(960 - imgW / 2, top, imgW, imgH, Theme.RadiusLarge));
            stillView.Add(new TextLabel { Position = new Position(160, top + imgH + 40), Size = new Size(1600, 56), PointSize = Theme.SizeTitle, TextColor = Color.White, FontStyle = Theme.SemiBold(), HorizontalAlignment = HorizontalAlignment.Center, Text = Lang.T("player.still_title") });
            stillView.Add(new TextLabel { Position = new Position(160, top + imgH + 104), Size = new Size(1600, 30), PointSize = Theme.SizeInfo, TextColor = Theme.Grey, HorizontalAlignment = HorizontalAlignment.Center, Text = Lang.T("player.still_next", Pictures.Cut(NextName(), 70)) });
            const int w = 300, h = Theme.ButtonHeight;
            string[] keys = { "player.still_continue", "player.still_back" };
            for (int i = 0; i < 2; i++)
            {
                stillBtn[i] = ActionButton(w, h, Color.Black, out stillInner[i], out stillLabel[i]);
                stillBtn[i].Position = new Position(960 - w - Theme.Gap / 2 + i * (w + Theme.Gap), top + imgH + 170);
                stillLabel[i].Text = Lang.T(keys[i]);
                stillView.Add(stillBtn[i]);
            }
            stillView.Hide();
            root.Add(stillView);
        }

        void PaintStill()
        {
            for (int i = 0; i < 2; i++)
            {
                PaintAction(stillInner[i], stillLabel[i], Color.Black, stillFocus == i);
            }
        }

        bool StillKeys(string key)
        {
            switch (key)
            {
                case "Left": case "Up": stillFocus = 0; PaintStill(); return true;
                case "Right": case "Down": stillFocus = 1; PaintStill(); return true;
                case "Return":
                case "KP_Enter":
                    if (stillFocus == 1) { Nav.Back(); return true; }
                    asking = false;
                    stillView.Hide();
                    if (ended) ShowLoader(false);
                    PlayNext(0);
                    return true;
            }
            return true;
        }

        // Statistiques, page Lecture.
        string SegmentsText()
        {
            if (segments.Count == 0 && segmentsError != null) return Lang.T("player.ending_error", segmentsError);
            if (segments.Count == 0 && !segmentsLoaded) return "…";
            if (segments.Count == 0) return Lang.T("player.ending_none");
            var l = new List<string>();
            foreach (Skippable s in segments) l.Add(s.Type + " " + Library.Clock(s.Start * Playback.TicksPerMs) + "–" + Library.Clock(s.End * Playback.TicksPerMs));
            return string.Join(", ", l);
        }

        string NextText()
        {
            if (nextError != null) return Lang.T("player.ending_error", nextError);
            if (!nextLoaded) return "…";
            if (next == null) return Lang.T("player.ending_none");
            string v = nextVersions == null ? "-" : !nextVersions.IsCompleted ? "…"
                : nextVersions.Status == TaskStatus.RanToCompletion ? (nextVersions.Result?.MediaSources?.Length ?? 0).ToString() : Lang.T("player.ending_failed");
            int at = PromptAt();
            return Lang.T("player.ending_next", NextName(), at < 0 ? "-" : Library.Clock(at * Playback.TicksPerMs), v);
        }

        // ---- Message bref ----

        void Notice(string text, int ms)
        {
            int w = Math.Min(1300, 64 + text.Length * 13);
            noticeLabel.Text = text;
            noticeLabel.Size = new Size(w, 56);
            noticeBox.Size = new Size(w, 56);
            noticeBox.Position = new Position(Margin, 140);
            noticeBox.Show();
            noticeBox.RaiseToTop();
            if (noticeTimer == null)
            {
                noticeTimer = new Timer((uint)ms);
                noticeTimer.Tick += (s, e) => { if (!Closed) noticeBox.Hide(); return false; };
            }
            noticeTimer.Stop();
            noticeTimer.Interval = (uint)ms;
            noticeTimer.Start();
        }

        void HideNotice()
        {
            noticeTimer?.Stop();
            noticeBox.Hide();
        }

        // Statistiques, page Pistes : de quoi suivre les essais sur la TV.
        string SubStats()
        {
            int announced = 0;
            foreach (MediaStream x in source.MediaStreams ?? new MediaStream[0])
                if (x.Type == "Subtitle" && x.IsExternal) announced++;
            MediaStream m = CurrentServerSub();
            return Lang.T("sub.stats", ServerSubs().Count, announced,
                m == null ? "-" : (m.Language ?? "?") + " " + (m.Codec ?? "") + " " + (m.Title ?? m.DisplayTitle ?? ""),
                cues?.Count.ToString() ?? "-", cuesLoadMs < 0 ? "-" : cuesLoadMs + " ms", DelayLabel(), SyncStatusText() ?? "-",
                refTrack < 0 ? "-" : refTrack.ToString(), heardLines.Count, syncTries, loadError ?? "-");
        }

        // ---- Barre de contrôle ----

        void ShowControls()
        {
            if (failed) return;
            HidePause();
            if (!controlsShown) { controls.Opacity = 1f; controls.Show(); controlsShown = true; }
            UpdateLift();
            Refresh();
            hideTimer?.Stop();
            // En pause : écran de pause après 5 s sans appui.
            if (!playing && started && panelKind == null)
            {
                pauseTimer = new Timer(PauseAfter);
                pauseTimer.Tick += (s, e) => { guard.Run("PauseScreen", ShowPause); return false; };
                pauseTimer.Start();
            }
            // Cachée après 5 s de lecture sans appui ; reste affichée en pause ou panneau ouvert.
            if (panelKind != null || !IsPlaying()) return;
            hideTimer = new Timer(HideAfter);
            hideTimer.Tick += (s, e) => { guard.Run("Hide", HideControls); return false; };
            hideTimer.Start();
        }

        void HideControls()
        {
            if (panelKind != null || !IsPlaying() || pendingSeek >= 0) { ShowControls(); return; }
            controls.Hide();
            controlsShown = false;
            UpdateLift();
            zone = Zone.Bar;
            if (cardShown) onCard = true;
            PaintEnding();
        }

        void Refresh()
        {
            if (controls == null) return;
            int pos = pendingSeek >= 0 ? pendingSeek : positionMs;
            now.Text = DateTime.Now.ToString("HH:mm");
            string elapsed = Library.Clock(pos * Playback.TicksPerMs);
            times.Text = elapsed;
            // Largeur du temps écoulé mesurée seulement quand son nombre de caractères change.
            if (elapsed.Length != timesLength)
            {
                timesLength = elapsed.Length;
                timesTotal.PositionX = Margin + (int)times.NaturalSize2D.Width + 6;
            }
            timesTotal.Text = "/ " + (durationMs > 0 ? Library.Clock(durationMs * Playback.TicksPerMs) : "--:--")
                + (pendingSeek >= 0 ? "   (" + (pendingSeek >= positionMs ? "+" : "−") + Library.Clock(Math.Abs(pendingSeek - positionMs) * Playback.TicksPerMs) + ")" : "");
            endsAt.Text = durationMs > 0 ? Lang.T("player.ends_at", DateTime.Now.AddMilliseconds(durationMs - pos).ToString("HH:mm")) : "";
            float f = durationMs > 0 ? Math.Max(0f, Math.Min(1f, pos / (float)durationMs)) : 0f;
            bool onBar = zone == Zone.Bar && panelKind == null && !(cardShown && onCard);
            int h = onBar ? 6 : 4;
            barTrack.Size = new Size(BarW, h);
            barTrack.PositionY = BarY - h / 2 + 3;
            barTrack.Rounded(h / 2f);
            barPlayed.Size = new Size((int)(BarW * f), h);
            barPlayed.Rounded(h / 2f);
            barThumb.Position = new Position(Margin + (int)(BarW * f) - 8, BarY + 3 - 8);
            if (onBar) barThumb.Show(); else barThumb.Hide();
            playIcon.ResourceUrl = Icon(IsPlaying() ? "pause" : "play_lu");
            bool onButtons = zone == Zone.Buttons && panelKind == null;
            for (int i = 0; i < btnBox.Length; i++)
            {
                bool on = onButtons && i == btnFocus;
                btnBox[i].BackgroundColor = on ? Color.White : Color.Transparent;
                btnImg[i].Color = on ? Theme.Ink : IconRest;
            }
            if (onButtons) ShowTip(); else tip.Hide();
            PaintEnding();
        }

        string TipText(Btn b)
        {
            switch (b)
            {
                case Btn.Play: return Lang.T(IsPlaying() ? "player.tip_pause" : "player.tip_play");
                case Btn.Back: return Lang.T("player.tip_back");
                case Btn.Forward: return Lang.T("player.tip_forward");
                case Btn.Next: return Lang.T("player.tip_next", next != null ? Library.Code(next) ?? next.Name ?? "" : "");
                case Btn.Prev: return Lang.T("player.tip_prev", prev != null ? Library.Code(prev) ?? prev.Name ?? "" : "");
                case Btn.Versions: return Lang.T("player.tip_versions");
                case Btn.Audio: return Lang.T("player.tip_audio");
                case Btn.Subtitles: return Lang.T("player.tip_subtitles");
                default: return Lang.T("player.stats_title");
            }
        }

        // Bulle centrée au-dessus du bouton, gardée dans l'écran ; largeur estimée (10 px par
        // caractère à 18 pt, comme la page d'un titre).
        void ShowTip()
        {
            string text = TipText(BtnOrder[btnFocus]);
            int w = 40 + text.Length * 10;
            int x = (int)btnBox[btnFocus].PositionX + BtnD / 2 - w / 2;
            x = Math.Max(Margin - 12, Math.Min(1920 - Margin + 12 - w, x));
            tipLabel.Text = text;
            tip.Size = new Size(w, 44);
            tipLabel.Size = new Size(w, 44);
            tip.Position = new Position(x, BtnY - 56);
            tip.Show();
        }

        void Press(Btn b)
        {
            switch (b)
            {
                case Btn.Play: TogglePause(); break;
                case Btn.Back: SeekBy(-Step); break;
                case Btn.Forward: SeekBy(Step); break;
                case Btn.Next: PlayOn(false); break;
                case Btn.Prev: if (prev != null && !leaving) PlayNext(0, prev); break;
                case Btn.Versions: OpenPanel("versions"); break;
                case Btn.Audio: OpenPanel("audio"); break;
                case Btn.Subtitles: OpenPanel("subtitles"); break;
                case Btn.Stats: OpenPanel("stats"); break;
            }
        }

        // ---- Touches ----

        public override bool OnBack()
        {
            chain = 0;
            if (asking || leaving) return false; // « Vous regardez toujours ? » : Retour arrête
            if (cardShown && onCard && panelKind == null) { guard.Run("Back", DismissCard); return true; }
            if (pauseShown) { guard.Run("Back", ShowControls); return true; }
            if (panelKind == "ear" || panelKind == "lines") { guard.Run("Back", () => OpenPanel("sync")); return true; }
            if (panelKind == "sync" || panelKind == "style") { guard.Run("Back", () => { ShowControls(); OpenPanel("subtitles"); }); return true; }
            if (panelKind == null) return false;
            guard.Run("Back", ClosePanel);
            return true;
        }

        public override bool OnKey(string key)
        {
            bool done = true;
            guard.Run("Key " + key, () => done = Keys(key));
            return done;
        }

        bool Keys(string key)
        {
            chain = 0; // quelqu'un est là
            if (okDown)
            {
                if (key == "Return" || key == "KP_Enter") return true; // répétition de la touche maintenue
                DropHold(); // une autre touche : l'appui sur OK est abandonné
            }
            if (asking) return StillKeys(key);
            if (leaving) return true; // épisode suivant en route
            // Écran de pause : toute touche le ferme, puis agit normalement (OK relance la lecture).
            if (pauseShown) HidePause();
            // Touches multimédia de la télécommande (noms supposés, à confirmer avec la sonde).
            switch (key)
            {
                case "XF86AudioPlay": if (!IsPlaying()) TogglePause(); else ShowControls(); return true;
                case "XF86AudioPause": if (IsPlaying()) TogglePause(); else ShowControls(); return true;
                case "XF86PlayBack": TogglePause(); return true;
                case "XF86AudioRewind": SeekBy(-Step); return true;
                case "XF86AudioForward": SeekBy(Step); return true;
                case "XF86AudioStop": Nav.Back(); return true;
            }
            if (failed)
            {
                // Lien en échec (vu le 2026-11-09 : « Failed to prepare the player ») : OK réessaie ;
                // fichier refusé par la TV : OK lance la version suivante.
                if (key == "Return" || key == "KP_Enter")
                {
                    if (refusedNext != null) { Source n = refusedNext; refusedNext = null; SwitchTo(n, true); return true; }
                    Release(); Open(positionMs > 0 ? positionMs : startMs, true); return true;
                }
                return false;
            }
            if (cardShown && onCard && panelKind == null) return CardKeys(key);
            if (panelKind == "versions")
            {
                switch (key)
                {
                    case "Up": if (vlist == null || !vlist.Up()) Ignored(); return true;
                    case "Down": if (vlist == null || !vlist.Down()) Ignored(); return true;
                    case "Return": case "KP_Enter": PanelChoose(); return true;
                    case "Left": case "Right": Ignored(); return true;
                }
                return false;
            }
            if (panelKind != null)
            {
                switch (key)
                {
                    case "Up": case "Down": if (menu.OnKey(key)) panelFocus = menu.Focus; else Ignored(); return true;
                    case "Return": case "KP_Enter": PanelChoose(); return true;
                    case "Left":
                    case "Right":
                    {
                        PanelItem it = panelKind != "versions" && panelFocus < panelItems.Count ? panelItems[panelFocus] : null;
                        if (it?.Step != null) { it.Step(key == "Left" ? -1 : 1); RefreshPanel(); }
                        else Ignored();
                        return true;
                    }
                }
                return false;
            }
            bool wasHidden = !controlsShown;
            switch (key)
            {
                case "Return":
                case "KP_Enter":
                    // Bouton « Passer » : OK le prend quand la barre est cachée, ou quand il est choisi.
                    if (skipUp && (wasHidden || zone == Zone.Skip)) Skip();
                    else if (zone == Zone.Buttons && !wasHidden) Press(BtnOrder[btnFocus]);
                    else if (IsPlaying() && started && !leaving) BeginPress(); // pause au relâchement, ou vitesse maintenue
                    else TogglePause();
                    return true;
                case "Left":
                    if (zone == Zone.Skip && !wasHidden) { Ignored(); ShowControls(); }
                    else if (zone == Zone.Buttons && !wasHidden) { int b = NextButton(btnFocus, -1); if (b >= 0) btnFocus = b; else Ignored(); ShowControls(); }
                    else SeekBy(-Step);
                    return true;
                case "Right":
                    if (zone == Zone.Skip && !wasHidden) { Ignored(); ShowControls(); }
                    else if (zone == Zone.Buttons && !wasHidden) { int b = NextButton(btnFocus, 1); if (b >= 0) btnFocus = b; else Ignored(); ShowControls(); }
                    else SeekBy(Step);
                    return true;
                case "Down":
                    if (!wasHidden && zone == Zone.Skip) zone = Zone.Bar;
                    else if (!wasHidden && zone == Zone.Bar) { zone = Zone.Buttons; btnFocus = 0; }
                    ShowControls();
                    return true;
                case "Up":
                    if (!wasHidden && zone == Zone.Buttons) zone = Zone.Bar;
                    else if (!wasHidden && zone == Zone.Bar && cardShown) onCard = true;
                    else if (!wasHidden && zone == Zone.Bar && skipUp) zone = Zone.Skip;
                    ShowControls();
                    return true;
            }
            return false;
        }

        static void Ignored() => App.Current.KeyIgnored();

        // ---- Vitesse maintenue (réglage du web « Held speed », module 13c) ----
        // OK pendant la lecture : la pause attend le relâchement ; gardé 0,5 s (HOLD_MS du web), la
        // vidéo passe à la vitesse choisie jusqu'au relâchement. Une autre touche abandonne l'appui.

        const int HoldMs = 500;
        readonly float holdRate = Prefs.HoldRate;
        float speed = 1f;       // vitesse en cours (horloge des sous-titres)
        bool okDown, holding;
        Timer holdTimer;        // gardé dans un champ, sinon il s'arrête

        void BeginPress()
        {
            okDown = true;
            if (holdTimer == null)
            {
                holdTimer = new Timer(HoldMs);
                holdTimer.Tick += (s, e) => { guard.Run("Hold", StartHold); return false; };
            }
            holdTimer.Stop();
            holdTimer.Start();
        }

        public override bool OnKeyUp(string key)
        {
            if (!okDown || (key != "Return" && key != "KP_Enter")) return false;
            guard.Run("KeyUp", () =>
            {
                okDown = false;
                holdTimer?.Stop();
                if (holding) EndHold();
                else TogglePause(); // appui court
            });
            return true;
        }

        void DropHold()
        {
            okDown = false;
            holdTimer?.Stop();
            if (holding) EndHold();
        }

        void StartHold()
        {
            if (!okDown || holding || !IsPlaying() || leaving) return;
            holding = true;
            Probe.Log("player", $"vitesse maintenue {holdRate} demandée à {positionMs} ms");
            SetRate(holdRate);
            Notice(Lang.T("player.held", SettingsScreen.RateText(holdRate)), 600000);
        }

        void EndHold()
        {
            holding = false;
            HideNotice();
            SetRate(1f);
        }

        // Hors du fil d'interface (les appels au lecteur bloquent, LECONS.md).
        void SetRate(float r)
        {
            Player p = player;
            if (p == null) return;
            long t0 = clock.ElapsedMilliseconds;
            Task.Run(() =>
            {
                try
                {
                    p.SetPlaybackRate(r);
                    long ms = clock.ElapsedMilliseconds - t0;
                    Ui("Rate", () =>
                    {
                        Anchor(Now());
                        speed = r;
                        Probe.Log("player", $"vitesse {r} en {ms} ms");
                    });
                }
                catch (Exception e)
                {
                    string msg = Pictures.Cut(e.GetType().Name + " " + e.Message, 80);
                    Ui("RateError", () =>
                    {
                        Probe.Log("player", $"vitesse {r} refusée : {msg}");
                        lastError = "vitesse : " + msg;
                        if (r != 1f) { holding = false; Notice(Lang.T("player.held_failed", msg), 5000); }
                    });
                }
            });
        }

        // ---- Statistiques (pages Lecture, Cache, Pistes, comme la version web) ----

        string Ms(long t, long from) => t < 0 || from < 0 ? "-" : ((t - from) / 1000.0).ToString("0.0") + " s";

        string StatsText()
        {
            switch (statsPage)
            {
                case 1:
                {
                    string state = "?";
                    try { state = player?.State.ToString() ?? "-"; } catch (Exception) { }
                    var server = new List<string>();
                    foreach (MediaStream m in source.MediaStreams ?? new MediaStream[0])
                        if (m.Type == "Video")
                            server.Add($"{m.Width}×{m.Height} {m.Codec}" + (m.Profile != null ? " " + m.Profile : "")
                                + ((m.VideoRangeType ?? m.VideoRange) != null ? ", " + (m.VideoRangeType ?? m.VideoRange) : "")
                                + (m.BitRate > 0 ? $", {m.BitRate.Value / 1000000.0:0.0} Mb/s" : ""));
                    return Lang.T("player.stats_play", state, videoInfo, string.Join(" ; ", server), source.Container ?? "-",
                        source.Bitrate > 0 ? (source.Bitrate.Value / 1000000.0).ToString("0.0") : "-",
                        Pictures.Cut(VersionName(source).Replace('\n', ' '), 80))
                        + "\n" + Lang.T("player.stats_ending", SegmentsText(), NextText(), source.Aio?.BingeGroup != null ? Pictures.Cut(source.Aio.BingeGroup, 40) : "-", ChaptersText());
                }
                case 2:
                    return Lang.T("player.stats_cache", bufferPercent, PreBuffer / 1000, ReBuffer / 1000, bufferEvents,
                        Ms(tPrepared, tOpen), Ms(tFirstFrame, tOpen), SeekText(), stalls, autoPauses, readMax,
                        reporter?.Sent ?? 0, reporter?.Failed ?? 0, lastError == "" ? "-" : lastError);
                case 3:
                    return Lang.T("player.stats_tracks", TrackList("Audio", AudioIndex()), TrackList("Subtitle", subtitleChoice < 0 || subtitleChoice >= ServerBase ? -1 : SubtitleIndex()), subtitleEvents) + "\n" + SubStats();
                default:
                    return "";
            }
        }

        // Pistes annoncées par le serveur, la piste en cours marquée (si on sait la retrouver).
        string TrackList(string type, int? current)
        {
            var sb = new StringBuilder();
            foreach (MediaStream m in ServerTracks(type))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(m.Index == current ? "  ✓ " : "     ").Append(m.DisplayTitle ?? m.Language ?? m.Codec ?? "?");
            }
            return sb.Length == 0 ? "     -" : sb.ToString();
        }

        string SeekText()
        {
            if (seekTimes.Count == 0) return "-";
            var l = new List<string>();
            for (int i = Math.Max(0, seekTimes.Count - 5); i < seekTimes.Count; i++) l.Add((seekTimes[i] / 1000.0).ToString("0.0"));
            return string.Join(", ", l) + " s";
        }

        void Note() => Measure.Note = () => Closed ? null : Lang.T("player.panel",
            Ms(tPrepared, tOpen), Ms(tFirstFrame, tOpen), SeekText(), videoInfo, readMax, guard.SlowCount, guard.Slowest(),
            lastError == "" ? "-" : lastError);
    }
}
