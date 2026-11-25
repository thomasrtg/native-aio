// Découvrir : un catalogue entier en grille d'affiches, page après page, avec les filtres
// de la version web (pages/discover.tsx). Disposition choisie par l'auteur après essai :
// - à gauche, une colonne fixe : les contrôles empilés comme dans la version web (Films |
//   Séries | Collections, le catalogue, le genre seulement si le catalogue en propose, Tout |
//   Non vus), puis la carte du titre regardé comme sur l'accueil (image en largeur, logo,
//   informations, résumé), changée 0,3 s après l'arrêt du focus ;
// - à droite, la grille : 5 affiches par ligne avec le titre et l'année, qui défile seule.
// Mêmes règles de choix que le web : un catalogue mixte s'ouvre sur les films ; changer de
// type ouvre le catalogue de même nom dans l'autre type, sinon ce catalogue filtré s'il est
// mixte, sinon le dernier parcouru de ce type ; le genre suit d'un catalogue à l'autre par
// son nom ; un catalogue qui exige un genre s'ouvre sur « None » s'il existe, sinon sur le
// premier. Dernier catalogue parcouru gardé sur la TV, par utilisateur et par type.
// Fluidité : cartes, textes et images faits au tour de boucle suivant, près du focus ; les
// cartes loin du focus sont détruites (un catalogue peut compter des milliers de titres).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Tizen.Applications;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Screens
{
    class DiscoverScreen : Screen
    {
        // Grille : Cols affiches de TileW×ImageH, titre et année dessous. Taille moyenne :
        // 5 affiches de 192×288 tous les 216 px ; les autres tailles (réglage « Taille des
        // affiches », module 13c) gardent la largeur et changent le nombre par ligne.
        const int X0 = 96, LeftW = 560, GX = X0 + LeftW + 56, GridY = 60;
        // Sous une affiche (charte, comme Favoris) : titre 18 pt, année 15 pt grise dessous.
        const int MediumCols = 5, MediumStep = 216, TitleLineH = 34, CaptionH = 28;
        readonly int Cols = Prefs.GridCols(MediumCols);
        readonly int Step, TileW, ImageH, TextH, RowStep;
        readonly bool showTitle = Prefs.PosterTitle, showYear = Prefs.PosterYear;

        public DiscoverScreen()
        {
            Step = MediumCols * MediumStep / Cols;
            TileW = Step - (MediumStep - 192);
            ImageH = TileW * 3 / 2;
            TextH = (showTitle ? TitleLineH : 0) + (showYear ? CaptionH : 0);
            RowStep = ImageH + (TextH > 0 ? 12 + TextH : 0) + 34;
        }
        // Contrôles, de haut en bas ; la carte du titre a une place fixe dessous (logo à
        // l'échelle de la carte ouverte de l'accueil : 360×130 sur 883 px de large).
        const int TopY = 60, FilterH = 56, RowGap = 20, Gap = 12, ScrollMargin = 40, ScrollMs = 300;
        // Pastilles comme la version web : hauteur 44 dans un fond de 56, largeur selon le texte.
        const int PillH = 44, PillPad = 6, PillMinW = 110;
        // Liste déroulante sous un champ ; affiche regardée grossie (animation de la TV).
        // Couleurs, tailles, arrondis, contours et durées : ceux de la charte (Ui/Theme.cs, module 14).
        const int SlideMs = Theme.Slide;
        const int FicheY = 410, FicheH = LeftW * 9 / 16, LogoW = 228, LogoH = 82, FeatureDelayMs = 300;
        const int PageSize = 60, BuildRows = 2, KeepRows = 6;

        static readonly string[] Kinds = { "Movie", "Series", "BoxSet" };

        enum FKind { Type, Catalog, Genre, Filter }

        class Filter
        {
            public FKind Kind;
            public string Value; // type (Movie…) ; « unplayed » ou null pour Tout
            public int Row;      // ligne de contrôles, de haut en bas
            public View Box, Ring; // Ring : contour du focus, séparé par un liseré sombre
            public TextLabel Label;
        }

        class Tile
        {
            public int Index;
            public Item Item;
            public View Root, Ring, Frame;
            public string Url;
            public bool Built, Texted, Requested, Dead;
        }

        View root, pane, left;
        TextLabel count, message;

        // Liste déroulante ouverte sous un champ (boîte de choix commune).
        readonly Picker picker = new Picker();

        // Affiche grossie, et animations gardées (sinon elles s'arrêtent).
        Tile grown;
        readonly List<Animation> grows = new List<Animation>();

        // Pastille blanche de chaque ligne de pastilles, contour de focus d'une ligne, choix
        // affiché, lignes dont la pastille glisse encore (textes recolorés à mi-chemin).
        readonly Dictionary<FKind, View> thumbs = new Dictionary<FKind, View>();
        readonly Dictionary<int, View> groupRings = new Dictionary<int, View>();
        readonly Dictionary<int, View> groupBacks = new Dictionary<int, View>(); // fond commun de chaque ligne de pastilles
        readonly Dictionary<FKind, string> shownChosen = new Dictionary<FKind, string>();
        readonly HashSet<FKind> sliding = new HashSet<FKind>();
        bool genreRow; // champ Genre affiché à la dernière mise en page
        Timer colorTimer;

        // Carte du titre regardé (comme la fiche de la recherche).
        View fiche, ficheFrame;
        TextLabel ficheTitle, ficheMeta, ficheText;
        ImageView ficheLogo;
        int featureGen, landShown;
        string pendingId = "";
        Timer featureTimer;
        readonly Dictionary<string, Item> details = new Dictionary<string, Item>();
        readonly List<Animation> backFades = new List<Animation>();
        readonly List<View> lands = new List<View>();
        Guarded guard;
        readonly List<Filter> filters = new List<Filter>();
        readonly List<View> groups = new List<View>();

        // État des filtres.
        List<Item> views;
        Item view;
        string kind;            // Movie, Series ou BoxSet
        string genreName;       // genre choisi, null : celui par défaut
        bool unplayed;
        List<Item> genres;      // genres du catalogue, null pendant la demande
        Item genreItem;         // genre envoyé au serveur
        bool withAll;
        readonly Dictionary<string, List<Item>> genreCache = new Dictionary<string, List<Item>>();

        // Grille.
        readonly List<Tile> tiles = new List<Tile>();
        readonly HashSet<Tile> live = new HashSet<Tile>(); // cartes construites
        int total, gen, cur;
        bool loading, failed, inFilters = true;
        int filterFocus;
        int scroll, paneHeight;
        Animation scrollAnim;
        Timer builder;
        readonly Queue<View> toDrop = new Queue<View>();
        readonly List<Animation> fades = new List<Animation>(); // gardées, sinon elles s'arrêtent

        // Mesures (panneau, touche rouge ; journal de la sonde, catégorie discover).
        readonly Stopwatch clock = Stopwatch.StartNew();
        string viewsTime = "-", genresTime = "-", pageTime = "-";
        int pages, built, dropped, imagesAsked, imagesShown;

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.ScreenBack(Theme.Background) };
            guard = new Guarded(root);
            pane = new View { Size = new Size(1920, 1080) };
            root.Add(pane);
            left = new View { Size = new Size(GX - 24, 1080) };
            root.Add(left);
            count = new TextLabel { Position = new Position(X0, TopY), Size = new Size(LeftW, 32), PointSize = Theme.SizeInfo, TextColor = Theme.Grey };
            left.Add(count);
            message = new TextLabel { Position = new Position(GX, GridY + 10), Size = new Size(1920 - GX - 60, 100), PointSize = Theme.SizeBody, MultiLine = true, TextColor = Theme.Grey };
            pane.Add(message);
            fiche = new View { Position = new Position(X0, FicheY), Size = new Size(LeftW, 1080 - FicheY) };
            ficheFrame = new View { Size = new Size(LeftW, FicheH), BackgroundColor = Theme.Placeholder }.Rounded(Theme.RadiusLarge);
#pragma warning disable CS0618 // ShadowOffset/ShadowColor : dépréciés en API 8, toujours présents (comme l'accueil)
            ficheTitle = new TextLabel
            {
                Position = new Position(18, FicheH - 70),
                Size = new Size(LeftW - 36, 56),
                PointSize = Theme.SizeTitle,
                Ellipsis = true,
                TextColor = Color.White,
                ShadowOffset = new Vector2(2, 2),
                ShadowColor = new Color(0f, 0f, 0f, 0.9f),
            };
#pragma warning restore CS0618
            ficheMeta = new TextLabel { Position = new Position(0, FicheH + 14), Size = new Size(LeftW, 30), PointSize = Theme.SizeInfo, TextColor = Theme.Grey };
            ficheText = new TextLabel { Position = new Position(0, FicheH + 50), Size = new Size(LeftW, 5 * 31), PointSize = Theme.SizeBody, MultiLine = true, TextColor = Theme.Text };
            ficheFrame.Add(ficheTitle);
            fiche.Add(ficheFrame);
            fiche.Add(ficheMeta);
            fiche.Add(ficheText);
            fiche.Hide();
            left.Add(fiche);
            left.Add(picker.View);
            cardMenu = new CardMenu(this, Guard) { Changed = MenuDone };
            root.Add(cardMenu.View); // menu d'une carte, au-dessus de tout
            LoadViews();
            return root;
        }

        public override void ApplyTexts()
        {
            MakeFilters();
            ShowState();
        }

        public override void OnShow() => Measure.Note = NoteText;
        public override void OnHide() { Measure.Note = null; cardMenu?.Hide(); }
        public override bool OnKeyUp(string key) => cardMenu.KeyUp(key);

        // Menu d'une carte (Ui/CardMenu.cs) ; après un envoi, coche « vu » de la carte remise à jour.
        CardMenu cardMenu;

        void MenuDone(Item it)
        {
            foreach (Tile t in tiles)
                if (t.Item == it && t.Frame != null) CardMenu.SetCheck(t.Frame, it.UserData?.Played == true, () => Check(TileW - 52, 12));
        }

        public override void OnClose()
        {
            builder?.Stop();
            featureTimer?.Stop();
            colorTimer?.Stop();
            guard.Stop();
            Measure.Note = null;
        }

        void Guard(string where, Action run) => guard.Run(where, run);


        static string UserKey => "discover.last." + Session.UserId;

        static string ReadPref(string key)
        {
            try { return Preference.Contains(key) ? Preference.Get<string>(key) : null; }
            catch (Exception) { return null; }
        }

        // Hors du fil d'interface : l'écriture a pris 75 ms sur la TV (2026-11-11), peut-être
        // aussi le blocage de 1,4 s vu au premier changement de catalogue (file commune, Store).
        static void WritePref(string key, string value) => Store.Set(key, value ?? "");

        // ---- Catalogues ----

        async void LoadViews()
        {
            loading = true;
            ShowState();
            long t0 = clock.ElapsedMilliseconds;
            List<Item> list;
            try { list = await Library.Views(true); }
            catch (Exception e)
            {
                Probe.Log("discover", "catalogues : échec " + e.GetType().Name);
                if (Closed) return;
                Guard("Views", () => { loading = false; failed = true; viewsTime = Lang.T("search.panel_failed"); ShowState(); });
                return;
            }
            if (Closed) return;
            Guard("Views", () =>
            {
                views = list;
                viewsTime = ((clock.ElapsedMilliseconds - t0) / 1000.0).ToString("0.0") + " s";
                Probe.Log("discover", views.Count + " catalogues en " + viewsTime);
                loading = false;
                failed = false;
                if (views.Count == 0) { MakeFilters(); ShowState(); return; }
                // Le dernier catalogue parcouru sur cette TV, sinon le premier.
                string last = ReadPref(UserKey);
                Item target = views.Find(v => v.Id == last) ?? views[0];
                Open(target, null);
            });
        }

        // Ouvre un catalogue dans un type (null : le sien, films pour un catalogue mixte).
        void Open(Item v, string k)
        {
            // Durée de chaque étape dans le journal : un changement de catalogue a bloqué
            // 1,4 s une fois sur la TV (2026-11-11), 40 ms la fois suivante, cause inconnue.
            long t0 = clock.ElapsedMilliseconds;
            int gc0 = GC.CollectionCount(2);
            view = v;
            kind = k ?? Library.KindOf(v) ?? "Movie";
            WritePref(UserKey, v.Id);
            WritePref(UserKey + "." + kind, v.Id);
            long t1 = clock.ElapsedMilliseconds;
            genres = null;
            genreItem = null;
            int was = tiles.Count, wasLive = live.Count;
            Clear();
            long t2 = clock.ElapsedMilliseconds;
            // Genres déjà connus : les contrôles ne sont faits qu'une fois (SetGenres). Faits
            // deux fois dans le même tour, la seconde partait d'une pastille blanche pas encore
            // affichée (position 0) : elle arrivait du bord gauche (« glitch » vu par l'auteur).
            bool cached = genreCache.TryGetValue(v.Id, out List<Item> known);
            if (!cached) MakeFilters();
            long t3 = clock.ElapsedMilliseconds;
            Probe.Log("discover", "ouverture de « " + v.Name + " » : préférences " + (t1 - t0) + " ms, vidage (" + was + " cartes, "
                + wasLive + " construites) " + (t2 - t1) + " ms, contrôles " + (t3 - t2) + " ms, ramasse-miettes complets " + (GC.CollectionCount(2) - gc0));
            if (cached) { SetGenres(known); return; }
            LoadGenres(v, gen);
        }

        async void LoadGenres(Item v, int g)
        {
            long t0 = clock.ElapsedMilliseconds;
            List<Item> list;
            try
            {
                ItemsResult r = await Library.Genres(v.Id);
                list = new List<Item>(r?.Items ?? new Item[0]);
            }
            catch (Exception e)
            {
                // Sans genres, le catalogue s'affiche quand même (sauf s'il en exige un).
                Probe.Log("discover", "genres de « " + v.Name + " » : échec " + e.GetType().Name);
                list = new List<Item>();
            }
            if (Closed || g != gen) return;
            Guard("Genres", () =>
            {
                genresTime = list.Count + " en " + ((clock.ElapsedMilliseconds - t0) / 1000.0).ToString("0.0") + " s";
                Probe.Log("discover", "genres de « " + v.Name + " » : " + genresTime);
                genreCache[v.Id] = list;
                SetGenres(list);
            });
        }

        // Genre envoyé au serveur (defaultGenre du web) : celui choisi s'il existe dans ce
        // catalogue, sinon « None » s'il existe, sinon le premier si le catalogue en exige un.
        void SetGenres(List<Item> list)
        {
            genres = list;
            bool required = view.Aio?.GenreRequired == true;
            Item opening = list.Find(x => x.Name == "None") ?? (required && list.Count > 0 ? list[0] : null);
            withAll = !required || opening?.Name == "None";
            Item picked = genreName == null ? null : list.Find(x => string.Equals(x.Name, genreName, StringComparison.OrdinalIgnoreCase));
            if (genreName != null && picked == null) genreName = null; // absent de ce catalogue
            genreItem = picked ?? opening;
            MakeFilters();
            LoadPage();
        }

        string Types() => Library.KindOf(view) == kind ? Library.Types(view) : kind;

        // ---- Pages de la grille ----

        void Clear()
        {
            gen++;
            foreach (Tile t in tiles) { Drop(t); t.Dead = true; }
            tiles.Clear();
            total = 0;
            cur = 0;
            failed = false;
            loading = true;
            SetScroll(0, false);
            pendingId = null;
            WantFeatured();
            ShowState();
        }

        async void LoadPage()
        {
            if (view == null || genres == null) return;
            int g = gen, start = tiles.Count;
            loading = true;
            failed = false;
            ShowState();
            long t0 = clock.ElapsedMilliseconds;
            ItemsResult r;
            try { r = await Library.Browse(view.Id, Types(), genreItem?.Id, unplayed, start, PageSize); }
            catch (Exception e)
            {
                Probe.Log("discover", "page " + start + " de « " + view.Name + " » : échec " + e.GetType().Name + " " + e.Message);
                if (Closed || g != gen) return;
                Guard("Page", () => { loading = false; failed = true; pageTime = Lang.T("search.panel_failed"); ShowState(); });
                return;
            }
            if (Closed || g != gen) return;
            Guard("Page", () =>
            {
                long ms = clock.ElapsedMilliseconds - t0;
                Item[] items = r?.Items ?? new Item[0];
                pages++;
                pageTime = items.Length + " en " + (ms / 1000.0).ToString("0.0") + " s";
                total = r?.TotalRecordCount ?? 0;
                Probe.Log("discover", "« " + view.Name + " » " + Types() + " genre " + (genreItem?.Name ?? "-") + (unplayed ? " non vus" : "")
                    + " : " + items.Length + " titres dès " + start + " sur " + total + ", " + ms + " ms");
                foreach (Item it in items)
                    tiles.Add(new Tile { Index = tiles.Count, Item = it, Url = Library.Poster(it, TileW) });
                // Une page vide met fin à la suite, même si le total annoncé est plus grand.
                if (items.Length == 0) total = tiles.Count;
                loading = false;
                ShowState();
                EnsureBuilder();
                if (start == 0) WantFeatured();
                MaybeMore();
            });
        }

        bool HasMore => !loading && !failed && tiles.Count > 0 && tiles.Count < total;

        // La suite, quand le focus approche de la dernière ligne chargée.
        void MaybeMore()
        {
            if (!HasMore) return;
            int lastRow = (tiles.Count - 1) / Cols;
            if (cur / Cols >= lastRow - 2) LoadPage();
        }

        void ShowState()
        {
            if (count == null) return;
            count.Text = total > 0 && !unplayed ? (total == 1 ? Lang.T("discover.count_one") : Lang.T("discover.count", total)) : "";
            string text = "";
            if (views != null && views.Count == 0) text = Lang.T("home.empty");
            else if (failed && tiles.Count == 0) text = Lang.T("discover.failed");
            else if (loading && tiles.Count == 0) text = Lang.T("home.row_loading");
            else if (!loading && !failed && tiles.Count == 0 && view != null) text = Lang.T("discover.empty");
            message.Text = text;
            if (text.Length > 0) message.Show(); else message.Hide();
            int rows = (tiles.Count + Cols - 1) / Cols;
            if (count.Text.Length > 0) count.Show(); else count.Hide();
            paneHeight = GridY + rows * RowStep + 40;
        }

        // ---- Ligne des filtres ----

        // Types proposés : celui en cours, ceux qu'un catalogue porte, et films et séries si un
        // catalogue mixte existe (pas les collections, qu'un catalogue mixte contient rarement).
        List<string> OfferedKinds()
        {
            var list = new List<string>();
            if (views == null) return list;
            bool untyped = views.Exists(v => Library.KindOf(v) == null);
            foreach (string k in Kinds)
                if (k == kind || views.Exists(v => Library.KindOf(v) == k) || (untyped && k != "BoxSet")) list.Add(k);
            return list;
        }

        // Les catalogues du type en cours, les mixtes, et celui en cours.
        List<Item> Siblings()
        {
            var list = new List<Item>();
            if (views == null) return list;
            foreach (Item v in views)
            {
                string k = Library.KindOf(v);
                if (k == kind || k == null || v.Id == view?.Id) list.Add(v);
            }
            return list;
        }

        List<Item> Pickable() => genres == null ? new List<Item>() : genres.FindAll(x => x.Name != "None");

        static string KindText(string k) => Lang.T(k == "Movie" ? "discover.movies" : k == "Series" ? "discover.shows" : "discover.boxsets");

        string GenreText() => genreName ?? (withAll ? Lang.T("discover.all_genres") : genreItem?.Name ?? "");

        // Refaite à chaque changement (une dizaine de vues, peu coûteux). Une ligne par
        // contrôle, comme la version web ; le nombre de titres dessous. La pastille blanche
        // d'une ligne (Thumb) part de sa place d'avant et glisse jusqu'au nouveau choix.
        void MakeFilters()
        {
            if (pane == null) return;
            FKind? was = filterFocus < filters.Count ? filters[filterFocus].Kind : (FKind?)null;
            string wasValue = filterFocus < filters.Count ? filters[filterFocus].Value : null;
            var thumbFrom = new Dictionary<FKind, (float x, float w)>();
            foreach (var kv in thumbs) thumbFrom[kv.Key] = (kv.Value.CurrentPosition.X, kv.Value.CurrentSize.Width); // position affichée, même en plein glissement
            var oldChosen = new Dictionary<FKind, string>(shownChosen); // choix affichés avant ce changement
            foreach (Filter f in filters) { f.Box.Hide(); toDrop.Enqueue(f.Box); if (f.Ring != null) { f.Ring.Hide(); toDrop.Enqueue(f.Ring); } }
            foreach (View gv in groups) { gv.Hide(); toDrop.Enqueue(gv); }
            foreach (View tv in thumbs.Values) { tv.Hide(); toDrop.Enqueue(tv); }
            filters.Clear();
            groups.Clear();
            thumbs.Clear();
            groupRings.Clear();
            groupBacks.Clear();
            if (view == null) { EnsureBuilder(); return; }
            int y = TopY, row = 0;
            List<string> offered = OfferedKinds();
            if (offered.Count > 1)
            {
                int x = X0 + PillPad;
                foreach (string k in offered) x = Pill(FKind.Type, k, KindText(k), x, y, row);
                Group(FKind.Type, y, x, row, thumbFrom, oldChosen);
                y += FilterH + RowGap;
                row++;
            }
            Add(FKind.Catalog, null, view.Name ?? "", X0, y, LeftW, row++);
            y += FilterH + RowGap;
            // Pendant la demande des genres d'un nouveau catalogue, le champ Genre garde sa
            // place d'avant : sinon la ligne Tout / Non vus monte puis redescend (« glitch »
            // vu par l'auteur en passant de Films à Séries).
            bool showGenre = genres == null ? genreRow : Pickable().Count > 0;
            genreRow = showGenre;
            if (showGenre)
            {
                Add(FKind.Genre, null, genres == null ? Lang.T("home.row_loading") : GenreText(), X0, y, LeftW, row++);
                y += FilterH + RowGap;
            }
            int fx = Pill(FKind.Filter, null, Lang.T("discover.all"), X0 + PillPad, y, row);
            fx = Pill(FKind.Filter, "unplayed", Lang.T("discover.unplayed"), fx, y, row);
            Group(FKind.Filter, y, fx, row, thumbFrom, oldChosen);
            count.PositionY = y + FilterH + 16;
            // Le focus reste sur le même contrôle ; dans une ligne de pastilles, sur la choisie.
            int i = filters.FindIndex(f => f.Kind == was && (IsPill(f) ? Selected(f) : f.Value == wasValue));
            if (i < 0) i = filters.FindIndex(f => f.Kind == was);
            filterFocus = Math.Max(0, i);
            Paint();
            EnsureBuilder();
        }

        static bool IsPill(Filter f) => f.Kind == FKind.Type || f.Kind == FKind.Filter;

        // Fond commun d'une ligne de pastilles (capsule sombre du web), de X0 jusqu'à end, son
        // contour de focus, et la pastille blanche du choix, qui glisse (animation de la TV).
        void Group(FKind k, int y, int end, int row, Dictionary<FKind, (float x, float w)> from, Dictionary<FKind, string> oldChosen)
        {
            int w = end - X0 + PillPad - 4;
            var g = new View { Position = new Position(X0, y), Size = new Size(w, FilterH), BackgroundColor = Theme.Surface }.Rounded(FilterH / 2f);
            // Focus comme les cartes de la liste des liens (demande de l'auteur) : contour au centre
            // transparent et fond assombri de 20 % (Paint).
            var ring = Theme.HollowRing(w, FilterH, FilterH / 2);
            ring.Position = new Position(X0 - Theme.RingOut, y - Theme.RingOut);
            ring.Hide();
            Filter chosen = filters.Find(f => f.Kind == k && Selected(f));
            var thumb = new View { Position = new Position(X0, y + PillPad), Size = new Size(PillMinW, PillH), BackgroundColor = Color.White }.Rounded(PillH / 2f);
            left.Add(thumb);
            left.Add(g);
            left.Add(ring);
            thumb.LowerToBottom();
            g.LowerToBottom();
            ring.LowerToBottom();
            groups.Add(g);
            groups.Add(ring);
            groupRings[row] = ring;
            groupBacks[row] = g;
            thumbs[k] = thumb;
            shownChosen[k] = chosen?.Value;
            if (chosen == null) { thumb.Hide(); return; }
            float tx = chosen.Box.PositionX, tw = chosen.Box.SizeWidth;
            // Pastille d'avant jamais affichée (largeur 0) : pas de point de départ, pas de glissement.
            if (!from.TryGetValue(k, out var old) || old.w <= 0 || (old.x == tx && old.w == tw))
            {
                thumb.PositionX = tx;
                thumb.SizeWidth = tw;
                return;
            }
            thumb.PositionX = old.x;
            thumb.SizeWidth = old.w;
            var a = new Animation(SlideMs);
            a.AnimateTo(thumb, "PositionX", tx, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            a.AnimateTo(thumb, "SizeWidth", tw, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            a.Play();
            grows.Add(a);
            if (grows.Count > 8) grows.RemoveAt(0);
            // Les textes changent de couleur quand la pastille blanche est à mi-chemin (Paint
            // les laisse jusque-là).
            oldChosen.TryGetValue(k, out string before);
            foreach (Filter f in filters)
                if (f.Kind == k) f.Label.TextColor = f.Value == before ? Theme.Ink : Theme.Text;
            sliding.Add(k);
            colorTimer?.Stop();
            colorTimer = new Timer(SlideMs / 2);
            colorTimer.Tick += (s, e) => { if (!Closed) Guard("Thumb", () => { sliding.Clear(); Paint(); }); return false; };
            colorTimer.Start();
        }

        // Pastille de la version web : largeur selon le texte (demandée à la TV, comme les
        // pastilles de la recherche). Même police et même taille que les champs.
        int Pill(FKind k, string value, string text, int x, int y, int row)
        {
            var label = new TextLabel { PointSize = Theme.SizeBody, Text = text };
            float tw = label.NaturalSize2D.Width;
            if (tw <= 0) tw = text.Length * 12;
            int w = Math.Max(PillMinW, (int)tw + 44);
            Add(k, value, label, x, y + PillPad, w, PillH, row);
            return x + w + 4;
        }

        // Champ (catalogue, genre) : sur toute la largeur de la colonne.
        void Add(FKind k, string value, string text, int x, int y, int w, int row) =>
            Add(k, value, new TextLabel { PointSize = Theme.SizeBody, Text = text }, x, y, w, FilterH, row);

        void Add(FKind k, string value, TextLabel label, int x, int y, int w, int h, int row)
        {
            bool field = k == FKind.Catalog || k == FKind.Genre;
            var f = new Filter { Kind = k, Value = value, Row = row, Label = label };
            int r = field ? Theme.RadiusLarge : h / 2;
            if (field)
            {
                // Focus comme les cartes de la liste des liens (demande de l'auteur) : contour au
                // centre transparent, champ assombri de 20 % (Paint) ; pour une ligne de pastilles,
                // le contour entoure la ligne entière (Group).
                f.Ring = Theme.HollowRing(w, h, r);
                f.Ring.Position = new Position(x - Theme.RingOut, y - Theme.RingOut);
                f.Ring.Hide();
                left.Add(f.Ring);
            }
            f.Box = new View { Position = new Position(x, y), Size = new Size(w, h) }.Rounded(r);
            label.Position = new Position(field ? 22 : 0, 0);
            label.Size = new Size(field ? w - 44 : w, h);
            label.Ellipsis = true;
            label.HorizontalAlignment = field ? HorizontalAlignment.Begin : HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            f.Box.Add(label);
            left.Add(f.Box);
            filters.Add(f);
        }

        bool Selected(Filter f)
        {
            switch (f.Kind)
            {
                case FKind.Type: return f.Value == kind;
                case FKind.Filter: return (f.Value == "unplayed") == unplayed;
                default: return false;
            }
        }

        // Couleurs de la version web : pastille choisie blanche au texte foncé, les autres
        // transparentes au texte blanc ; champs sur fond léger. Le focus : un contour blanc.
        void Paint()
        {
            for (int i = 0; i < filters.Count; i++)
            {
                Filter f = filters[i];
                bool on = inFilters && i == filterFocus;
                bool field = f.Kind == FKind.Catalog || f.Kind == FKind.Genre;
                // Pastilles : fond transparent, la pastille blanche (Thumb) glisse dessous.
                f.Box.BackgroundColor = field ? (on ? Theme.Shade : Theme.Surface) : Color.Transparent;
                if (field) f.Label.TextColor = Color.White;
                else if (!sliding.Contains(f.Kind)) f.Label.TextColor = Selected(f) ? Theme.Ink : Theme.Text;
                if (f.Ring != null) { if (on) f.Ring.Show(); else f.Ring.Hide(); }
            }
            // Ligne de pastilles regardée : le contour entoure toute la ligne.
            Filter cf = inFilters && filterFocus < filters.Count ? filters[filterFocus] : null;
            foreach (var kv in groupRings)
            {
                bool on = cf != null && IsPill(cf) && cf.Row == kv.Key;
                if (on) kv.Value.Show(); else kv.Value.Hide();
                if (groupBacks.TryGetValue(kv.Key, out View back)) back.BackgroundColor = on ? Theme.Shade : Theme.Surface;
            }
            foreach (Tile t in live) PaintTile(t);
            GrowFocused();
        }

        void PaintTile(Tile t)
        {
            if (t.Ring == null) return;
            if (!inFilters && t.Index == cur) t.Ring.Show(); else t.Ring.Hide();
        }

        // L'affiche regardée grossit légèrement, l'ancienne revient à sa taille (demande de
        // l'auteur) : animation jouée par la TV. Chaque animation va jusqu'au bout (en arrêter
        // une laisserait une affiche à moitié grossie).
        void GrowFocused()
        {
            Tile want = !inFilters && cur < tiles.Count && tiles[cur].Built ? tiles[cur] : null;
            if (want == grown) return;
            if (want != null) ArtLayer.Hover(want.Item); // arrière-plan d'après l'image
            var a = new Animation(Theme.Slide);
            bool any = false;
            if (grown?.Root != null) { a.AnimateTo(grown.Root, "Scale", new Vector3(1f, 1f, 1f), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut)); any = true; }
            if (want?.Root != null)
            {
                want.Root.RaiseToTop();
                a.AnimateTo(want.Root, "Scale", new Vector3(Theme.Grow, Theme.Grow, 1f), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                any = true;
            }
            grown = want;
            if (!any) return;
            a.Play();
            grows.Add(a);
            if (grows.Count > 8) grows.RemoveAt(0);
        }

        // ---- Choix ----

        void FilterOk(Filter f)
        {
            switch (f.Kind)
            {
                case FKind.Type: SwitchKind(f.Value); break;
                case FKind.Filter:
                    if ((f.Value == "unplayed") == unplayed) { Ignored(); return; }
                    unplayed = f.Value == "unplayed";
                    Clear();
                    MakeFilters();
                    LoadPage();
                    break;
                case FKind.Catalog:
                {
                    List<Item> list = Siblings();
                    if (list.Count < 2) { Ignored(); return; }
                    var names = list.ConvertAll(v => v.Name ?? "");
                    OpenDrop(f, names, list.FindIndex(v => v.Id == view.Id), i =>
                    {
                        Item v = list[i];
                        if (v.Id == view.Id) return;
                        Open(v, Library.KindOf(v) ?? (kind == "BoxSet" ? "Movie" : kind));
                    });
                    break;
                }
                case FKind.Genre:
                {
                    if (genres == null) { Ignored(); return; } // genres encore demandés
                    List<Item> pick = Pickable();
                    var names = new List<string>();
                    if (withAll) names.Add(Lang.T("discover.all_genres"));
                    foreach (Item g in pick) names.Add(g.Name ?? "");
                    int at = genreName == null ? (withAll ? 0 : pick.FindIndex(g => g.Id == genreItem?.Id)) : names.IndexOf(genreName);
                    OpenDrop(f, names, Math.Max(0, at), i =>
                    {
                        string next = withAll && i == 0 ? null : names[i];
                        if (next == genreName) return;
                        genreName = next;
                        Clear();
                        SetGenres(genres);
                    });
                    break;
                }
            }
        }

        // ---- Liste déroulante, dans la continuité du champ (demande de l'auteur) ----

        // Boîte de choix commune (Ui/Picker, module 14), juste sous le champ et de sa largeur,
        // par-dessus la carte du titre ; le choix en cours est regardé à l'ouverture.
        void OpenDrop(Filter f, List<string> names, int current, Action<int> chosen) =>
            picker.Open(names, Math.Max(0, Math.Min(current, names.Count - 1)), X0, LeftW, f.Box.PositionY, f.Box.PositionY + FilterH, chosen);

        // Retour ferme la liste sans rien changer.
        public override bool OnBack()
        {
            if (cardMenu.Back()) return true;
            if (!picker.IsOpen) return false;
            Guard("Back", picker.Close);
            return true;
        }

        // Le type : le catalogue de même nom dans ce type s'il existe, sinon ce catalogue
        // filtré s'il est mixte, sinon le dernier parcouru de ce type, sinon le premier.
        void SwitchKind(string next)
        {
            if (next == kind) { Ignored(); return; }
            Item sibling = views.Find(v => v.Name == view.Name && Library.KindOf(v) == next && v.Id != view.Id);
            if (sibling != null) { Open(sibling, next); return; }
            if (Library.KindOf(view) == null) { Open(view, next); return; }
            string last = ReadPref(UserKey + "." + next);
            Item target = views.Find(v => v.Id == last && Library.KindOf(v) == next)
                ?? views.Find(v => Library.KindOf(v) == next)
                ?? (next != "BoxSet" ? views.Find(v => Library.KindOf(v) == null) : null);
            if (target == null) { Ignored(); return; }
            Open(target, next);
        }

        // ---- Cartes, faites par petits morceaux ----

        void EnsureBuilder()
        {
            if (builder != null || Closed) return;
            builder = new Timer(1);
            builder.Tick += (s, e) =>
            {
                bool more = false;
                Guard("Build", () => more = BuildStep());
                if (more) return true;
                builder = null;
                return false;
            };
            builder.Start();
        }

        int FocusRow => cur / Cols;

        bool BuildStep()
        {
            if (Closed) return false;
            if (toDrop.Count > 0)
            {
                View v = toDrop.Dequeue();
                v.Unparent();
                v.Dispose();
                return true;
            }
            // Seules les lignes proches du focus sont parcourues.
            Tile best = null;
            int bestPrio = int.MaxValue, fr = FocusRow;
            int from = Math.Max(0, (fr - 1) * Cols), to = Math.Min(tiles.Count, (fr + BuildRows + 2) * Cols);
            for (int i = from; i < to; i++)
            {
                Tile t = tiles[i];
                if (t.Built && t.Texted && (t.Requested || t.Url == null)) continue;
                int p = Priority(t);
                if (p < bestPrio) { bestPrio = p; best = t; }
            }
            if (best == null)
            {
                // Plus rien à faire près du focus : une carte lointaine est détruite.
                Tile far = null;
                foreach (Tile t in live) if (Math.Abs(t.Index / Cols - fr) > KeepRows) { far = t; break; }
                if (far == null) return false;
                Drop(far);
                dropped++;
                return true;
            }
            if (!best.Built) BuildTile(best);
            else if (!best.Texted) TextTile(best);
            else
            {
                best.Requested = true;
                Tile t = best;
                imagesAsked++;
                // Carte trop loin : le chargeur abandonne la demande, elle sera refaite au retour.
                ImageCache.Request(t.Url, () =>
                {
                    int p = Priority(t);
                    if (p == int.MaxValue && t.Built) t.Requested = false;
                    return p;
                }, path => Guard("Tile", () => OnTileFile(t, path)));
            }
            return true;
        }

        // Plus petit = plus tôt. Les lignes visibles d'abord, puis une ligne au-dessus et deux
        // en dessous.
        int Priority(Tile t)
        {
            if (Closed || t.Dead) return int.MaxValue;
            int row = t.Index / Cols, fr = FocusRow;
            int d = row - fr;
            if (d < -1 || d > BuildRows + 1) return int.MaxValue;
            return Math.Abs(d) * 100 + Math.Abs(t.Index % Cols - cur % Cols);
        }

        void BuildTile(Tile t)
        {
            t.Built = true;
            live.Add(t);
            built++;
            // Grossissement autour du centre de l'affiche, la position restant celle du coin.
            t.Root = new View
            {
                Position = new Position(GX + t.Index % Cols * Step, GridY + t.Index / Cols * RowStep),
                Size = new Size(TileW, ImageH),
                PivotPoint = Tizen.NUI.PivotPoint.Center,
                PositionUsesPivotPoint = false,
            };
            t.Ring = Theme.FocusRing(TileW, ImageH, Theme.RadiusSmall);
            t.Ring.Position = new Position(-Theme.RingOut, -Theme.RingOut);
            t.Frame = new View { Size = new Size(TileW, ImageH), BackgroundColor = Theme.Placeholder }.Rounded(Theme.RadiusSmall);
            t.Root.Add(t.Ring);
            t.Root.Add(t.Frame);
            pane.Add(t.Root);
            PaintTile(t);
            if (t.Index == cur) GrowFocused();
        }

        void TextTile(Tile t)
        {
            Item it = t.Item;
            t.Texted = true;
            if (!showTitle && t.Url == null) // affiche sans image : son titre reste montré (web)
                t.Frame.Add(Pictures.NoArt(it.Name, TileW, ImageH));
            if (showTitle)
                t.Root.Add(new TextLabel
                {
                    Position = new Position(0, ImageH + 12),
                    Size = new Size(TileW, TitleLineH),
                    PointSize = Theme.SizeBody,
                    Ellipsis = true,
                    TextColor = Color.White,
                    Text = it.Name ?? "",
                });
            string caption = showYear ? Library.YearScore(it) : null;
            if (caption != null)
                t.Root.Add(new TextLabel
                {
                    Position = new Position(0, ImageH + 12 + (showTitle ? TitleLineH : 0)),
                    Size = new Size(TileW, CaptionH),
                    PointSize = Theme.SizeInfo,
                    TextColor = Theme.Grey,
                    Ellipsis = true,
                    Text = caption,
                });
            if (it.UserData?.Played == true) t.Frame.Add(Check(TileW - 52, 12));
        }

        static View Check(int x, int y)
        {
            var b = new View { Name = CardMenu.CheckName, Position = new Position(x, y), Size = new Size(40, 32), BackgroundColor = Theme.Accent }.Rounded(16);
            b.Add(new TextLabel { Size = new Size(40, 32), PointSize = Theme.SizeSmall, TextColor = Theme.OnAccent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = "✓" });
            return b;
        }

        // Carte retirée (lointaine, ou d'un ancien filtre) : refaite si le focus revient.
        void Drop(Tile t)
        {
            if (t.Root != null) { t.Root.Hide(); toDrop.Enqueue(t.Root); }
            live.Remove(t);
            if (t == grown) grown = null;
            t.Root = t.Ring = t.Frame = null;
            t.Built = t.Texted = t.Requested = false;
            EnsureBuilder();
        }

        void OnTileFile(Tile t, string path)
        {
            if (Closed || path == null || t.Frame == null || t.Dead) return;
            View v = Pictures.Cover(path, TileW, ImageH, out ImageView img);
            if (v == null) return;
            img.Rounded(Theme.RadiusSmall); // images arrondies au décodage (charte, comme Favoris)
            t.Frame.Add(v);
            v.LowerToBottom();
            Fade(img);
        }

        // Image qui apparaît en fondu une fois prête (voir ItemScreen.Fade).
        void Fade(ImageView img)
        {
            if (img == null) return;
            if (img.LoadingStatus == ImageView.LoadingStatusType.Ready) { imagesShown++; return; }
            img.Opacity = 0;
            img.ResourceReady += (s, e) =>
            {
                if (Closed) return;
                imagesShown++;
                var a = new Animation(Theme.Fade);
                a.AnimateTo(img, "Opacity", 1f);
                a.Play();
                fades.Add(a);
                if (fades.Count > 8) fades.RemoveAt(0);
            };
        }

        // ---- Carte du titre regardé (comme la fiche de la recherche) ----

        // L'affiche regardée (gardée quand on va aux contrôles), 0,3 s après l'arrêt.
        void WantFeatured()
        {
            Item it = cur < tiles.Count ? tiles[cur].Item : null;
            string id = it?.Id ?? "";
            if (id == pendingId) return;
            pendingId = id;
            featureTimer?.Stop();
            if (it == null) { ShowFeatured(null); return; }
            featureTimer = new Timer(FeatureDelayMs);
            featureTimer.Tick += (s, e) => { if (!Closed) Guard("Featured", () => ShowFeatured(it)); return false; };
            featureTimer.Start();
        }

        void ShowFeatured(Item it)
        {
            int g = ++featureGen;
            if (ficheLogo != null) { ficheLogo.Unparent(); ficheLogo.Dispose(); ficheLogo = null; }
            ficheTitle.Hide();
            if (it == null) { fiche.Hide(); ShowLand(null); return; }
            // Comme la carte ouverte de l'accueil : une vignette « Thumb » porte déjà le titre
            // (ni logo ni titre écrit) ; sinon le logo, et sans logo le titre écrit.
            bool titleInImage = it.Type != "Episode" && it.ImageTags?.Thumb != null;
            string logo = titleInImage ? null : Library.Logo(it, LogoW);
            ficheTitle.Text = Library.Title(it);
            if (!titleInImage && logo == null) ficheTitle.Show();
            FillFiche(details.TryGetValue(it.Id, out Item known) ? known : it);
            fiche.Show();
            if (known == null && it.Overview == null) AskDetails(it, g);
            List<string> urls = Library.LandscapeCandidates(it, LeftW);
            if (urls.Count == 0) ShowLand(null);
            else
            {
                imagesAsked++;
                ImageCache.RequestAny(urls, () => Closed || g != featureGen ? int.MaxValue : 0,
                    path => Guard("Landscape", () => { if (g == featureGen) ShowLand(path); }));
            }
            if (logo != null)
            {
                imagesAsked++;
                ImageCache.Request(logo, () => Closed || g != featureGen ? int.MaxValue : 0,
                    path => Guard("Logo", () => { if (g == featureGen) ShowLogo(path); }));
            }
        }

        // Informations et résumé ; le résumé manque souvent dans une liste : demandé à part
        // (Library.Details), une fois par titre.
        void FillFiche(Item it)
        {
            ficheMeta.Text = Library.Meta(it);
            ficheText.Text = Pictures.Cut(it.Overview, 260) ?? "";
        }

        async void AskDetails(Item it, int g)
        {
            Item d;
            try { d = await Library.Details(it.Id); }
            catch (Exception) { return; }
            if (Closed || d == null) return;
            details[it.Id] = d;
            if (g == featureGen) Guard("Details", () => FillFiche(d));
        }

        // Logo en bas à gauche de l'image, comme sur la carte ouverte de l'accueil.
        void ShowLogo(string path)
        {
            if (path == null) { ficheTitle.Show(); return; }
            Pictures.ImageSize(path, out int nw, out int nh);
            if (nw <= 0 || nh <= 0) { ficheTitle.Show(); return; }
            float k = Math.Min(1f, Math.Min((float)LogoW / nw, (float)LogoH / nh));
            int w = Math.Max(1, (int)(nw * k)), h = Math.Max(1, (int)(nh * k));
            ImageView logo = Pictures.Decoded(path, w, h);
            if (logo == null) { ficheTitle.Show(); return; }
            logo.Position = new Position(18, FicheH - 18 - h);
            ficheLogo = logo;
            ficheFrame.Add(logo);
            Fade(logo);
        }

        // Image en largeur, en fondu enchaîné : la nouvelle apparaît une fois prête, les
        // précédentes s'effacent puis sont détruites.
        void ShowLand(string path)
        {
            ImageView img = null;
            View v = path == null ? null : Pictures.Cover(path, LeftW, FicheH, out img);
            if (v == null)
            {
                foreach (View o in lands.ToArray()) Retire(o);
                return;
            }
            v.Opacity = 0;
            ficheFrame.Add(v);
            v.LowerToBottom();
            lands.Add(v);
            Action show = () =>
            {
                if (Closed || !lands.Contains(v)) return;
                landShown++;
                foreach (View o in lands.ToArray()) if (o != v) Retire(o);
                var a = new Animation(Theme.Fade);
                a.AnimateTo(v, "Opacity", 1f);
                a.Play();
                KeepFade(a);
            };
            if (img.LoadingStatus == ImageView.LoadingStatusType.Ready) show();
            else img.ResourceReady += (s, e) => Guard("Land", show);
        }

        void Retire(View o)
        {
            lands.Remove(o);
            var a = new Animation(Theme.Fade);
            a.AnimateTo(o, "Opacity", 0f);
            a.Finished += (s, e) => { o.Unparent(); o.Dispose(); };
            a.Play();
            KeepFade(a);
        }

        void KeepFade(Animation a)
        {
            backFades.Add(a);
            if (backFades.Count > 8) backFades.RemoveAt(0);
        }

        // ---- Défilement ----

        // La ligne regardée reste entière à l'écran ; les filtres reviennent en haut.
        void Refocus()
        {
            Paint();
            int target = scroll;
            if (cur < Cols) target = 0;
            else
            {
                int top = GridY + cur / Cols * RowStep - 10, bottom = top + RowStep;
                if (bottom - target > 1080 - ScrollMargin) target = bottom - (1080 - ScrollMargin);
                if (top - target < ScrollMargin) target = top - ScrollMargin;
            }
            target = Math.Max(0, Math.Min(target, Math.Max(0, paneHeight - 1080)));
            SetScroll(target, true);
            if (!inFilters && cur < tiles.Count && !tiles[cur].Built) BuildTile(tiles[cur]);
            EnsureBuilder();
            ImageCache.Pump();
            MaybeMore();
            WantFeatured();
        }

        void SetScroll(int target, bool animate)
        {
            if (pane == null || target == scroll) return;
            scroll = target;
            scrollAnim?.Stop();
            if (!animate) { pane.PositionY = -scroll; return; }
            scrollAnim = new Animation(ScrollMs);
            scrollAnim.AnimateTo(pane, "PositionY", (float)-scroll, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            scrollAnim.Play();
        }

        // ---- Touches ----

        public override string SidebarSection => "discover";
        public override bool ArtBackdrop => true;

        public override bool OnKey(string key)
        {
            bool done = true;
            Guard("Key " + key, () => done = cardMenu.Key(key) || (picker.IsOpen ? (picker.OnKey(key) || Ignore()) : inFilters ? FilterKeys(key) : GridKeys(key)));
            return done;
        }

        // Haut/Bas d'une ligne à l'autre (sur la pastille choisie), OK choisit (dans une ligne de
        // pastilles : le choix suivant), Droite : la grille, sur l'affiche quittée ; Gauche : la
        // barre latérale.
        bool FilterKeys(string key)
        {
            if (filters.Count == 0)
            {
                if (failed && (key == "Right" || key == "Return" || key == "KP_Enter")) { Retry(); return true; }
                if (key == "Left") { App.Current.ToSidebar(); return true; }
                return key == "Up" || key == "Down" || key == "Right";
            }
            Filter f = filters[filterFocus];
            switch (key)
            {
                case "Left":
                case "Right":
                {
                    // Gauche : la barre latérale ; Droite : la grille, depuis n'importe quel contrôle
                    // (l'auteur, 2026-11-19 : choisir avec Gauche/Droite obligeait à passer par la
                    // dernière pastille pour aller à la grille). Le choix se fait avec OK.
                    if (key == "Left") { App.Current.ToSidebar(); return true; }
                    if (tiles.Count == 0) { if (failed) Retry(); else Ignored(); return true; }
                    inFilters = false;
                    if (cur >= tiles.Count) cur = 0;
                    Refocus();
                    return true;
                }
                case "Up":
                case "Down":
                {
                    int row = f.Row + (key == "Up" ? -1 : 1);
                    int i = filters.FindIndex(x => x.Row == row && Selected(x));
                    if (i < 0) i = filters.FindIndex(x => x.Row == row);
                    if (i < 0) { Ignored(); return true; }
                    filterFocus = i;
                    Paint();
                    return true;
                }
                case "Return":
                case "KP_Enter":
                    if (IsPill(f))
                    {
                        // Ligne de pastilles : OK passe au choix suivant (Films → Séries → Films),
                        // la pastille blanche glisse (MakeFilters).
                        List<Filter> row = filters.FindAll(x => x.Row == f.Row);
                        int at = row.FindIndex(Selected);
                        FilterOk(row[(at + 1) % row.Count]);
                        return true;
                    }
                    FilterOk(f);
                    return true;
            }
            return false;
        }

        // Catalogue en panne : redemandé là où il s'est arrêté.
        void Retry()
        {
            if (views == null) LoadViews();
            else if (view == null) Ignored();
            else if (genres == null) LoadGenres(view, gen);
            else LoadPage();
        }

        bool GridKeys(string key)
        {
            int col = cur % Cols;
            switch (key)
            {
                case "Left":
                    if (col == 0) { inFilters = true; Paint(); return true; } // vers les contrôles
                    cur--;
                    break;
                case "Right":
                    if (col == Cols - 1 || cur + 1 >= tiles.Count) { Ignored(); return true; }
                    cur++;
                    break;
                case "Up":
                    if (cur < Cols) { Ignored(); return true; }
                    cur -= Cols;
                    break;
                case "Down":
                    if (cur + Cols < tiles.Count) cur += Cols;
                    else if (cur / Cols < (tiles.Count - 1) / Cols) cur = tiles.Count - 1; // dernière ligne incomplète
                    else { Ignored(); MaybeMore(); return true; }
                    break;
                case "Return":
                case "KP_Enter":
                    if (cur < tiles.Count)
                    {
                        Tile t = tiles[cur];
                        cardMenu.Press(t.Item, () => Nav.Push(ItemScreen.For(t.Item)), t.Frame);
                    }
                    return true;
                default:
                    return false;
            }
            Refocus();
            return true;
        }

        static void Ignored() => App.Current.KeyIgnored();
        static bool Ignore() { Ignored(); return true; }

        // ---- Mesures ----

        string NoteText()
        {
            if (Closed) return null;
            int alive = live.Count;
            return Lang.T("discover.panel", view?.Name ?? "-", views?.Count ?? 0, viewsTime, kind ?? "-",
                genreItem?.Name ?? "-", genresTime, pages, tiles.Count, total, pageTime,
                alive, built, dropped, imagesAsked, imagesShown, ImageCache.Queued, ImageCache.Running, landShown,
                guard.SlowCount, guard.Slowest(), guard.LastError ?? Lang.T("home.none"));
        }
    }
}
