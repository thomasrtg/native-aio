// Accueil en « open card », repris du POC dotnet et de la version web (pages/home.tsx) :
// - une rangée par écran : « Reprendre » (cartes en largeur), puis une rangée par catalogue ;
// - la carte choisie s'ouvre sur son image en largeur, avec le logo ou le titre,
//   la ligne d'informations, puis le résumé après 300 ms ;
// - seules les rangées à ±3 de la rangée choisie existent, construites une par tour
//   de boucle après le déplacement, jamais dans le traitement d'une touche ;
// - un seul titre, une seule ligne d'informations et un seul résumé, déplacés sur
//   la carte ouverte (créer du texte est ce qui coûte le plus à la TV) ;
// - contenu des catalogues demandé au serveur, les plus proches d'abord, 20 titres
//   à la fois, la suite quand on approche de la fin ;
// - Gauche depuis la première carte d'une rangée : la barre latérale (module 13), qui
//   remplace la barre du haut (Actualiser, Changer d'utilisateur et Se déconnecter sont
//   passés dans les Paramètres) ;
// - au retour sur l'accueil, « Reprendre » et « À venir » sont redemandés sans bloquer,
//   et refaits seulement s'ils ont changé (demande de l'auteur, module 13).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Screens
{
    class HomeScreen : Screen
    {
        // Carte ouverte à sa place d'avant le module 14, à peu près au milieu de l'écran (l'auteur :
        // pas remontée ; la bande du haut reviendra à la bannière à la une, module 14b). Titre de
        // la rangée juste au-dessus, à 12 px du contour.
        const int Left = Theme.Margin, RowH = 1080, TrackY = 234, TitleH = 60, TitleY = TrackY - TitleH - 12, Gap = Theme.TileGap;
        const int PW = 288, PH = 432, WW = 528, WH = 297, OW = 883, OH = 497, Bottom = 464;
        const int BuildAround = 3, Near = 2, PageSize = 20, MoreBefore = 6, Parallel = 3, LogoW = 360, LogoH = 130;
        const int DetailsW = OW, OverviewLines = 5, Skeletons = 6;
        // Textes sous la carte ouverte : 22 et 20 pt, exception à la charte (choix de l'auteur,
        // lisibilité de loin). Titre écrit à la place d'un logo absent : 48 pt (charte).
        const float MetaSize = 22, OverviewSize = 20;
        // Titres de rangées 50 % plus grands que la charte (22 et 15 pt), demande de l'auteur.
        const float RowTitleSize = 33, RowSubSize = 23;
        const int VerticalMs = 350, HorizontalMs = 250;
        // Bannière à la une (module 14b) : quand elle a le focus, la première rangée descend juste
        // sous elle, son titre rapproché de ses cartes fermées (sinon séparés par la place de la
        // carte ouverte) ; dans les rangées, la bannière sort par le haut.
        const int HeroMax = 10, HeroPerSource = 8;
        // « Suit la carte sélectionnée » et « dans le coin » (étape 2) : la bannière reste en haut et
        // montre la carte regardée ; pas de carte ouverte (elle ne tient pas sous la bannière), les
        // cartes restent fermées, avec le contour et le grossissement de la charte, et leur titre de
        // rangée juste au-dessus. « Suit la carte » : cartes plus petites (bannière de 594 px).
        const int SmallPW = 240, SmallPH = 360, SmallWW = 432, SmallWH = 243;

        // Une rangée : « Reprendre » ou un catalogue, avec ce qui en est déjà reçu.
        class Row
        {
            public Item View; // null pour « Reprendre », « À suivre » et « À venir »
            public bool Upcoming; // « À venir » (module 11)
            public bool NextUp;   // « À suivre », quand il n'est pas réuni à « Reprendre » (module 13c)
            public readonly List<Item> Items = new List<Item>();
            public int Total;
            public bool Fetched, Fetching, Failed;
            public int Col;
            public bool Small; // « Suit la carte sélectionnée » : cartes plus petites
            public bool Wide => View == null;
            public int CW => Wide ? (Small ? SmallWW : WW) : (Small ? SmallPW : PW);
            public int CH => Wide ? (Small ? SmallWH : WH) : (Small ? SmallPH : PH);
            public int Slide => CW + Gap;
        }

        class RowView
        {
            public View Root, Track, Skeleton;
            public Animation SkeletonPulse; // gardée ici, sinon elle s'arrête
            public TextLabel Title, Sub, Message; // titre de la rangée, précision grise
            public readonly List<Card> Cards = new List<Card>();
        }

        class Card
        {
            public Item Item;
            public int Row, Col;
            public bool Dead; // rangée détruite : plus rien à afficher ni à télécharger
            public View Root, Ring, TrailerRing, Frame, LandBox, LandBack, ProgBack, ProgFill, Badge;
            public ImageView Closed, Land, Logo;
            public string ClosedUrl, LandUrl, LogoUrl, LandPath, LogoPath;
            public string HeroUrl; // image de la bannière qui suit la carte, téléchargée d'avance
            public List<string> LandCandidates; // images en largeur possibles, essayées dans l'ordre
            public int LandTry;
            public double Pct; // avancement, 100 si vu
            // Carte à ±2 du focus : son image en largeur et son logo sont confiés à la TV
            // (les autres sont seulement téléchargés sur le disque).
            public bool Near;
            public bool LandReady, LandFailed, ClosedReady, LogoReady, LogoFailed;
            public bool TitleInImage; // vignette « Thumb » : le titre est déjà dessiné dans l'image
            public long ClosedAt, LandAt; // fichier confié à la TV : mesure de sa préparation
        }


        readonly List<Row> rows = new List<Row>();
        readonly Dictionary<int, RowView> built = new Dictionary<int, RowView>();
        readonly Queue<int> toBuild = new Queue<int>();
        readonly Queue<View> toDrop = new Queue<View>();
        readonly List<Card> toRequest = new List<Card>(); // cartes dont les images restent à demander
        readonly List<Card> toShow = new List<Card>(); // cartes voisines dont les images restent à confier à la TV
        long openTexts;
        Card pendingTexts; // carte ouverte dont les textes restent à poser
        const int RequestsPerTick = 4;
        readonly Dictionary<string, Item> details = new Dictionary<string, Item>();
        // Fiche de chaque titre ouvert (genres, résumé, bandes-annonces), demandée une fois.
        readonly Dictionary<string, Task<Item>> detailTasks = new Dictionary<string, Task<Item>>();

        // Bande-annonce de la carte ouverte (module 8), comme la version web : chargée après
        // 1 s sur la carte, démarrée après 2,5 s. Pendant qu'elle se voit, la fenêtre est
        // transparente et le fond de l'écran est fait de quatre bandes autour de la carte
        // ouverte (Left, TrackY, OW, OH) : la vidéo passe sous la fenêtre.
        const int TrailerLoadMs = 1000, TrailerPlayMs = 2500, TrailerFadeMs = 500;
        CardTrailer trailer;
        Card trailerCard; // carte dont la bande-annonce se voit
        Timer trailerLoad, trailerPlay;
        Animation trailerFade;
        View[] hole;
        int trailerNone;

        View root, rowsView;
        TextLabel status, cardTitle, meta, overview, error;
        bool side; // la barre latérale a le focus
        bool trailerVisible; // la vidéo de la carte ouverte se voit
        int themeSeen = Theme.Version;
        int focusRow;
        Card opened;
        int openedRow = -1, openedCol;
        Timer builder, detailsTimer; // gardés dans des champs, sinon ils s'arrêtent
        Animation vertical, horizontal, fade;
        Func<string> statusMake;
        bool shownOnce, refreshing;
        // Paramètres, « Actualiser l'accueil » : tout est redemandé au retour sur l'accueil.
        public static bool ReloadWanted;
        bool merged; // réglage « Réunir Reprendre et À suivre » au chargement
        Hero hero; // null : bannière désactivée ou sans titre
        View heroSlot;
        bool onHero; // la bannière a le focus (ou l'aurait, hors barre latérale)
        string heroSeen; // réglage « Bannière à la une » au chargement
        bool follow; // la bannière suit la carte regardée (dans le coin ou non)
        Card lit; // carte regardée quand la bannière la suit (fermée, contour, grossie)
        Card Focused => follow ? lit : opened; // carte dont la bande-annonce peut se voir
        bool switching; // la carte regardée change (bande-annonce arrêtée par une touche)
        readonly List<Animation> grows = new List<Animation>(); // grossissements en cours, gardés
        int gen; // change à chaque rechargement : les réponses d'avant sont ignorées
        int inflight;

        // Mesures affichées sous la mesure de fluidité.
        readonly Stopwatch clock = new Stopwatch();
        string tViews = "-", tContinue = "-", tFirst = "-", tUpcoming = "-";
        int nUpcoming;
        // En millisecondes : téléchargement par l'application, de la demande à
        // l'affichage par la TV, attente vue à l'ouverture d'une carte, affiches.
        readonly List<long> landShow = new List<long>(), landWait = new List<long>(), posterShow = new List<long>();
        long openedAt;
        int webpTried, webpShown, webpRefused, landFallbacks;
        int unreadable, logosShown, logoFails, logosNone, titleInThumb, slowCount;
        readonly Stopwatch guardClock = Stopwatch.StartNew();
        readonly Dictionary<string, long> slowest = new Dictionary<string, long>();
        int gcAtStart = GC.CollectionCount(2);
        string unreadableKinds = "", lastError;
        Timer errorTimer;
        Animation pulse;
        View pulsing;

        protected override View Build()
        {
            Probe.Boot("accueil : construction");
            root = new View { Size = new Size(1920, 1080) };
            hole = new[]
            {
                new View { Position = new Position(0, 0), Size = new Size(1920, TrackY) },
                new View { Position = new Position(0, TrackY + OH), Size = new Size(1920, 1080 - TrackY - OH) },
                new View { Position = new Position(0, TrackY), Size = new Size(Left, OH) },
                new View { Position = new Position(Left + OW, TrackY), Size = new Size(1920 - Left - OW, OH) },
            };
            foreach (View h in hole)
            {
                h.BackgroundColor = App.Background;
                h.Hide();
                root.Add(h);
            }
            heroSlot = new View();
            root.Add(heroSlot);
            rowsView = new View();
            root.Add(rowsView);
            trailer = new CardTrailer(Guard) { Shown = TrailerShown, Gone = TrailerGone };


            cardMenu = new CardMenu(this, Guard) { Changed = MenuDone };
            status = Labels.Make(420, 200, Theme.SizeHeading, Theme.Text);
            status.PositionX = 0;
            status.Size = new Size(1920, 200);
            status.HorizontalAlignment = HorizontalAlignment.Center;
            root.Add(status);
            root.Add(cardMenu.View); // menu d'une carte, au-dessus de tout l'accueil

            // Les textes de la carte ouverte, créés une fois et déplacés de carte en carte.
            // Pas de voile sombre sur l'image : le titre écrit (sans logo) porte une ombre.
#pragma warning disable CS0618 // ShadowOffset/ShadowColor : dépréciés en API 8, toujours présents ; Shadow (carte de propriétés) n'est pas essayé
            cardTitle = new TextLabel
            {
                Position = new Position(24, OH - 24 - 80),
                Size = new Size(OW - 48, 80),
                PointSize = Theme.SizeHuge,
                TextColor = Color.White,
                ShadowOffset = new Vector2(2, 2),
                ShadowColor = new Color(0f, 0f, 0f, 0.9f),
            };
#pragma warning restore CS0618
            // Informations et résumé, de la largeur de la carte ; résumé sur 5 lignes au plus.
            meta = new TextLabel { Position = new Position(0, OH + 16), Size = new Size(DetailsW, 52), PointSize = MetaSize, TextColor = Theme.Text };
            overview = new TextLabel { Position = new Position(0, OH + 72), Size = new Size(DetailsW, 42 * OverviewLines), PointSize = OverviewSize, MultiLine = true, TextColor = Color.White, Opacity = 0 };

            Load();
            return root;
        }

        public override void ApplyTexts()
        {
            status.Text = statusMake?.Invoke() ?? "";
            foreach (var kv in built) SetRowTitle(kv.Value, rows[kv.Key]);
            foreach (var kv in built) if (kv.Value.Message != null) kv.Value.Message.Text = RowMessage(rows[kv.Key]);
            if (opened != null) meta.Text = MetaOf(opened);
            hero?.RefreshTexts();
            Note();
        }

        public override void OnShow()
        {
            Note();
            // Thème changé dans les Paramètres : barres d'avancement à la nouvelle couleur.
            if (themeSeen != Theme.Version)
            {
                themeSeen = Theme.Version;
                foreach (RowView rv in built.Values) foreach (Card c in rv.Cards) if (c.ProgFill != null) c.ProgFill.BackgroundColor = Theme.Accent;
            }
            // Arrière-plan d'après l'image : celui de la carte ouverte, ou du titre de la bannière.
            ArtLayer.Show(onHero || follow ? hero?.Current : opened?.Item);
            hero?.Run(onHero);
            // Retour sur l'accueil : la bande-annonce de la carte ouverte (ou suivie) repart.
            if (!side) ResumeTrailer();
            if (!shownOnce) { shownOnce = true; return; }
            if (ReloadWanted || rows.Count == 0) { ReloadWanted = false; Reload(); }
            else Refresh();
        }

        public override string SidebarSection => "home";
        public override bool ArtBackdrop => true;

        public override void OnSidebar(bool focused) => Guard("Sidebar", () =>
        {
            side = focused;
            if (onHero) hero?.Focus(!focused);
            if (opened != null) ShowRing(opened);
            if (lit?.Ring != null) { if (focused) lit.Ring.Hide(); else lit.Ring.Show(); }
            // Barre latérale : la carte n'a plus le focus, sa bande-annonce s'arrête.
            if (focused) StopTrailer();
            else if (Nav.Top == this) ResumeTrailer();
        });

        public override void OnHide() { StopTrailer(); hero?.Run(false); Measure.Note = null; cardMenu?.Hide(); }

        public override void OnAppPause() => Guard("AppPause", () => { StopTrailer(); hero?.Run(false); });

        public override void OnAppResume() => Guard("AppResume", () =>
        {
            if (!side) ResumeTrailer();
            if (Nav?.Top == this) hero?.Run(onHero);
        });

        public override void OnClose()
        {
            cardMenu?.Hide();
            StopTrailer();
            vertical?.Stop();
            hero?.Close();
            hero = null;
            gen++;
            foreach (RowView rv in built.Values) foreach (Card c in rv.Cards) c.Dead = true;
            builder?.Stop();
            detailsTimer?.Stop();
            Measure.Note = null;
        }

        // ---- Chargement ----

        async void Load()
        {
            int g = ++gen;
            Probe.Boot("accueil : chargement demandé");
            SetStatus(() => Lang.T("home.loading"));
            clock.Restart();
            merged = Prefs.MergeNextUp;
            heroSeen = Prefs.Hero;
            follow = heroSeen == "follow" || heroSeen == "corner";
            // Demandes déjà lancées par l'écran de démarrage (Prepare), si elles valent encore.
            Early pre = TakeEarly(merged);
            if (pre != null) Probe.Log("home", $"préparation reprise, lancée {Environment.TickCount - pre.Started} ms avant");
            Task<List<Item>> views = pre?.Views ?? Library.Views();
            Task<(List<Item> Continue, List<Item> NextUp)> cont = pre?.Rows ?? Library.HomeRows(merged);
            Task<List<Item>> next = pre?.Upcoming ?? Library.Upcoming();
            List<Item> v, c = null, n = null, up = null;
            try
            {
                v = await views;
                tViews = Seconds();
                Probe.Boot("accueil : catalogues reçus");
            }
            catch (ApiError e) { if (!Closed && g == gen) SetStatus(() => SignInScreen.ErrorText(e)); return; }
            catch (Exception) { if (!Closed && g == gen) SetStatus(() => Lang.T("error.unknown")); return; }
            // Titres à la une : les premiers des catalogues présentés, demandés tout de suite.
            // Quand la bannière suit la carte, inutiles : le focus est sur une carte dès l'ouverture.
            List<string> sources = heroSeen == "off" || follow ? new List<string>() : HeroSources(v);
            var heads = new Dictionary<string, Task<ItemsResult>>();
            Dictionary<string, Task<ItemsResult>> ready = null;
            if (pre != null && pre.Hero == heroSeen && sources.Count > 0)
            {
                try { ready = await pre.Heads; } catch (Exception) { }
                if (Closed || g != gen) return;
            }
            foreach (string s in sources)
                if (s.StartsWith("view:"))
                {
                    if (ready != null && ready.TryGetValue(s, out Task<ItemsResult> t0)) { heads[s] = t0; continue; }
                    Item view = v.Find(x => "view:" + x.Id == s);
                    if (view != null) heads[s] = Library.Heads(view, HeroPerSource);
                }
            // « Reprendre » en panne n'empêche pas d'afficher les catalogues.
            try { (c, n) = await cont; tContinue = Seconds(); }
            catch (Exception) { tContinue = "×"; }
            // « À venir » non plus (rangée absente si vide ou en panne).
            try { up = await next; tUpcoming = Seconds(); nUpcoming = up.Count; }
            catch (Exception) { tUpcoming = "×"; }
            Probe.Boot("accueil : Reprendre et À venir reçus");
            if (Closed || g != gen) return;
            Note();

            if (c != null && c.Count > 0)
            {
                var row = new Row { Fetched = true, Total = c.Count };
                row.Items.AddRange(c);
                rows.Add(row);
            }
            if (n != null && n.Count > 0)
            {
                var row = new Row { Fetched = true, Total = n.Count, NextUp = true };
                row.Items.AddRange(n);
                rows.Add(row);
            }
            // Juste après « Reprendre » et « À suivre », comme la version web.
            if (up != null && up.Count > 0)
            {
                var row = new Row { Fetched = true, Total = up.Count, Upcoming = true };
                row.Items.AddRange(up);
                rows.Add(row);
            }
            foreach (Item view in v) rows.Add(new Row { View = view });
            if (rows.Count == 0) { SetStatus(() => Lang.T("home.empty")); return; }
            foreach (Row r in rows) r.Small = follow; // même taille dans les deux modes (l'auteur)
            SetStatus(null);
            bool withHero = sources.Count > 0;
            Guard("Load", () =>
            {
                if (follow) { MakeFollowHero(); SetFocus(0, 0); }
                else if (withHero) MakeHero(); else SetFocus(0, 0);
                if (rows[0].Fetched) FirstShown();
            });
            if (!withHero || hero == null) return;
            // Les titres de chaque source, mêlés (interleave du web), 10 au plus.
            var lists = new List<List<Item>>();
            foreach (string s in sources)
            {
                var l = new List<Item>();
                if (s == "resume" && c != null) l.AddRange(c);
                else if (s == "next-up" && n != null) l.AddRange(n);
                else if (heads.TryGetValue(s, out Task<ItemsResult> t))
                {
                    try { ItemsResult r = await t; if (r?.Items != null) l.AddRange(r.Items); }
                    catch (Exception) { }
                }
                if (l.Count > HeroPerSource) l.RemoveRange(HeroPerSource, l.Count - HeroPerSource);
                lists.Add(l);
            }
            if (Closed || g != gen || hero == null) return;
            var picked = new List<Item>();
            var seen = new HashSet<string>();
            for (int i = 0; picked.Count < HeroMax; i++)
            {
                bool any = false;
                foreach (List<Item> l in lists)
                {
                    if (i >= l.Count) continue;
                    any = true;
                    Item it = l[i];
                    if (it?.Id == null || seen.Contains(it.Id) || picked.Count >= HeroMax) continue;
                    seen.Add(it.Id);
                    picked.Add(it);
                }
                if (!any) break;
            }
            Guard("Hero", () =>
            {
                hero.SetItems(picked);
                Probe.Log("hero", picked.Count + " titres reçus, " + hero.Count + " avec une image, " + Seconds() + " s");
                if (hero.Count > 0) { if (onHero) { hero.Focus(!side); hero.Run(Nav?.Top == this); ArtLayer.Show(hero.Current); } return; }
                // Aucun titre à présenter : pas de bannière.
                bool was = onHero;
                hero.Stop();
                hero = null;
                onHero = false;
                if (was) SetFocus(0, rows[0].Col);
            });
        }

        // Sources des titres à la une (pages/home.tsx du web) : le réglage du compte, sans les
        // catalogues retirés ; automatique (ou plus aucun catalogue choisi) : premier catalogue de
        // films et premier de séries, sinon le premier catalogue. « Rien » : aucune.
        // ---- Préparation au démarrage (module 16) ----
        // Demandes de l'accueil lancées par l'écran de démarrage dès que le serveur a répondu, pour
        // l'utilisateur en cours, pendant la traînée et « Qui regarde ? » (gain mesuré : ~1 s sur la
        // première rangée). Reprises une seule fois par le premier chargement de l'accueil, si
        // l'utilisateur et le réglage « Reprendre » n'ont pas changé et si elles ont moins d'une minute ;
        // jetées sinon (autre utilisateur choisi : réponses inutiles).
        sealed class Early
        {
            public string User, Hero;
            public bool Merged;
            public int Started;
            public Task<List<Item>> Views, Upcoming;
            public Task<(List<Item> Continue, List<Item> NextUp)> Rows;
            public Task<Dictionary<string, Task<ItemsResult>>> Heads;
        }

        const int EarlyMaxMs = 60000;
        static Early early;

        public static void Prepare()
        {
            early = null;
            if (Session.UserId == null) return;
            var e = new Early
            {
                User = Session.UserId,
                Merged = Prefs.MergeNextUp,
                Hero = Prefs.Hero,
                Started = Environment.TickCount,
            };
            e.Views = Library.Views();
            e.Rows = Library.HomeRows(e.Merged);
            e.Upcoming = Library.Upcoming();
            e.Heads = EarlyHeads(e.Views, e.Hero);
            early = e;
            Probe.Boot("accueil : préparation lancée");
        }

        static Early TakeEarly(bool merged)
        {
            Early e = early;
            early = null;
            if (e == null) return null;
            if (e.User != Session.UserId || e.Merged != merged || Environment.TickCount - e.Started > EarlyMaxMs)
            {
                Probe.Log("home", "préparation jetée (autre utilisateur, réglage changé ou trop ancienne)");
                return null;
            }
            return e;
        }

        // Premiers titres des catalogues de la bannière, demandés dès l'arrivée des catalogues.
        static async Task<Dictionary<string, Task<ItemsResult>>> EarlyHeads(Task<List<Item>> views, string heroPref)
        {
            var heads = new Dictionary<string, Task<ItemsResult>>();
            if (heroPref == "off" || heroPref == "follow" || heroPref == "corner") return heads;
            List<Item> v = await views;
            foreach (string s in HeroSources(v))
                if (s.StartsWith("view:"))
                {
                    Item view = v.Find(x => "view:" + x.Id == s);
                    if (view != null) heads[s] = Library.Heads(view, HeroPerSource);
                }
            return heads;
        }

        static List<string> HeroSources(List<Item> views)
        {
            List<string> f = Prefs.Featured;
            if (f != null)
            {
                var live = f.FindAll(s => !s.StartsWith("view:") || views.Exists(x => "view:" + x.Id == s));
                if (live.Count > 0 || f.Count == 0) return live;
            }
            var ids = new List<string>();
            foreach (string kind in new[] { "movies", "tvshows" })
            {
                Item x = views.Find(w => w.CollectionType == kind);
                if (x != null) ids.Add("view:" + x.Id);
            }
            if (ids.Count == 0 && views.Count > 0) ids.Add("view:" + views[0].Id);
            return ids;
        }

        // Bannière créée vide (ses titres arrivent ensuite) ; l'accueil s'ouvre sur elle.
        void MakeHero()
        {
            hero = new Hero(Guard)
            {
                // Arrière-plan d'après l'image changé en même temps que le titre de la bannière.
                Shown = (it, ready) => { if (onHero && !Closed && Nav?.Top == this) ArtLayer.ShowWith(it, ready); else ready(null); },
                PlayIt = it => Nav.Push(new VersionsScreen(it, it.UserData?.Played == true ? 0 : it.UserData?.PlaybackPositionTicks ?? 0, null)),
                OpenIt = it => Nav.Push(ItemScreen.For(it)),
            };
            heroSlot.Add(hero.Root);
            FocusHero();
        }

        // Bannière qui suit la carte regardée : fixe en haut, sans focus ; les rangées passent dessous.
        void MakeFollowHero()
        {
            hero = new Hero(Guard, heroSeen == "corner" ? Hero.Kind.Corner : Hero.Kind.Follow)
            {
                // Arrière-plan d'après l'image changé en même temps que le titre de la bannière.
                Shown = (it, ready) => { if (!Closed && Nav?.Top == this) ArtLayer.ShowWith(it, ready); else ready(null); },
            };
            // Bande-annonce minutée à partir du titre complet (entré, image affichée), pas de l'appui.
            hero.Complete = it => Guard("HeroComplete", () =>
            {
                Card c = lit;
                if (c == null || c.Item.Id != it?.Id || side || Nav?.Top != this) return;
                ScheduleTrailer(c);
            });
            heroSlot.Add(hero.Root);
            rowsView.PositionY = FollowTop; // première rangée posée sous la bannière, sans glisser
        }

        // Rangée regardée sous la bannière qui suit la carte : son titre 8 px au-dessus du bas de la
        // bannière (remontée de 24 px à la demande de l'auteur : cartes trop basses).
        int FollowTop => (hero?.H ?? 0) - 8 - TitleY;

        // Haut de l'image d'une carte fermée dans sa rangée : alignée en bas de la place de la
        // carte ouverte, ou, quand la bannière suit la carte, juste sous le titre de la rangée.
        int FrameY(Row row) => follow ? 0 : Bottom - row.CH;

        // Bannière qui suit la carte : cartes descendues de ce que la carte regardée gagne en
        // grossissant (×1,06 autour de son centre), pour garder 12 px entre le titre de la rangée
        // et la carte regardée, comme au-dessus de la carte ouverte (remarque de l'auteur).
        int TrackTop(Row row) => TrackY + (follow ? (int)Math.Ceiling(row.CH * (Theme.Grow - 1) / 2) : 0);

        // Focus sur la bannière : elle revient en haut, la première rangée dessous, fermée.
        void FocusHero()
        {
            onHero = true;
            focusRow = 0;
            CloseCard();
            Sync();
            var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
            vertical?.Stop();
            vertical = new Animation(VerticalMs);
            int lift = Lift(rows[0]);
            vertical.AnimateTo(rowsView, "PositionY", (float)(Hero.Height + 8 - TitleY - lift), ease);
            vertical.AnimateTo(hero.Root, "PositionY", 0f, ease);
            if (built.TryGetValue(0, out RowView rv0)) PlaceTitle(rv0, lift, vertical, ease);
            vertical.Play();
            Pump();
            hero.Focus(!side);
            hero.Run(Nav?.Top == this);
            if (hero.Current != null) ArtLayer.Hover(hero.Current);
        }

        // Sous la bannière : titre juste au-dessus des cartes fermées (en bas de la place de la
        // carte ouverte).
        static int Lift(Row row) => Bottom - row.CH;

        static void PlaceTitle(RowView rv, int lift, Animation anim, AlphaFunction ease)
        {
            anim.AnimateTo(rv.Title, "PositionY", (float)(TitleY + lift), ease);
            if (rv.Sub != null) anim.AnimateTo(rv.Sub, "PositionY", (float)(TitleY - 3 + lift), ease);
        }

        string Seconds() => (clock.ElapsedMilliseconds / 1000.0).ToString("0.0");

        void FirstShown()
        {
            if (tFirst != "-") return;
            tFirst = Seconds();
            Probe.Boot("accueil : première rangée posée");
            Probe.BootFrame("accueil : première rangée, image affichée");
            Note();
        }

        // Demande le contenu des catalogues, les plus proches de la rangée choisie d'abord.
        void Pump()
        {
            while (inflight < Parallel)
            {
                int best = -1;
                for (int i = 0; i < rows.Count; i++)
                {
                    Row r = rows[i];
                    if (r.Fetched || r.Fetching || r.Failed) continue;
                    if (best < 0 || Math.Abs(i - focusRow) < Math.Abs(best - focusRow)) best = i;
                }
                if (best < 0) return;
                Fetch(best);
            }
        }

        async void Fetch(int index)
        {
            int g = gen;
            Row row = rows[index];
            row.Fetching = true;
            row.Failed = false;
            inflight++;
            ShowRowMessage(index);
            ItemsResult page;
            try { page = await Library.Page(row.View, row.Items.Count, PageSize); }
            catch (Exception)
            {
                if (Closed || g != gen) return;
                if (row.Items.Count == 0) row.Failed = true;
                else row.Total = row.Items.Count; // la suite ne vient pas : on s'arrête là
                row.Fetching = false;
                inflight--;
                Guard("Fetch", () => { ShowRowMessage(index); Pump(); });
                return;
            }
            if (Closed || g != gen) return;
            row.Fetching = false;
            inflight--;
            Guard("Fetch", () =>
            {
                int from = row.Items.Count;
                if (page?.Items != null) row.Items.AddRange(page.Items);
                row.Total = page?.Items == null || page.Items.Length == 0 ? row.Items.Count : Math.Max(page.TotalRecordCount, row.Items.Count);
                row.Fetched = true;
                if (built.TryGetValue(index, out RowView rv))
                {
                    for (int i = from; i < row.Items.Count; i++) AddCard(row, rv, i);
                    ShowRowMessage(index);
                    if (index == focusRow && opened == null && lit == null && !onHero && rv.Cards.Count > 0) SetFocus(index, Math.Min(row.Col, rv.Cards.Count - 1));
                }
                if (index == focusRow && row.Items.Count > 0) FirstShown();
                Pump();
            });
        }

        // ---- Rangées ----

        // Titre et précision (type du catalogue), comme le web : « Tendances  Films ».
        (string Title, string Sub) RowTitle(Row r)
        {
            if (r.Upcoming) return (Lang.T("home.upcoming"), null);
            if (r.NextUp) return (Lang.T("home.next_up"), null);
            if (r.View == null) return (Lang.T("home.continue"), null);
            string kind = null;
            switch (r.View.CollectionType)
            {
                case "movies": kind = Lang.T("library.movies"); break;
                case "tvshows": kind = Lang.T("library.shows"); break;
                case "boxsets": kind = Lang.T("library.collections"); break;
            }
            return (r.View.Name ?? "", kind);
        }

        // Titre en 33 pt blanc (demi-gras comme le web, à juger sur photo), précision en 23 pt
        // grise, 16 px après, sur la même ligne d'écriture (bas des textes alignés ; la précision
        // remontée de 3 px pour compenser sa plus petite descente, estimé).
        void SetRowTitle(RowView rv, Row row)
        {
            var (title, sub) = RowTitle(row);
            rv.Title.Text = title;
            if (sub == null) { rv.Sub?.Hide(); return; }
            if (rv.Sub == null)
            {
                rv.Sub = new TextLabel { Size = new Size(700, TitleH), PositionY = rv.Title.PositionY - 3, PointSize = RowSubSize, TextColor = Theme.Grey, VerticalAlignment = VerticalAlignment.Bottom };
                rv.Root.Add(rv.Sub);
            }
            rv.Sub.Text = sub;
            float w = rv.Title.NaturalSize2D.Width;
            if (w <= 0) w = title.Length * 20;
            rv.Sub.PositionX = Left + w + 16;
            rv.Sub.Show();
        }

        string RowMessage(Row r)
        {
            if (r.Failed) return Lang.T("home.row_failed");
            if (!r.Fetched) return Lang.T("home.row_loading");
            if (r.Items.Count == 0) return Lang.T("home.row_empty");
            return "";
        }

        void ShowRowMessage(int index)
        {
            if (!built.TryGetValue(index, out RowView rv)) return;
            Row row = rows[index];
            ShowSkeleton(row, rv, !row.Fetched && !row.Failed);
            string text = row.Fetched || row.Failed ? RowMessage(row) : "";
            if (text.Length == 0)
            {
                if (rv.Message != null) rv.Message.Hide();
                return;
            }
            if (rv.Message == null)
            {
                rv.Message = new TextLabel { Position = new Position(Left, TrackY + 180), Size = new Size(1600, 60), PointSize = Theme.SizeBody, TextColor = Theme.Grey };
                rv.Root.Add(rv.Message);
            }
            rv.Message.Text = text;
            rv.Message.Show();
        }

        // Cartes grises à la place des cartes, qui pulsent ensemble tant que la rangée charge.
        void ShowSkeleton(Row row, RowView rv, bool on)
        {
            if (!on)
            {
                if (rv.Skeleton == null) return;
                rv.SkeletonPulse?.Stop();
                rv.SkeletonPulse = null;
                rv.Skeleton.Unparent();
                rv.Skeleton.Dispose();
                rv.Skeleton = null;
                return;
            }
            if (rv.Skeleton != null) return;
            rv.Skeleton = new View { Position = new Position(Left, TrackTop(row)) };
            for (int i = 0; i < Skeletons; i++)
                rv.Skeleton.Add(new View
                {
                    Position = new Position(i * row.Slide, FrameY(row)),
                    Size = new Size(row.CW, row.CH),
                    BackgroundColor = Theme.Placeholder,
                }.Rounded(Radius(row)));
            rv.Root.Add(rv.Skeleton);
            rv.SkeletonPulse = new Animation(1200) { Looping = true };
            rv.SkeletonPulse.AnimateTo(rv.Skeleton, "Opacity", 0.4f, new AlphaFunction(AlphaFunction.BuiltinFunctions.Sin));
            rv.SkeletonPulse.Play();
        }

        RowView BuildRow(int r)
        {
            Row row = rows[r];
            var rv = new RowView
            {
                Root = new View { Position = new Position(0, r * RowH), Size = new Size(1920, RowH) },
                Track = new View { Position = new Position(Left - row.Col * row.Slide, TrackTop(row)) },
                Title = new TextLabel { Position = new Position(Left, TitleY), Size = new Size(1700, TitleH), PointSize = RowTitleSize, TextColor = Color.White, VerticalAlignment = VerticalAlignment.Bottom, FontStyle = Theme.SemiBold() },
            };
            rv.Root.Add(rv.Title);
            if (onHero && r == 0) rv.Title.PositionY = TitleY + Lift(row);
            SetRowTitle(rv, row);
            rv.Root.Add(rv.Track);
            for (int i = 0; i < row.Items.Count; i++) AddCard(row, rv, i);
            rowsView.Add(rv.Root);
            built[r] = rv;
            ShowRowMessage(r);
            return rv;
        }

        void AddCard(Row row, RowView rv, int i)
        {
            Item it = row.Items[i];
            int cw = row.CW, ch = row.CH;
            var card = new Card { Item = it, Row = rows.IndexOf(row), Col = i, Root = new View { Position = new Position(i * row.Slide, 0) } };
            // Bannière qui suit la carte : grossissement autour du centre (comme Découvrir).
            if (follow)
            {
                card.Root.Size = new Size(cw, ch);
                card.Root.PivotPoint = Tizen.NUI.PivotPoint.Center;
                card.Root.PositionUsesPivotPoint = false;
            }
            card.Frame = new View
            {
                Position = new Position(0, FrameY(row)),
                Size = new Size(cw, ch),
                BackgroundColor = Theme.Placeholder,
                ClippingMode = ClippingModeType.ClipChildren,
            }.Rounded(Radius(row));
            card.Pct = it.UserData?.Played == true ? 100 : Library.Progress(it);
            if (card.Pct > 0)
            {
                card.ProgBack = new View { BackgroundColor = new Color(0f, 0f, 0f, 0.6f) }.Rounded(ProgH / 2);
                card.ProgFill = new View { BackgroundColor = Theme.Accent }.Rounded(ProgH / 2);
                card.Frame.Add(card.ProgBack);
                card.Frame.Add(card.ProgFill);
                PlaceProgress(card, cw, ch, Radius(row));
            }
            card.Root.Add(card.Frame);
            rv.Track.Add(card.Root);
            rv.Cards.Add(card);
            // Carte ajoutée après une carte ouverte : décalée comme les autres.
            if (opened != null && card.Row == openedRow && i > openedCol) card.Root.PositionX = i * row.Slide + (OW - cw);

            // Images demandées au chargeur, chacune avec sa priorité (relue à chaque
            // téléchargement libéré). Dans « Reprendre », la carte fermée et la carte
            // ouverte montrent la même image en largeur : un seul fichier.
            card.LandCandidates = Library.LandscapeCandidates(it, OW);
            card.LandUrl = card.LandCandidates.Count > 0 ? card.LandCandidates[0] : null;
            card.TitleInImage = it.Type != "Episode" && it.ImageTags?.Thumb != null;
            card.LogoUrl = card.TitleInImage ? null : Library.Logo(it, LogoW);
            // Pas de carte ouverte : ni logo ni image en largeur à télécharger pour les affiches.
            if (follow)
            {
                card.LogoUrl = null;
                if (!row.Wide) { card.LandUrl = null; card.LandCandidates.Clear(); }
                // Image de la bannière téléchargée d'avance (seulement téléchargée : décodée par la
                // bannière quand le titre est montré), comme les images en largeur sans bannière.
                card.HeroUrl = Hero.BackdropUrl(it);
            }
            card.ClosedUrl = row.Wide ? card.LandUrl : Library.Poster(it, cw);
            if (card.ClosedUrl == null) card.ClosedReady = true;
            if (card.LandUrl == null) card.LandFailed = true;
            if (card.LogoUrl == null) card.LogoFailed = true;
            // Demandes faites plus tard, quelques cartes par tour de boucle (BuildStep) :
            // une rangée entière d'un coup bloquait l'interface 100 à 260 ms.
            toRequest.Add(card);
            EnsureBuilder();
        }

        // Partie « ouverte » d'une carte, créée à la demande (carte voisine ou ouverte) :
        // cadre blanc, et fond gris (qui pulse tant que l'image en largeur n'est pas là)
        // sur lequel s'ajoutent l'image en largeur et le logo. Construire ces vues pour
        // toutes les cartes d'une rangée coûtait cher à chaque nouvelle rangée.
        void EnsureOpenParts(Card card)
        {
            if (card.LandBox != null) return;
            // Contour de la charte (comme Favoris) : plein, posé sous la carte arrondie.
            card.Ring = Theme.FocusRing(OW, OH, Theme.RadiusLarge);
            card.Ring.Position = new Position(-Theme.RingOut, -Theme.RingOut);
            card.Ring.Hide();
            card.Root.Add(card.Ring);
            card.Ring.LowerToBottom();
            // Pendant la bande-annonce, la carte est transparente et un contour plein cacherait
            // la vidéo : contour en image, transparent au centre, dont le liseré remplit aussi les
            // coins hors de l'arrondi et cache les coins carrés de la vidéo (tools/card_ring.py).
            card.TrailerRing = new ImageView(Tizen.Applications.Application.Current.DirectoryInfo.Resource + "images/card_ring.png")
            {
                Position = new Position(-Theme.RingOut, -Theme.RingOut),
                Size = new Size(OW + 2 * Theme.RingOut, OH + 2 * Theme.RingOut),
            };
            card.TrailerRing.Hide();
            card.Root.Add(card.TrailerRing);
            card.LandBox = new View { Size = new Size(OW, OH) };
            card.LandBack = new View { Size = new Size(OW, OH), BackgroundColor = Theme.Placeholder }.Rounded(Theme.RadiusLarge);
            card.LandBox.Add(card.LandBack);
            card.LandBox.Hide();
            card.Frame.Add(card.LandBox);
            card.ProgBack?.RaiseToTop();
            card.ProgFill?.RaiseToTop();
            card.Badge?.RaiseToTop();
        }

        // « À venir » : date de sortie en pastille en haut à gauche de la carte (comme le web),
        // faite avec les demandes d'images, quelques cartes par tour de boucle.
        void MakeBadge(Card card)
        {
            if (card.Badge != null || !rows[card.Row].Upcoming) return;
            DateTime? day = Library.AirDay(card.Item.PremiereDate);
            if (day == null) return;
            var label = new TextLabel
            {
                Position = new Position(12, 0),
                PointSize = Theme.SizeSmall,
                TextColor = Color.White,
                VerticalAlignment = VerticalAlignment.Center,
                Text = Library.DayDate(day.Value),
            };
            float tw = label.NaturalSize2D.Width;
            if (tw <= 0) tw = label.Text.Length * 9;
            label.Size = new Size((int)tw + 2, 32);
            // Fond sombre et non blanc à 16 % (charte) : lisible sur une image claire.
            card.Badge = new View
            {
                Position = new Position(12, 12),
                Size = new Size((int)tw + 26, 32),
                BackgroundColor = Theme.Panel,
            }.Rounded(16);
            card.Badge.Add(label);
            card.Frame.Add(card.Badge);
        }

        // Ligne d'informations de la carte ouverte ; « À venir » : date et délai devant.
        string MetaOf(Card card)
        {
            string m = Library.Meta(card.Item);
            if (!rows[card.Row].Upcoming) return m;
            DateTime? day = Library.AirDay(card.Item.PremiereDate);
            if (day == null) return m;
            string when = Library.DayDate(day.Value) + "  ·  " + Library.Until(day.Value);
            string time = Library.AirTime(card.Item.PremiereDate);
            if (time != null) when += "  ·  " + time;
            return m.Length > 0 ? when + "  ·  " + m : when;
        }

        void RequestImages(Card card)
        {
            MakeBadge(card);
            if (card.ClosedUrl != null)
                ImageCache.Request(card.ClosedUrl, () => PosterPriority(card), path => Guard("Poster", () => OnPosterFile(card, path)));
            if (card.LandUrl != null)
                ImageCache.Request(card.LandUrl, () => LandPriority(card), path => Guard("Land", () => OnLandFile(card, path)));
            if (card.LogoUrl != null)
                ImageCache.Request(card.LogoUrl, () => LandPriority(card), path => Guard("Logo", () => OnLogoFile(card, path)));
            if (card.HeroUrl != null)
                ImageCache.Request(card.HeroUrl, () => HeroPriority(card), _ => { });
        }

        // Images de la bannière qui suit la carte : la rangée regardée (8 cartes de chaque côté) et
        // les rangées voisines (4 cartes), après les affiches visibles ; plus loin, abandonnées
        // (la bannière les demande elle-même au besoin).
        int HeroPriority(Card k)
        {
            if (k.Dead || Closed) return int.MaxValue;
            int dr = Math.Abs(k.Row - focusRow), dc = Math.Abs(k.Col - rows[k.Row].Col);
            if (dr > 1 || dc > (dr == 0 ? 8 : 4)) return int.MaxValue;
            return dr * 100 + dc * 6 + 10;
        }

        // ---- Priorités des images (plus petit = plus tôt) ----
        // Carte regardée : image en largeur et logo d'abord. Puis, dans la rangée
        // regardée, les affiches visibles et les images en largeur des voisines ;
        // puis le reste de la rangée ; puis les rangées voisines, de la même façon.

        int PosterPriority(Card k)
        {
            if (k.Dead || Closed) return int.MaxValue;
            int dr = Math.Abs(k.Row - focusRow), dc = k.Col - rows[k.Row].Col;
            if (dr > BuildAround) return int.MaxValue;
            bool visible = dc >= -1 && dc <= 6;
            return dr * 100 + (visible ? dc + 2 : 40 + Math.Abs(dc));
        }

        int LandPriority(Card k)
        {
            if (k.Dead || Closed) return int.MaxValue;
            int dr = Math.Abs(k.Row - focusRow), dc = Math.Abs(k.Col - rows[k.Row].Col);
            if (dr > BuildAround) return int.MaxValue;
            if (dr == 0 && dc == 0) return 0;
            return dr * 100 + dc * 6 + 1;
        }

        // ---- Images reçues (fichier sur le disque, ou null en cas d'échec) ----

        // Affiche de la carte fermée, décodée à la taille affichée, sous le reste de la carte.
        void OnPosterFile(Card card, string path)
        {
            if (card.Dead) return;
            if (path == null) { card.ClosedReady = true; return; }
            Row row = rows[card.Row];
            card.Closed = Decoded(path, row.CW, row.CH, "", Radius(row));
            if (card.Closed == null) { card.ClosedReady = true; return; }
            card.ClosedAt = Now;
            card.Closed.ResourceReady += (s, e) =>
            {
                if (card.ClosedReady) return;
                card.ClosedReady = true;
                posterShow.Add(Now - card.ClosedAt);
            };
            card.Frame.Add(card.Closed);
            card.Closed.LowerToBottom();
            if (card == opened) card.Closed.Hide();
        }

        void OnLandFile(Card card, string path)
        {
            if (card.Dead) return;
            if (path == null)
            {
                // Image introuvable sur le serveur : on essaie la suivante (comme le web).
                if (card.LandCandidates != null && card.LandTry + 1 < card.LandCandidates.Count)
                {
                    card.LandTry++;
                    card.LandUrl = card.LandCandidates[card.LandTry];
                    landFallbacks++;
                    ImageCache.Request(card.LandUrl, () => LandPriority(card), p => Guard("Land", () => OnLandFile(card, p)));
                    return;
                }
                LandDone(card, false);
                return;
            }
            card.LandPath = path;
            // « Reprendre » : la carte fermée montre la même image en largeur.
            if (rows[card.Row].Wide && card.Closed == null && card.LandTry > 0) OnPosterFile(card, path);
            if (card.Near) ShowLand(card);
        }

        void OnLogoFile(Card card, string path)
        {
            if (card.Dead) return;
            card.LogoPath = path;
            if (path == null) { card.LogoFailed = true; ShowTitle(card); return; }
            if (card.Near) ShowLogo(card);
        }

        // Image en largeur confiée à la TV, décodée à la taille de la carte ouverte,
        // au-dessus du fond gris.
        void ShowLand(Card card)
        {
            if (card.Land != null || card.LandPath == null) return;
            EnsureOpenParts(card);
            card.Land = Decoded(card.LandPath, OW, OH, "", Theme.RadiusLarge);
            if (card.Land == null) { LandDone(card, false); return; }
            card.LandAt = Now;
            card.Land.ResourceReady += (s, e) => Guard("Land", () => LandDone(card, card.Land.LoadingStatus == ImageView.LoadingStatusType.Ready));
            card.LandBox.Add(card.Land);
            card.Land.LowerToBottom();
            card.LandBack.LowerToBottom();
        }

        // Fichier confié à la TV, décodé à la taille affichée. Null si la TV ne sait pas
        // le lire : format non pris en charge (WebP…), ou fichier que NUI refuse en levant
        // une division par zéro au réglage de la taille de décodage (vu sur la TV).
        // Images arrondies au décodage (charte), comme Favoris ; radius 0 pour un logo.
        ImageView Decoded(string path, int w, int h, string what = "", int radius = 0)
        {
            string ext = System.IO.Path.GetExtension(path);
            // Essai : le WebP est confié à la TV tel quel, sans taille de décodage (elle
            // lèverait une division par zéro si elle ne lit pas l'en-tête). Le panneau
            // compte les WebP affichés et refusés.
            if (ext == ".webp")
            {
                var wv = new ImageView(path) { Size = new Size(w, h) }.Rounded(radius);
                webpTried++;
                bool counted = false;
                wv.ResourceReady += (s, e) =>
                {
                    if (counted) return;
                    counted = true;
                    if (wv.LoadingStatus == ImageView.LoadingStatusType.Ready) webpShown++; else webpRefused++;
                };
                return wv;
            }
            if (ext != ".jpg" && ext != ".png") { Unreadable(what + ext); return null; }
            var v = new ImageView(path) { Size = new Size(w, h) }.Rounded(radius);
            try
            {
                v.DesiredWidth = w;
                v.DesiredHeight = h;
                return v;
            }
            catch (DivideByZeroException)
            {
                v.Dispose();
                // Fichier présent mais refusé : effacé, il sera retéléchargé au prochain passage.
                bool there = System.IO.File.Exists(path);
                Unreadable(what + ext + " ÷0 " + (there ? Lang.T("home.file_present") : Lang.T("home.file_missing")));
                if (there) ImageCache.Forget(path);
                return null;
            }
        }

        void Unreadable(string kind)
        {
            unreadable++;
            if (!unreadableKinds.Contains("[" + kind + "]")) unreadableKinds += "[" + kind + "] ";
        }

        // Logo : sa taille est lue dans l'en-tête du fichier (PNG ou JPEG), puis il est
        // confié à la TV déjà à la taille affichée. On ne dépend plus de la taille
        // annoncée par la TV après chargement (souvent nulle quand la carte est fermée).
        void ShowLogo(Card card)
        {
            if (card.Logo != null || card.LogoPath == null || card.LogoFailed) return;
            EnsureOpenParts(card);
            int nw, nh;
            Pictures.ImageSize(card.LogoPath, out nw, out nh);
            if (nw <= 0 || nh <= 0) { LogoFail(card, "logo " + System.IO.Path.GetExtension(card.LogoPath) + " ?"); return; }
            float k = Math.Min(1f, Math.Min((float)LogoW / nw, (float)LogoH / nh));
            int w = Math.Max(1, (int)(nw * k)), h = Math.Max(1, (int)(nh * k));
            card.Logo = Decoded(card.LogoPath, w, h, "logo ");
            if (card.Logo == null) { LogoFail(card, null); return; }
            card.Logo.Position = new Position(24, OH - 24 - h);
            card.Logo.ResourceReady += (s, e) => Guard("Logo", () =>
            {
                if (card.LogoReady || card.LogoFailed) return;
                if (card.Logo.LoadingStatus == ImageView.LoadingStatusType.Ready) { card.LogoReady = true; logosShown++; }
                else LogoFail(card, "logo " + System.IO.Path.GetExtension(card.LogoPath) + " ×");
            });
            card.LandBox.Add(card.Logo);
            if (card == opened) cardTitle.RaiseToTop();
        }

        void LogoFail(Card card, string kind)
        {
            if (card.LogoFailed) return;
            card.LogoFailed = true;
            logoFails++;
            if (kind != null) Unreadable(kind);
            ShowTitle(card);
        }

        // Barre d'avancement collée au bas de la carte, arrondie aux bouts et raccourcie de
        // (arrondi de la carte - ProgH / 2) de chaque côté : elle épouse les coins arrondis sans
        // en dépasser (la TV ne coupe pas ce qui dépasse).
        const int ProgH = 8;

        static void PlaceProgress(Card card, int w, int h, int radius)
        {
            if (card.ProgBack == null) return;
            int inset = Math.Max(0, radius - ProgH / 2), bw = w - 2 * inset;
            card.ProgBack.Position = new Position(inset, h - ProgH);
            card.ProgBack.Size = new Size(bw, ProgH);
            card.ProgFill.Position = new Position(inset, h - ProgH);
            card.ProgFill.Size = new Size(Math.Max(ProgH, (int)(bw * card.Pct / 100)), ProgH);
        }

        static int Radius(Row row) => row.Wide ? Theme.RadiusLarge : Theme.RadiusSmall;

        // Contour de la carte ouverte : plein, ou en image transparente au centre pendant que sa
        // vidéo se voit ; aucun quand la barre latérale a le focus.
        void ShowRing(Card card)
        {
            if (card.Ring == null) return;
            bool on = card == opened && !side;
            bool video = on && trailerVisible && trailerCard == card;
            if (on && !video) card.Ring.Show(); else card.Ring.Hide();
            if (video) card.TrailerRing.Show(); else card.TrailerRing.Hide();
        }

        // Les rangées qui sortent de la zone de ±3 sont cachées puis détruites, celles
        // qui y entrent construites, une par tour de boucle après le déplacement.
        void Sync()
        {
            var gone = new List<int>();
            foreach (int k in built.Keys) if (Math.Abs(k - focusRow) > BuildAround) gone.Add(k);
            foreach (int k in gone)
            {
                if (k == openedRow) { StopPulse(); opened = null; openedRow = -1; }
                if (lit != null && lit.Row == k) lit = null;
                built[k].SkeletonPulse?.Stop();
                foreach (Card c in built[k].Cards) c.Dead = true;
                built[k].Root.Hide();
                toDrop.Enqueue(built[k].Root);
                built.Remove(k);
            }
            if (!built.ContainsKey(focusRow)) BuildRow(focusRow);
            toBuild.Clear();
            for (int d = 1; d <= BuildAround; d++)
                foreach (int r in new[] { focusRow + d, focusRow - d })
                    if (r >= 0 && r < rows.Count && !built.ContainsKey(r)) toBuild.Enqueue(r);
            EnsureBuilder();
        }

        // Un minuteur fait, un petit morceau par tour de boucle : détruire les rangées
        // sorties, demander les images des cartes (celles de la rangée regardée
        // d'abord), construire les rangées qui entrent dans la zone.
        void EnsureBuilder()
        {
            if (builder != null || (pendingTexts == null && toDrop.Count == 0 && toBuild.Count == 0 && toRequest.Count == 0 && toShow.Count == 0)) return;
            builder = new Timer(1);
            builder.Tick += (s, e) =>
            {
                BuildStep();
                if (pendingTexts != null || toDrop.Count > 0 || toBuild.Count > 0 || toRequest.Count > 0 || toShow.Count > 0) return true;
                builder = null;
                return false;
            };
            builder.Start();
        }

        void BuildStep()
        {
            // D'abord les textes de la carte ouverte.
            if (pendingTexts != null)
            {
                Card t = pendingTexts;
                pendingTexts = null;
                Guard("Texts", () => ApplyCardTexts(t));
                return;
            }
            // Puis les images des cartes voisines (la carte ouverte en premier), une par tour.
            if (toShow.Count > 0)
            {
                int bi = -1, bp = int.MaxValue;
                for (int i = toShow.Count - 1; i >= 0; i--)
                {
                    if (toShow[i].Dead) { toShow.RemoveAt(i); if (bi > i) bi--; continue; }
                    int p = LandPriority(toShow[i]);
                    if (p <= bp) { bp = p; bi = i; }
                }
                if (bi >= 0)
                {
                    Card k = toShow[bi];
                    toShow.RemoveAt(bi);
                    Guard("Near", () => { ShowLand(k); ShowLogo(k); });
                    return;
                }
            }
            if (toDrop.Count > 0)
            {
                View v = toDrop.Dequeue();
                v.Unparent();
                v.Dispose();
                return;
            }
            int best = BestRequest();
            bool focusedRow = best >= 0 && PosterPriority(toRequest[best]) < 100;
            if (toBuild.Count > 0 && !focusedRow)
            {
                int r = toBuild.Dequeue();
                if (!built.ContainsKey(r) && Math.Abs(r - focusRow) <= BuildAround) Guard("BuildRow", () => BuildRow(r));
                return;
            }
            Guard("Images", () =>
            {
                for (int n = 0; n < RequestsPerTick; n++)
                {
                    int i = BestRequest();
                    if (i < 0) return;
                    Card c = toRequest[i];
                    toRequest.RemoveAt(i);
                    RequestImages(c);
                }
            });
        }

        // La carte dont les images sont les plus utiles maintenant ; les cartes
        // détruites sont retirées au passage.
        int BestRequest()
        {
            int best = -1, bestPrio = int.MaxValue;
            for (int i = toRequest.Count - 1; i >= 0; i--)
            {
                Card c = toRequest[i];
                if (c.Dead) { toRequest.RemoveAt(i); if (best > i) best--; continue; }
                int p = PosterPriority(c);
                if (p <= bestPrio) { bestPrio = p; best = i; }
            }
            return best;
        }

        // ---- Focus et carte ouverte ----

        void SetFocus(int r, int c)
        {
            long t0 = guardClock.ElapsedMilliseconds;
            focusRow = r;
            Row row = rows[r];
            bool leaving = onHero;
            if (onHero) { onHero = false; hero?.Focus(false); hero?.Run(false); }
            // Bande-annonce de la bannière arrêtée par ce changement de carte : l'image reste
            // cachée jusqu'au titre suivant (sinon celle de la carte d'avant reparaissait).
            switching = follow;
            CloseCard();
            switching = false;
            long t1 = guardClock.ElapsedMilliseconds;
            Sync();
            long t2 = guardClock.ElapsedMilliseconds;
            RowView rv = built[r];
            var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
            vertical?.Stop();
            vertical = new Animation(VerticalMs);
            vertical.AnimateTo(rowsView, "PositionY", (float)(-r * RowH + (follow ? FollowTop : 0)), ease);
            if (hero != null && !follow) vertical.AnimateTo(hero.Root, "PositionY", (float)(-Hero.Height - 24), ease);
            if (leaving && built.TryGetValue(0, out RowView first)) PlaceTitle(first, 0, vertical, ease);
            vertical.Play();
            Pump();
            long t3 = guardClock.ElapsedMilliseconds;
            if (lit != null && lit.Row != r) Unlight();
            if (rv.Cards.Count == 0) { hero?.Reveal(); return; }
            c = Math.Max(0, Math.Min(c, rv.Cards.Count - 1));
            row.Col = c;
            if (follow) Light(rv, c); else OpenCard(r, c);
            long t4 = guardClock.ElapsedMilliseconds;
            if (t4 - t0 >= 30)
                Probe.Log("keyparts", $"fermer {t1 - t0}, rangées {t2 - t1}, pump {t3 - t2}, ouvrir {t4 - t3} (textes {openTexts}, reste {t4 - t3 - openTexts})");
            horizontal?.Stop();
            horizontal = new Animation(HorizontalMs);
            horizontal.AnimateTo(rv.Track, "PositionX", (float)(Left - c * row.Slide), ease);
            // Les cartes avant la carte ouverte disparaissent en fondu pendant le glissement :
            // sinon la précédente reste visible dans la marge, sous la barre latérale. Les
            // voisines sont animées (depuis leur opacité affichée), les autres posées directement.
            for (int k = 0; k < rv.Cards.Count; k++)
            {
                float want = k < c ? 0f : 1f;
                if (Math.Abs(k - c) <= 1) horizontal.AnimateTo(rv.Cards[k].Root, "Opacity", want, ease);
                else rv.Cards[k].Root.Opacity = want;
            }
            horizontal.Play();
            // La suite du catalogue, quand on approche de la fin.
            if (row.View != null && !row.Fetching && row.Items.Count < row.Total && c >= row.Items.Count - MoreBefore) Fetch(r);
        }

        void CloseCard()
        {
            StopTrailer();
            detailsTimer?.Stop();
            fade?.Stop();
            StopPulse();
            pendingTexts = null;
            cardTitle.Hide();
            meta.Hide();
            overview.Opacity = 0;
            if (opened == null) return;
            if (built.TryGetValue(openedRow, out RowView rv))
            {
                Row row = rows[openedRow];
                opened.Ring.Hide();
                opened.TrailerRing.Hide();
                opened.LandBox.Hide();
                opened.Closed?.Show();
                opened.Frame.Position = new Position(0, FrameY(row));
                opened.Frame.Size = new Size(row.CW, row.CH);
                opened.Frame.Rounded(Radius(row));
                PlaceProgress(opened, row.CW, row.CH, Radius(row));
                for (int k = openedCol + 1; k < rv.Cards.Count; k++) rv.Cards[k].Root.PositionX = k * row.Slide;
            }
            opened = null;
            openedRow = -1;
        }

        // Bannière qui suit la carte : la carte regardée reste fermée, avec le contour de la charte,
        // grossie de 6 % en 0,2 s (l'ancienne revient à sa taille) ; la bannière la suit après 0,3 s.
        void Light(RowView rv, int c)
        {
            Card card = rv.Cards[c];
            if (card != lit)
            {
                Unlight();
                var a = new Animation(Theme.Slide);
                var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
                if (card.Ring == null)
                {
                    Row row = rows[card.Row];
                    card.Ring = Theme.FocusRing(row.CW, row.CH, Radius(row));
                    card.Ring.Position = new Position(-Theme.RingOut, FrameY(row) - Theme.RingOut);
                    card.Root.Add(card.Ring);
                    card.Ring.LowerToBottom();
                }
                card.Root.RaiseToTop();
                a.AnimateTo(card.Root, "Scale", new Vector3(Theme.Grow, Theme.Grow, 1f), ease);
                a.Play();
                grows.Add(a);
                if (grows.Count > 8) grows.RemoveAt(0);
                lit = card;
            }
            if (side) card.Ring.Hide(); else card.Ring.Show();
            hero?.Follow(card.Item, c > 0 ? rv.Cards[c - 1].Item : null, c + 1 < rv.Cards.Count ? rv.Cards[c + 1].Item : null);
            // Bande-annonce dans la bannière, comme pour une carte ouverte (1 s, 2,5 s).
            // (sinon, quand la bannière aura montré ce titre en entier : hero.Complete)
            if (!side && Nav?.Top == this && hero != null && hero.IsComplete(card.Item)) ScheduleTrailer(card);
        }

        // La carte regardée revient à sa taille, sans contour.
        void Unlight()
        {
            Card old = lit;
            lit = null;
            if (old == null || old.Dead) return;
            old.Ring?.Hide();
            var a = new Animation(Theme.Slide);
            a.AnimateTo(old.Root, "Scale", new Vector3(1f, 1f, 1f), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            a.Play();
            grows.Add(a);
            if (grows.Count > 8) grows.RemoveAt(0);
        }

        void OpenCard(int r, int c)
        {
            Row row = rows[r];
            RowView rv = built[r];
            Card card = rv.Cards[c];
            opened = card;
            openedRow = r;
            openedCol = c;
            EnsureOpenParts(card);
            card.Frame.Position = new Position(0, 0);
            card.Frame.Size = new Size(OW, OH);
            card.Frame.Rounded(Theme.RadiusLarge);
            PlaceProgress(card, OW, OH, Theme.RadiusLarge);
            card.Closed?.Hide(); // sinon l'affiche se voit sous l'image en largeur pas encore arrivée
            card.LandBox.Show();
            ShowRing(card);
            for (int k = c + 1; k < rv.Cards.Count; k++) rv.Cards[k].Root.PositionX = k * row.Slide + (OW - row.CW);

            ArtLayer.Hover(card.Item); // arrière-plan d'après l'image
            pendingTexts = card;
            openTexts = 0;
            detailsTimer = new Timer(300);
            detailsTimer.Tick += (s, e) => { Guard("DetailsStart", () => ShowDetails(card)); return false; };
            detailsTimer.Start();
            if (!side) ScheduleTrailer(card);

            // Cartes voisines : leur image en largeur et leur logo, déjà téléchargés ou en
            // cours, sont confiés à la TV pour être prêts à l'ouverture.
            for (int k = Math.Max(0, c - Near); k <= Math.Min(rv.Cards.Count - 1, c + Near); k++)
            {
                Card near = rv.Cards[k];
                if (near.Near) continue;
                near.Near = true;
                if (near.TitleInImage) titleInThumb++;
                else if (near.LogoUrl == null) logosNone++;
                toShow.Add(near);
            }
            EnsureBuilder();

            // Ce que l'on voit en ouvrant : image déjà là, ou attente (fond qui pulse).
            openedAt = Now;
            if (card.LandReady) landWait.Add(0);
            else if (!card.LandFailed) StartPulse(card.LandBack);
            ImageCache.Pump();
        }

        // Image en largeur prête (affichée par la TV), ou impossible à obtenir.
        // Titre, informations et emplacement du résumé, posés sur la carte ouverte.
        void ApplyCardTexts(Card card)
        {
            if (card != opened) return;
            long tt = guardClock.ElapsedMilliseconds;
            cardTitle.Unparent();
            card.LandBox.Add(cardTitle);
            meta.Unparent();
            meta.Text = MetaOf(card);
            card.Root.Add(meta);
            meta.Show();
            overview.Unparent();
            overview.Opacity = 0;
            overview.Text = "";
            card.Root.Add(overview);
            ShowTitle(card);
            openTexts = guardClock.ElapsedMilliseconds - tt;
            if (openTexts >= 30) Probe.Log("texts", openTexts + " ms");
        }

        void LandDone(Card card, bool ok)
        {
            if (card.LandReady || card.LandFailed) return;
            if (ok)
            {
                card.LandReady = true;
                landShow.Add(Now - card.LandAt);
            }
            else card.LandFailed = true;
            if (card != opened) return;
            if (ok) landWait.Add(Now - openedAt);
            StopPulse();
        }

        // Pulsation douce d'un emplacement gris : un chargement est en cours.
        void StartPulse(View v)
        {
            StopPulse();
            pulsing = v;
            pulse = new Animation(1200) { Looping = true };
            pulse.AnimateTo(v, "Opacity", 0.4f, new AlphaFunction(AlphaFunction.BuiltinFunctions.Sin));
            pulse.Play();
        }

        void StopPulse()
        {
            pulse?.Stop();
            pulse = null;
            if (pulsing != null) pulsing.Opacity = 1;
            pulsing = null;
        }

        void ShowTitle(Card card)
        {
            if (card != opened) return;
            bool written = !card.TitleInImage && (card.LogoUrl == null || card.LogoFailed);
            if (written)
            {
                cardTitle.Text = Library.Title(card.Item);
                cardTitle.Show();
            }
            else cardTitle.Hide();
            cardTitle.RaiseToTop();
        }

        // Après 300 ms sur la même carte : genres et résumé, demandés au serveur s'il le faut.
        async void ShowDetails(Card card)
        {
            Item it = card.Item;
            // Toujours demandée : les liens des bandes-annonces viennent avec elle.
            Task<Item> asked = DetailsOf(it);
            if (it.Overview == null || it.Genres == null)
            {
                Item d = null;
                try { d = await asked; }
                catch (Exception) { }
                if (d != null)
                {
                    if (it.Overview == null) it.Overview = d.Overview;
                    if (it.Genres == null) it.Genres = d.Genres;
                }
                if (Closed || opened != card) return;
                Guard("Details", () => meta.Text = MetaOf(card));
            }
            Guard("Details", () =>
            {
                SetOverview(it.Overview ?? "");
                fade = new Animation(300);
                fade.AnimateTo(overview, "Opacity", 1f);
                fade.Play();
            });
        }

        // Fiche d'un titre, une demande par titre (refaite après un échec).
        Task<Item> DetailsOf(Item it) => DetailsOf(it.Id);

        Task<Item> DetailsOf(string id)
        {
            if (id == null) return Task.FromResult<Item>(null);
            if (!detailTasks.TryGetValue(id, out Task<Item> t) || t.IsFaulted || t.IsCanceled)
            {
                t = Library.Details(id);
                detailTasks[id] = t;
            }
            return t;
        }

        // ---- Bande-annonce de la carte ouverte ----

        // Retour sur l'accueil, barre latérale quittée : la bande-annonce de la carte ouverte (ou
        // suivie) repart ; quand la bannière suit la carte, seulement si elle montre déjà ce titre
        // en entier (sinon hero.Complete s'en charge).
        void ResumeTrailer()
        {
            Card f = Focused;
            if (f == null) return;
            if (follow && (hero == null || !hero.IsComplete(f.Item))) return;
            ScheduleTrailer(f);
        }

        void ScheduleTrailer(Card card)
        {
            StopTrailer();
            trailerLoad = new Timer(TrailerLoadMs);
            trailerLoad.Tick += (s, e) => { Guard("TrailerLoad", () => LoadTrailer(card)); return false; };
            trailerLoad.Start();
            trailerPlay = new Timer(TrailerPlayMs);
            trailerPlay.Tick += (s, e) =>
            {
                Guard("TrailerPlay", () => { if (card == Focused && !side) trailer.Play(); });
                return false;
            };
            trailerPlay.Start();
        }

        async void LoadTrailer(Card card)
        {
            if (card != Focused) return;
            Item d = null;
            // Un épisode (« Reprendre ») n'a pas de bande-annonce sur le serveur : celles de sa
            // série (vu le 2026-11-11 sur Black Doves ; la version web, elle, n'en montre pas).
            bool episode = card.Item.Type == "Episode" && card.Item.SeriesId != null;
            try { d = await (episode ? DetailsOf(card.Item.SeriesId) : DetailsOf(card.Item)); }
            catch (Exception) { }
            if (Closed || card != Focused || side) return;
            var urls = new List<string>();
            if (d?.RemoteTrailers != null)
                foreach (MediaUrl m in d.RemoteTrailers)
                    if (!string.IsNullOrEmpty(m?.Url)) urls.Add(m.Url);
            if (urls.Count == 0) { trailerNone++; return; }
            Guard("TrailerLoad", () =>
            {
                trailerCard = card;
                Rectangle r = follow && hero != null ? hero.VideoRect : new Rectangle(Left, TrackY, OW, OH);
                trailer.Load(urls, r.X, r.Y, r.Width, r.Height);
            });
        }

        // La vidéo avance : l'image de la carte s'efface en fondu, la vidéo se voit dessous.
        void TrailerShown()
        {
            Card card = trailerCard;
            if (card == null || card != Focused || card.Dead) { StopTrailer(); return; }
            if (follow && hero != null)
            {
                // Bannière qui suit la carte : l'image s'efface, la vidéo se voit sous les voiles.
                // Le voile de la bannière reprend l'arrière-plan (d'après l'image ou uni) sur tout
                // l'écran : l'arrière-plan lui-même est retiré, sans changement visible. Pas avant
                // que le voile soit décodé (vu par l'auteur : bords fondus arrivés après la vidéo).
                hero.WhenVideoReady(() =>
                {
                    if (trailerCard != card || card != lit || card.Dead) return;
                    Window.Instance.BackgroundColor = Color.Transparent;
                    hero.Video(true);
                    if (ArtLayer.Visible) ArtLayer.Hole(new Rectangle(0, 0, 1920, 1080));
                    trailerVisible = true;
                });
                return;
            }
            Window.Instance.BackgroundColor = Color.Transparent;
            EnsureOpenParts(card);
            // Arrière-plan d'après l'image : il fait lui-même les quatre bandes autour de la carte.
            if (ArtLayer.Visible) ArtLayer.Hole(new Rectangle(Left, TrackY, OW, OH));
            else PlaceHole(new Rectangle(Left, TrackY, OW, OH));
            trailerVisible = true;
            ShowRing(card);
            StopPulse();
            card.Frame.BackgroundColor = Color.Transparent;
            card.LandBack.Hide();
            trailerFade?.Stop();
            if (card.Land == null) return;
            trailerFade = new Animation(TrailerFadeMs);
            trailerFade.AnimateTo(card.Land, "Opacity", 0f);
            trailerFade.Play();
        }

        // Fin de la bande-annonce : l'image revient en fondu, puis le fond redevient opaque.
        void TrailerGone(bool ended)
        {
            Card card = trailerCard;
            if (card == null) return;
            if (follow && hero != null)
            {
                if (card != lit || card.Dead) { RestoreTrailer(); return; }
                hero.Video(false, () => { if (trailerCard == card) RestoreTrailer(); });
                return;
            }
            if (card != opened || card.Dead || card.Land == null) { RestoreTrailer(); return; }
            trailerFade?.Stop();
            var anim = new Animation(TrailerFadeMs);
            anim.AnimateTo(card.Land, "Opacity", 1f);
            anim.Finished += (s, e) => Guard("TrailerGone", () => { if (trailerFade == anim) RestoreTrailer(); });
            trailerFade = anim;
            anim.Play();
        }

        // Arrêt immédiat, à chaque changement de carte, d'écran, ou en arrière-plan.
        void StopTrailer()
        {
            trailerLoad?.Stop();
            trailerPlay?.Stop();
            trailerLoad = trailerPlay = null;
            trailer?.Stop();
            RestoreTrailer();
        }

        // Fond opaque autour du rectangle de la vidéo (sans arrière-plan d'après l'image).
        void PlaceHole(Rectangle r)
        {
            var bands = new[]
            {
                new Rectangle(0, 0, 1920, r.Y),
                new Rectangle(0, r.Y + r.Height, 1920, 1080 - r.Y - r.Height),
                new Rectangle(0, r.Y, r.X, r.Height),
                new Rectangle(r.X + r.Width, r.Y, 1920 - r.X - r.Width, r.Height),
            };
            for (int i = 0; i < hole.Length; i++)
            {
                Rectangle b = bands[i];
                if (b.Width <= 0 || b.Height <= 0) { hole[i].Hide(); continue; }
                hole[i].Position = new Position(b.X, b.Y);
                hole[i].Size = new Size(b.Width, b.Height);
                hole[i].BackgroundColor = App.Background;
                hole[i].Show();
            }
        }

        void RestoreTrailer()
        {
            trailerFade?.Stop();
            trailerFade = null;
            Card card = trailerCard;
            trailerCard = null;
            if (card == null) return;
            trailerVisible = false;
            if (follow) hero?.Video(false, now: true, keepHidden: switching);
            else
            {
                if (card.Land != null) card.Land.Opacity = 1;
                card.Frame.BackgroundColor = Theme.Placeholder;
                ShowRing(card);
                card.LandBack?.Show();
            }
            foreach (View h in hole) h.Hide();
            ArtLayer.Hole(null);
            Window.Instance.BackgroundColor = App.Background;
        }

        // Le résumé tient en OverviewLines lignes ; sinon il est coupé à la fin d'un mot
        // et se termine par « … ». Le nombre de lignes est lu sur le texte mis en page
        // (LineCount) : quelques essais au plus, une fois que le focus s'est posé.
        void SetOverview(string text)
        {
            overview.Text = text;
            int lines = overview.LineCount;
            if (lines <= OverviewLines) return;
            int len = (int)(text.Length * (double)OverviewLines / lines);
            for (int tries = 0; tries < 6 && len > 1; tries++)
            {
                overview.Text = Cut(text, len);
                if (overview.LineCount <= OverviewLines) return;
                len = (int)(len * 0.92);
            }
        }

        static string Cut(string text, int len)
        {
            len = Math.Min(len, text.Length);
            int space = text.LastIndexOf(' ', len - 1);
            if (space < len / 2) space = len;
            return text.Substring(0, space).TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
        }

        // ---- Touches ----

        public override bool OnKey(string key)
        {
            bool done = true;
            Guard("Key " + key, () =>
            {
                if (cardMenu.Key(key)) return;
                done = Keys(key);
            });
            return done;
        }

        public override bool OnBack() => cardMenu.Back();

        public override bool OnKeyUp(string key) => cardMenu.KeyUp(key);

        // ---- Cartes : appui court, appui long (menu de la carte, Ui/CardMenu.cs) ----
        // Comme la version web : un appui court sur une carte « Reprendre » reprend la lecture
        // (dernière version lue si elle est retenue, sinon la liste des versions), sur « À suivre »
        // voir NextUp, ailleurs la page du titre ; un appui long ouvre le menu de la carte, sur
        // toutes les rangées. OK agit donc au relâchement.

        CardMenu cardMenu;

        bool IsResume(int r) => rows[r].View == null && !rows[r].Upcoming && !rows[r].NextUp;

        static long ResumeTicks(Item it) => CardMenu.ResumeTicks(it);

        // Reprise : la dernière version lue est relancée par le panneau des versions (LastVersions).
        void Resume(Item it) => Nav.Push(new VersionsScreen(it, ResumeTicks(it), null));

        // « À suivre » (demandes de l'auteur, 2026-11-21, comme la rangée du web) : lecture directe
        // si « Lancer automatiquement la première version » est actif (ou si une version est
        // retenue pour une reprise) ; sinon page du titre, liste des versions ouverte par-dessus
        // (versionsPath du web). Retour ramène alors à la page.
        void NextUp(Item it)
        {
            long ticks = ResumeTicks(it);
            if (Prefs.AutoPlayFirst || (ticks > 0 && LastVersions.Get(it.Id) != null)) { Resume(it); return; }
            Nav.Push(ItemScreen.For(it));
            Nav.Push(new VersionsScreen(it, ResumeTicks(it), null, straight: false));
        }

        // OK enfoncé sur la carte it de la rangée r : action de l'appui court, place de la carte.
        void Press(int r, Item it)
        {
            Action shortPress;
            if (IsResume(r)) shortPress = () => Resume(it);
            else if (rows[r].NextUp) shortPress = () => NextUp(it);
            else shortPress = () => Nav.Push(ItemScreen.For(it));
            cardMenu.Press(it, shortPress, () => CardPlace(r));
        }

        // Carte regardée : toujours au bord gauche de sa rangée ; ouverte (OW × OH en haut des
        // rangées) ou grossie autour de son centre (« Suit la carte sélectionnée »).
        (float X, float Y, float W, float H) CardPlace(int r)
        {
            if (opened != null && openedRow == r) return (Left, TrackY, OW, OH);
            Row row = rows[r];
            float g = follow ? Theme.Grow : 1f;
            // Rangée regardée : posée à FollowTop sous la bannière qui suit la carte (SetFocus).
            return (Left - row.CW * (g - 1) / 2, (follow ? FollowTop : 0) + TrackTop(row) - row.CH * (g - 1) / 2,
                row.CW * g, row.CH * g);
        }

        // Après un envoi du menu : « Reprendre » et les autres rangées redemandées.
        void MenuDone(Item it)
        {
            if (!Closed && Nav?.Top == this) Refresh();
        }

        bool Keys(string key)
        {
            if (rows.Count == 0) return false;
            int r = focusRow, c = rows[r].Col;
            int count = built.TryGetValue(r, out RowView rv) ? rv.Cards.Count : 0;
            if (onHero && hero != null)
            {
                switch (key)
                {
                    case "Left": if (!hero.Left()) App.Current.ToSidebar(); return true;
                    case "Right": if (!hero.Right()) Ignored(); return true;
                    case "Up": Ignored(); return true;
                    case "Down": SetFocus(0, rows[0].Col); return true;
                    case "Return":
                    case "KP_Enter":
                        if (hero.Count > 0) hero.Activate(); else Ignored();
                        return true;
                }
                return false;
            }
            switch (key)
            {
                case "Left": if (c > 0) SetFocus(r, c - 1); else App.Current.ToSidebar(); return true;
                case "Right": if (c < count - 1) SetFocus(r, c + 1); else Ignored(); return true;
                case "Up":
                    if (r > 0) SetFocus(r - 1, rows[r - 1].Col);
                    else if (hero != null && hero.Count > 0 && !follow) FocusHero();
                    else Ignored();
                    return true;
                case "Down": if (r < rows.Count - 1) SetFocus(r + 1, rows[r + 1].Col); else Ignored(); return true;
                case "Return":
                case "KP_Enter":
                    // Catalogue en panne : OK le redemande. Sinon, appui court ou long sur la carte (Press).
                    if (rows[r].Failed && !rows[r].Fetching) Fetch(r);
                    else if (opened != null && openedRow == r) Press(r, opened.Item);
                    else if (lit != null && lit.Row == r) Press(r, lit.Item);
                    else Ignored();
                    return true;
            }
            return false;
        }

        static void Ignored() => App.Current.KeyIgnored();

        // ---- Rechargement ----

        void Reload()
        {
            gen++;
            refreshing = false;
            CloseCard();
            builder?.Stop();
            builder = null;
            toBuild.Clear();
            toRequest.Clear();
            toShow.Clear();
            while (toDrop.Count > 0) { View d = toDrop.Dequeue(); d.Unparent(); d.Dispose(); }
            foreach (RowView rv in built.Values)
            {
                rv.SkeletonPulse?.Stop();
                foreach (Card c in rv.Cards) c.Dead = true;
                rv.Root.Unparent();
                rv.Root.Dispose();
            }
            built.Clear();
            rows.Clear();
            inflight = 0;
            focusRow = 0;
            vertical?.Stop();
            hero?.Close();
            hero = null;
            onHero = false;
            lit = null;
            foreach (View left in new List<View>(heroSlot.Children)) { left.Unparent(); left.Dispose(); } // bannière vide arrêtée
            rowsView.PositionY = 0;
            tViews = tContinue = tFirst = tUpcoming = "-";
            Load();
        }

        // Retour sur l'accueil : « Reprendre », « À suivre » et « À venir » redemandés. Une
        // rangée qui a changé est refaite à sa place, le focus restant sur le même titre s'il
        // y est encore. Si l'une de ces rangées apparaît ou disparaît, ou si le réglage
        // « Réunir Reprendre et À suivre » a changé, tout l'accueil est rechargé.
        async void Refresh()
        {
            if (refreshing) return;
            if (Prefs.MergeNextUp != merged || Prefs.Hero != heroSeen) { Guard("Refresh", Reload); return; }
            refreshing = true;
            int g = gen;
            List<Item> c, n, up;
            Task<List<Item>> next = null;
            try
            {
                Task<(List<Item> Continue, List<Item> NextUp)> cont = Library.HomeRows(merged);
                next = Library.Upcoming();
                (c, n) = await cont;
            }
            catch (Exception) { if (g == gen) refreshing = false; return; }
            try { up = await next; } catch (Exception) { up = null; } // en panne : on garde l'affiché
            if (Closed || g != gen) return;
            refreshing = false;
            Guard("Refresh", () =>
            {
                int ci = rows.FindIndex(x => x.View == null && !x.Upcoming && !x.NextUp);
                int ni = rows.FindIndex(x => x.NextUp);
                int ui = rows.FindIndex(x => x.Upcoming);
                bool hasC = c != null && c.Count > 0;
                bool hasN = n != null && n.Count > 0;
                bool hasU = up != null && up.Count > 0;
                if ((ci >= 0) != hasC || (ni >= 0) != hasN || (up != null && (ui >= 0) != hasU)) { Reload(); return; }
                bool changed = false;
                if (ci >= 0 && !Same(rows[ci].Items, c)) { Replace(ci, c); changed = true; }
                if (ni >= 0 && !Same(rows[ni].Items, n)) { Replace(ni, n); changed = true; }
                if (ui >= 0 && up != null && !Same(rows[ui].Items, up)) { Replace(ui, up); changed = true; }
                Probe.Log("home", changed ? "retour : rangées refaites" : "retour : rien de changé");
                if (!changed) return;
                if (onHero || rows[focusRow].Items.Count == 0) { Sync(); return; }
                SetFocus(focusRow, rows[focusRow].Col);
            });
        }

        // Mêmes titres, dans le même ordre, avec le même avancement.
        static bool Same(List<Item> a, List<Item> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Id != b[i].Id || a[i].PremiereDate != b[i].PremiereDate) return false;
                if ((a[i].UserData?.Played ?? false) != (b[i].UserData?.Played ?? false)) return false;
                if ((a[i].UserData?.PlaybackPositionTicks ?? 0) != (b[i].UserData?.PlaybackPositionTicks ?? 0)) return false;
            }
            return true;
        }

        // Remplace le contenu d'une rangée ; sa vue est détruite, puis refaite par Sync.
        void Replace(int index, List<Item> items)
        {
            Row row = rows[index];
            string keep = row.Col < row.Items.Count ? row.Items[row.Col].Id : null;
            if (index == openedRow) CloseCard();
            if (lit != null && lit.Row == index) lit = null;
            if (built.TryGetValue(index, out RowView rv))
            {
                rv.SkeletonPulse?.Stop();
                foreach (Card k in rv.Cards) k.Dead = true;
                rv.Root.Hide();
                toDrop.Enqueue(rv.Root);
                built.Remove(index);
            }
            row.Items.Clear();
            row.Items.AddRange(items);
            row.Total = items.Count;
            int col = keep == null ? -1 : items.FindIndex(x => x.Id == keep);
            row.Col = Math.Max(0, Math.Min(col < 0 ? row.Col : col, items.Count - 1));
        }

        void SetStatus(Func<string> make)
        {
            statusMake = make;
            status.Text = make?.Invoke() ?? "";
        }

        // ---- Erreurs ----

        // Une erreur dans l'accueil ferme toute l'application, et la TV ne permet pas
        // de lire ses journaux : elle est affichée à l'écran à la place (photo à envoyer).
        // Mesure aussi le temps passé sur le fil d'interface : au-delà de 30 ms, le
        // traitement est noté (journal de la sonde, et les plus lents dans le panneau).
        void Guard(string where, Action run)
        {
            long t0 = guardClock.ElapsedMilliseconds;
            try { run(); }
            catch (Exception e) { ShowError(where, e); }
            long ms = guardClock.ElapsedMilliseconds - t0;
            if (ms < 30) return;
            Probe.Log("slow", where + " " + ms + " ms");
            string name = where.StartsWith("Key ") ? "Key" : where;
            if (!slowest.TryGetValue(name, out long prev) || ms > prev) slowest[name] = ms;
            slowCount++;
        }

        string Slowest()
        {
            if (slowest.Count == 0) return Lang.T("home.none");
            var l = new List<KeyValuePair<string, long>>(slowest);
            l.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < l.Count && i < 5; i++) parts.Add(l[i].Key + " " + l[i].Value + " ms");
            return string.Join(", ", parts);
        }

        void ShowError(string where, Exception e)
        {
            if (Closed) return;
            var lines = new List<string> { $"{where} : {e.GetType().Name} : {e.Message}" };
            for (Exception inner = e.InnerException; inner != null; inner = inner.InnerException)
                lines.Add($"→ {inner.GetType().Name} : {inner.Message}");
            string[] stack = (e.StackTrace ?? "").Split('\n');
            for (int i = 0; i < stack.Length && i < 6; i++) lines.Add(stack[i].Trim());
            Probe.Log("error", string.Join(" | ", lines));
            if (error == null)
            {
                error = new TextLabel
                {
                    Position = new Position(40, 200),
                    Size = new Size(1840, 600),
                    PointSize = 14,
                    MultiLine = true,
                    TextColor = Theme.Error,
                    BackgroundColor = new Color(0f, 0f, 0f, 0.85f),
                };
                root.Add(error);
            }
            error.Text = Lang.T("home.error", string.Join("\n", lines));
            error.Show();
            error.RaiseToTop();
            // Gardée aussi dans le panneau de mesures ; le cadre jaune s'efface après 10 s.
            lastError = lines[0] + (stack.Length > 0 ? "\n" + stack[0].Trim() : "");
            errorTimer?.Stop();
            errorTimer = new Timer(10000);
            errorTimer.Tick += (s, e2) => { error?.Hide(); return false; };
            errorTimer.Start();
        }

        // ---- Mesures ----
        // Où passe le temps des images : téléchargement (mesuré à part, par l'application)
        // ou préparation et affichage par la TV (de la demande à ResourceReady) ?

        long Now => clock.ElapsedMilliseconds;

        // La mesure relit ce texte chaque seconde.
        void Note() => Measure.Note = NoteText;

        string NoteText()
        {
            if (Closed) return null;
            int waited = 0;
            foreach (long w in landWait) if (w > 0) waited++;
            return Lang.T("home.panel", tViews, tContinue, tFirst,
                ImageCache.Downloads.Count, Med(ImageCache.Downloads), Max(ImageCache.Downloads), ImageCache.Hits,
                ImageCache.Queued, ImageCache.Running, ImageCache.Failures,
                Med(landShow), Max(landShow), Med(posterShow), Max(posterShow), Pending(),
                unreadable, unreadableKinds, Med(landWait), Max(landWait), landWait.Count, waited,
                lastError ?? Lang.T("home.none"),
                logosShown, logosNone, logoFails, ImageCache.WebpReceived, ImageCache.WebpConverted,
                ImageCache.FileCount, ImageCache.TotalBytes / (1024 * 1024), ImageCache.Trimmed, ImageCache.Missing,
                titleInThumb, slowCount, Slowest(), GC.CollectionCount(2) - gcAtStart,
                webpTried, webpShown, webpRefused, Webp.SystemLibrary)
                + "\n" + Lang.T("home.panel_upcoming", nUpcoming, tUpcoming)
                + "\n\n" + Lang.T("home.trailer", trailerNone, trailer.Loaded, trailer.Started, trailer.ShownCount,
                    trailer.LastResolve, trailer.LastPrepare, trailer.LastFirst, trailer.LastCrop,
                    Trailers.SegOk, Trailers.SegHeld, Trailers.SegFailed, Trailers.SegLast,
                    trailer.Failures, trailer.LastError ?? Lang.T("home.none"));
        }

        // Images demandées à la TV dans les rangées construites, pas encore affichées.
        // Images confiées à la TV (fichiers déjà téléchargés), pas encore prêtes.
        int Pending()
        {
            int n = 0;
            foreach (RowView rv in built.Values)
                foreach (Card c in rv.Cards)
                {
                    if (c.Closed != null && !c.ClosedReady) n++;
                    if (c.Land != null && !c.LandReady && !c.LandFailed) n++;
                }
            return n;
        }

        static string Max(List<long> l)
        {
            if (l.Count == 0) return "-";
            long m = 0;
            foreach (long x in l) if (x > m) m = x;
            return m.ToString();
        }

        static string Med(List<long> l)
        {
            if (l.Count == 0) return "-";
            var s = new List<long>(l);
            s.Sort();
            return s[s.Count / 2].ToString();
        }
    }
}
