// Recherche, à la manière de Prime Video (demande de l'auteur) avec les requêtes de la
// version web (pages/search.tsx) :
// - à gauche, le texte tapé et un clavier dans l'application (AZERTY en français,
//   QWERTY en anglais), sans le clavier de la TV ;
// - à droite, avant de taper : les recherches récentes en pastilles, « Reprendre » et les
//   3 premiers catalogues de l'accueil ; à partir de 2 lettres, 0,4 s après la dernière :
//   une rangée Films et séries mélangés, ou deux rangées (réglage combineSearch du compte),
//   « Dans le même esprit que » le titre regardé (sinon le premier résultat), puis
//   Personnes en dernier (demande de l'auteur).
//   Résultats triés par correspondance avec le texte tapé ;
// - sous le clavier, la fiche du titre regardé (sinon du premier de l'écran) : son image
//   en largeur avec le logo dessus, comme une carte ouverte de l'accueil, puis ses
//   informations et son résumé (choix de l'auteur après essai). Couleurs, tailles et focus : la
//   charte (Ui/Theme.cs, module 14).
// Mêmes règles que la page d'un titre : touches, cartes et images faites au tour de
// boucle suivant, par petits morceaux, près du focus (BuildStep).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Tizen.Applications;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Screens
{
    class SearchScreen : Screen
    {
        // Clavier : touches de KeyW, espacées de Pitch, à partir de (KbX, KbY).
        const int KbX = 96, TextY = 60, TextH = 76, KbY = 156, KeyW = 60, Pitch = 68, KbW = 10 * Pitch - (Pitch - KeyW);
        // À droite : pastilles, puis rangées de cartes.
        const int RightX = KbX + KbW + 72, ChipsY = TextY, ChipH = Theme.PillHeight, ChipsMax = 5;
        // Titres de rangées comme l'accueil et la page d'un titre (module 14) : 33 pt demi-gras,
        // dans une boîte de 60 px, cartes 12 px plus bas.
        const int SectionTitleH = 72, SectionLabelH = 60, SectionGap = 28, ScrollMargin = 40;
        const float SectionSize = 33, KeySize = 22; // lettres du clavier : 22 pt (choix validé, module 14)
        const int MinLength = 2, HistoryMax = 12, SearchDelayMs = 400, ScrollMs = 350, SlideMs = Theme.Slide;
        // Sous une affiche : titre 18 pt, année 15 pt grise dessous (comme Favoris).
        const int TitleH = 34, CaptionH = 28;
        const int KeyBudgetMs = 8; // touches créées par tour de boucle, dans cette limite
        // Fiche du titre regardé, sous le clavier ; changée 0,3 s après l'arrêt du focus.
        // Image en 16:9 de la largeur du clavier ; logo à l'échelle de la carte ouverte de
        // l'accueil (360×130 sur 883 px de large).
        const int FicheY = KbY + 5 * Pitch + 24, FicheH = KbW * 9 / 16, LogoW = 274, LogoH = 99, FeatureDelayMs = 300, Catalogues = 3;

        enum KeyKind { Char, Space, Erase, Clear }

        class Key
        {
            public KeyKind Kind;
            public string Text; // caractère, ou clé du texte (Espace, Effacer)
            public int Row, Col, Span = 1;
            public View Box;
            public TextLabel Label;
            public float Center => Col + Span / 2f;
        }

        enum Kind { Chips, Posters, People }

        // Un arrêt du focus à droite : la ligne des pastilles, ou une rangée de cartes.
        class Entry
        {
            public Kind Kind;
            public View Root;
            public int Y, Height;
            public string TitleKey, TitleText, Types;
            public TextLabel Title, Message;
            public View Track;
            public Animation Slide; // gardée ici, sinon elle s'arrête
            public readonly List<Tile> Tiles = new List<Tile>();
            public int Col, First, TileW, ImageH, TextH, Step, Visible;
            public bool Loading = true, Failed;
            public string Time = "-";
            public int Count => Kind == Kind.Chips ? chips.Count : Tiles.Count;
            public List<Chip> chips;
        }

        class Tile
        {
            public Entry Sec;
            public int Index;
            public Item Item;
            public View Root, Ring, Frame;
            public string Url;
            public bool Built, Texted, Requested, Dead;
        }

        class Chip
        {
            public View Box;
            public TextLabel Label;
            public string Term; // null : « Effacer l'historique »
        }

        View root, pane, left;
        TextLabel shown, nothing;
        Guarded guard;

        // Clavier.
        readonly List<Key> keys = new List<Key>();
        int keyFocus, keysBuilt;
        string layoutLang;
        long keysMs;

        // À droite.
        readonly Entry chipsEntry = new Entry { Kind = Kind.Chips, chips = new List<Chip>() };
        string chipsSig;
        readonly List<Entry> browse = new List<Entry>(); // avant de taper : Reprendre, catalogues
        Entry mixed, movies, shows, people, similar;
        string similarName, similarFor; // titre dont la rangée « même esprit » montre les similaires
        readonly Dictionary<string, List<Item>> similarCache = new Dictionary<string, List<Item>>();

        // Fiche du titre regardé.
        View fiche, ficheFrame;
        TextLabel ficheTitle, ficheMeta, ficheText;
        ImageView ficheLogo;
        Item featured;
        int featureGen, landShown;
        Timer featureTimer;
        readonly Dictionary<string, Item> details = new Dictionary<string, Item>();
        readonly List<Animation> backFades = new List<Animation>(); // fondus de la fiche, gardés, sinon ils s'arrêtent
        readonly List<Entry> stops = new List<Entry>();
        Entry focus;      // null : le clavier
        Entry lastRight;  // dernier arrêt de droite, retrouvé avec Droite
        int scroll, paneHeight;
        Animation scrollAnim;
        Timer builder, searchTimer; // gardés dans des champs, sinon ils s'arrêtent
        readonly Queue<View> toDrop = new Queue<View>();

        string typed = "", query = "";
        bool split;
        int gen;
        List<string> history;

        // Mesures (panneau, touche rouge).
        readonly Stopwatch clock = Stopwatch.StartNew();
        int imagesAsked, imagesShown;

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.ScreenBack(Theme.Background) };
            guard = new Guarded(root);
            // La partie droite glisse vers le haut. Les cartes qui sortent à gauche
            // s'effacent (ClipChildren ne coupe rien sur la TV).
            pane = new View { Size = new Size(1920, 1080) };
            root.Add(pane);
            left = new View { Size = new Size(RightX - 24, 1080) };
            root.Add(left);
            fiche = new View { Position = new Position(KbX, FicheY), Size = new Size(KbW, 1080 - FicheY) };
            ficheFrame = new View { Size = new Size(KbW, FicheH), BackgroundColor = Theme.Placeholder }.Rounded(Theme.RadiusLarge);
#pragma warning disable CS0618 // ShadowOffset/ShadowColor : dépréciés en API 8, toujours présents (comme l'accueil)
            ficheTitle = new TextLabel
            {
                Position = new Position(18, FicheH - 70),
                Size = new Size(KbW - 36, 56),
                PointSize = Theme.SizeTitle,
                Ellipsis = true,
                TextColor = Color.White,
                ShadowOffset = new Vector2(2, 2),
                ShadowColor = new Color(0f, 0f, 0f, 0.9f),
            };
#pragma warning restore CS0618
            ficheMeta = new TextLabel { Position = new Position(0, FicheH + 12), Size = new Size(KbW, 30), PointSize = Theme.SizeInfo, TextColor = Theme.Grey };
            ficheText = new TextLabel { Position = new Position(0, FicheH + 48), Size = new Size(KbW, 3 * 31), PointSize = Theme.SizeBody, MultiLine = true, TextColor = Theme.Text };
            ficheFrame.Add(ficheTitle);
            fiche.Add(ficheFrame);
            fiche.Add(ficheMeta);
            fiche.Add(ficheText);
            fiche.Hide();
            left.Add(fiche);
            var textBox = new View { Position = new Position(KbX, TextY), Size = new Size(KbW, TextH), BackgroundColor = Theme.Surface }.Rounded(Theme.RadiusLarge);
            shown = new TextLabel { Position = new Position(24, 0), Size = new Size(KbW - 48, TextH), PointSize = Theme.SizeHeading, VerticalAlignment = VerticalAlignment.Center };
            textBox.Add(shown);
            left.Add(textBox);
            nothing = new TextLabel { Size = new Size(1920 - RightX - 96, 44), PointSize = Theme.SizeBody, TextColor = Theme.Grey };
            nothing.Hide();
            pane.Add(nothing);

            split = !Prefs.CombineSearch; // films et séries en deux rangées (module 13c)
            history = LoadHistory();
            MakeKeys();
            LoadBrowse();
            Layout();
            EnsureBuilder();
            cardMenu = new CardMenu(this, Guard) { Changed = MenuDone };
            root.Add(cardMenu.View); // menu d'une carte, au-dessus de tout
            return root;
        }

        public override void ApplyTexts()
        {
            if (layoutLang != Lang.Current) MakeKeys();
            foreach (Key k in keys) if (k.Label != null) k.Label.Text = KeyText(k);
            ShowTyped();
            if (similar != null) similar.TitleText = Lang.T("search.similar", similarName);
            foreach (Entry s in AllSections())
            {
                s.Title.Text = s.TitleText ?? (s.TitleKey != null ? Lang.T(s.TitleKey) : "");
                if (s.Loading) s.Message.Text = Lang.T("search.searching");
                else if (s.Failed) s.Message.Text = Lang.T("search.failed");
            }
            chipsSig = null;
            Layout();
        }

        public override void OnShow() => Measure.Note = NoteText;
        public override void OnHide() { Measure.Note = null; cardMenu?.Hide(); }
        public override bool OnKeyUp(string key) => cardMenu.KeyUp(key);
        public override bool OnBack() => cardMenu.Back();

        // Menu d'une carte de titre (Ui/CardMenu.cs) ; après un envoi, coche « vu » des cartes de
        // ce titre remise à jour.
        CardMenu cardMenu;

        void MenuDone(Item it)
        {
            foreach (Entry s in AllSections())
                if (s.Kind == Kind.Posters)
                    foreach (Tile t in s.Tiles)
                        if (t.Item == it && t.Frame != null) CardMenu.SetCheck(t.Frame, it.UserData?.Played == true, () => Check(s.TileW - 52, 12));
            // Titre de « Reprendre » (retiré, marqué vu…) : la rangée est redemandée (demande de
            // l'auteur, 2026-11-21).
            if (browse.Count > 0 && browse[0].Tiles.Exists(t => t.Item.Id == it.Id)) RefreshContinue(browse[0]);
        }

        // « Reprendre » refaite à sa place ; le focus garde sa colonne (le titre suivant prend la
        // place du titre retiré), la rangée ne glisse pas.
        async void RefreshContinue(Entry s)
        {
            long t0 = clock.ElapsedMilliseconds;
            List<Item> list;
            try { list = await Library.Continue(); }
            catch (Exception e) { Probe.Log("search", "reprendre : échec " + e.GetType().Name); return; }
            if (Closed) return;
            Guard("Continue", () =>
            {
                int col = s.Col, first = s.First;
                Reset(s);
                Fill(s, list, clock.ElapsedMilliseconds - t0);
                if (s.Tiles.Count > 0)
                {
                    s.Col = Math.Min(col, s.Tiles.Count - 1);
                    first = Math.Max(0, Math.Min(first, s.Col));
                    if (s.Col > first + s.Visible - 1) first = s.Col - s.Visible + 1;
                    s.First = first;
                    s.Track.PositionX = -first * s.Step;
                }
                Layout();
                if (focus == s && s.Tiles.Count > 0)
                {
                    if (!s.Tiles[s.Col].Built) BuildTile(s.Tiles[s.Col]);
                    foreach (Tile t in s.Tiles) PaintTile(t);
                    GrowFocused();
                }
                Probe.Log("search", $"reprendre redemandée ({list.Count} titres)");
            });
        }

        public override void OnClose()
        {
            searchTimer?.Stop();
            featureTimer?.Stop();
            builder?.Stop();
            Measure.Note = null;
        }

        void Guard(string where, Action run) => guard.Run(where, run);


        // ---- Clavier ----

        // Disposition selon la langue de l'application : AZERTY en français, QWERTY sinon.
        // Dernière rangée commune : Espace, apostrophe, tiret, effacer une lettre, tout effacer.
        void MakeKeys()
        {
            foreach (Key k in keys) if (k.Box != null) { k.Box.Hide(); toDrop.Enqueue(k.Box); }
            keys.Clear();
            keysBuilt = 0;
            keysMs = 0;
            layoutLang = Lang.Current;
            string[] rows = layoutLang == "fr"
                ? new[] { "1234567890", "azertyuiop", "qsdfghjklm", "wxcvbnéèàç" }
                : new[] { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm" };
            for (int r = 0; r < rows.Length; r++)
                for (int c = 0; c < rows[r].Length; c++)
                    keys.Add(new Key { Kind = KeyKind.Char, Text = rows[r][c].ToString(), Row = r, Col = c });
            int last = rows.Length;
            keys.Add(new Key { Kind = KeyKind.Space, Text = "search.space", Row = last, Col = 0, Span = 4 });
            keys.Add(new Key { Kind = KeyKind.Char, Text = "'", Row = last, Col = 4 });
            keys.Add(new Key { Kind = KeyKind.Char, Text = "-", Row = last, Col = 5 });
            keys.Add(new Key { Kind = KeyKind.Erase, Text = "←", Row = last, Col = 6, Span = 2 });
            keys.Add(new Key { Kind = KeyKind.Clear, Text = "search.clear_text", Row = last, Col = 8, Span = 2 });
            keyFocus = keys.FindIndex(k => k.Row == 1); // première lettre
            EnsureBuilder();
        }

        static string KeyText(Key k) => k.Kind == KeyKind.Space || k.Kind == KeyKind.Clear ? Lang.T(k.Text) : k.Text;

        void BuildKey(Key k)
        {
            k.Box = new View
            {
                Position = new Position(KbX + k.Col * Pitch, KbY + k.Row * Pitch),
                Size = new Size(k.Span * Pitch - (Pitch - KeyW), KeyW),
            }.Rounded(KeyW / 2f); // capsule (charte) : rondes pour une seule touche
            k.Label = new TextLabel
            {
                Size = new Size(k.Span * Pitch - (Pitch - KeyW), KeyW),
                PointSize = k.Kind == KeyKind.Char ? KeySize : Theme.SizeBody,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Text = KeyText(k),
            };
            k.Box.Add(k.Label);
            left.Add(k.Box);
            PaintKey(k);
        }

        void PaintKey(Key k)
        {
            if (k.Box == null) return;
            bool on = focus == null && keys.IndexOf(k) == keyFocus;
            k.Box.BackgroundColor = on ? Color.White : k.Kind == KeyKind.Char ? Color.Transparent : Theme.Surface;
            k.Label.TextColor = on ? Theme.Ink : Theme.Text;
        }

        void PaintKeys() { foreach (Key k in keys) PaintKey(k); }

        void Press(Key k)
        {
            switch (k.Kind)
            {
                case KeyKind.Char: typed += k.Text; break;
                case KeyKind.Space: if (typed.Length > 0 && !typed.EndsWith(" ")) typed += " "; break;
                case KeyKind.Erase: if (typed.Length > 0) typed = typed.Substring(0, typed.Length - 1); else { Ignored(); return; } break;
                case KeyKind.Clear: if (typed.Length > 0) typed = ""; else { Ignored(); return; } break;
            }
            OnTyped();
        }

        void ShowTyped()
        {
            if (typed.Length == 0)
            {
                shown.Text = Lang.T("search.placeholder");
                shown.TextColor = Theme.Faded;
            }
            else
            {
                shown.Text = typed + "|";
                shown.TextColor = Color.White;
            }
        }

        // Moins de 2 lettres : tout de suite (aucune demande) ; sinon 0,4 s après la dernière.
        void OnTyped()
        {
            ShowTyped();
            searchTimer?.Stop();
            string t = typed.Trim();
            if (t.Length < MinLength) { Search(t); return; }
            searchTimer = new Timer(SearchDelayMs);
            searchTimer.Tick += (s, e) => { if (!Closed) Guard("Delay", () => Search(typed.Trim())); return false; };
            searchTimer.Start();
        }

        // Haut/Bas : la touche de la rangée voisine qui couvre le milieu de la touche regardée.
        int KeyAbove(int step)
        {
            Key cur = keys[keyFocus];
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < keys.Count; i++)
            {
                Key k = keys[i];
                if (k.Row != cur.Row + step) continue;
                float d = cur.Center >= k.Col && cur.Center <= k.Col + k.Span ? 0 : Math.Abs(k.Center - cur.Center);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // ---- Résultats ----

        // Avant de taper : « Reprendre », puis les premiers catalogues de l'accueil.
        void LoadBrowse()
        {
            Entry cont = NewSection(Kind.Posters, "home.continue", null);
            browse.Add(cont);
            LoadContinue(cont);
            LoadCatalogues();
        }

        async void LoadContinue(Entry s)
        {
            long t0 = clock.ElapsedMilliseconds;
            try
            {
                List<Item> list = await Library.Continue();
                if (Closed) return;
                Guard("Continue", () => { Fill(s, list, clock.ElapsedMilliseconds - t0); Layout(); });
            }
            catch (Exception e) { BrowseFailed(s, "reprendre", e); }
        }

        async void LoadCatalogues()
        {
            List<Item> views;
            try { views = await Library.Views(); }
            catch (Exception e) { Probe.Log("search", "catalogues : échec " + e.GetType().Name); return; }
            if (Closed) return;
            for (int i = 0; i < views.Count && i < Catalogues; i++)
            {
                Entry s = NewSection(Kind.Posters, null, null);
                s.TitleText = views[i].Name ?? "";
                s.Title.Text = s.TitleText;
                browse.Add(s);
                LoadCatalogue(s, views[i]);
            }
            Guard("Catalogues", Layout);
        }

        async void LoadCatalogue(Entry s, Item view)
        {
            long t0 = clock.ElapsedMilliseconds;
            try
            {
                ItemsResult r = await Library.Page(view, 0, 20);
                if (Closed) return;
                Guard("Catalogue", () => { Fill(s, new List<Item>(r?.Items ?? new Item[0]), clock.ElapsedMilliseconds - t0); Layout(); });
            }
            catch (Exception e) { BrowseFailed(s, s.TitleText, e); }
        }

        void BrowseFailed(Entry s, string what, Exception e)
        {
            Probe.Log("search", what + " : échec " + e.GetType().Name);
            if (Closed) return;
            s.Loading = false;
            s.Time = Lang.T("search.panel_failed");
            Guard("Browse", Layout); // rangée vide : cachée
        }

        // « Dans le même esprit que » le premier résultat : ses titres similaires.
        // « Dans le même esprit que » suit le titre regardé dans une rangée de résultats
        // (sinon le premier résultat) : demande de l'auteur. Réponses gardées par titre.
        void ShowSimilar(Item best)
        {
            if (best == null || best.Id == similarFor) return;
            similarFor = best.Id;
            similarName = best.Name ?? "";
            if (similar == null) similar = NewSection(Kind.Posters, null, null);
            else Reset(similar);
            similar.TitleText = Lang.T("search.similar", similarName);
            similar.Title.Text = similar.TitleText;
            if (similarCache.TryGetValue(best.Id, out List<Item> known)) Fill(similar, known, 0);
            else FetchSimilar(best, similar, gen);
            Layout();
        }

        async void FetchSimilar(Item best, Entry s, int g)
        {
            long t0 = clock.ElapsedMilliseconds;
            ItemsResult r;
            try { r = await Library.Similar(best.Id); }
            catch (Exception e)
            {
                Probe.Log("search", "similaires : échec " + e.GetType().Name);
                if (Closed || g != gen || similar != s || similarFor != best.Id) return;
                s.Loading = false;
                s.Failed = true;
                s.Time = Lang.T("search.panel_failed");
                s.Message.Text = Lang.T("search.failed");
                Guard("Similar", Layout);
                return;
            }
            if (Closed || g != gen) return;
            Guard("Similar", () =>
            {
                var list = Titles(new List<Item>(r?.Items ?? new Item[0]));
                list.RemoveAll(it => it.Id == best.Id || Identity(it) == Identity(best));
                similarCache[best.Id] = list;
                long ms = clock.ElapsedMilliseconds - t0;
                Probe.Log("search", "similaires à « " + best.Name + " » : " + list.Count + ", " + ms + " ms");
                if (similar != s || similarFor != best.Id) return; // un autre titre regardé entre-temps
                Fill(s, list, ms);
                Layout();
            });
        }

        // Rangée vidée pour un nouveau contenu : cartes retirées au tour de boucle suivant.
        void Reset(Entry s)
        {
            foreach (Tile t in s.Tiles)
            {
                t.Dead = true;
                if (t.Root != null) { t.Root.Hide(); toDrop.Enqueue(t.Root); }
            }
            s.Tiles.Clear();
            s.Col = s.First = 0;
            s.Slide?.Stop();
            s.Track.PositionX = 0;
            s.Loading = true;
            s.Failed = false;
            s.Time = "-";
            s.Message.Text = Lang.T("search.searching");
            s.Message.Show();
            EnsureBuilder();
        }

        void Search(string q)
        {
            if (q == query) return;
            query = q;
            gen++;
            foreach (Entry s in Results()) Drop(s);
            mixed = movies = shows = people = similar = null;
            similarFor = null;
            if (q.Length >= MinLength)
            {
                if (split)
                {
                    movies = NewSection(Kind.Posters, "search.movies", "Movie");
                    shows = NewSection(Kind.Posters, "search.shows", "Series");
                }
                else mixed = NewSection(Kind.Posters, "search.titles", "Movie,Series");
                people = NewSection(Kind.People, "search.people", "Person");
                foreach (Entry s in Results()) Fetch(s, q, gen);
            }
            if (focus != null && focus != chipsEntry && !browse.Contains(focus)) focus = null; // ses cartes ont disparu
            Layout();
        }

        // Rangées de résultats, dans l'ordre affiché.
        IEnumerable<Entry> Results()
        {
            if (mixed != null) yield return mixed;
            if (movies != null) yield return movies;
            if (shows != null) yield return shows;
            if (similar != null) yield return similar;
            if (people != null) yield return people;
        }

        // Les rangées affichées : Reprendre et les catalogues tant qu'aucune recherche n'est lancée.
        IEnumerable<Entry> Sections() => query.Length < MinLength ? browse : Results();

        IEnumerable<Entry> AllSections()
        {
            foreach (Entry s in browse) yield return s;
            foreach (Entry s in Results()) yield return s;
        }

        async void Fetch(Entry s, string q, int g)
        {
            long t0 = clock.ElapsedMilliseconds;
            ItemsResult r = null;
            Exception error = null;
            try { r = await Library.Search(q, s.Types); }
            catch (Exception e) { error = e; }
            if (Closed || g != gen) return;
            long ms = clock.ElapsedMilliseconds - t0;
            Guard("Results", () =>
            {
                if (error != null)
                {
                    Probe.Log("search", s.Types + " « " + q + " » : échec " + error.GetType().Name + " " + error.Message);
                    s.Loading = false;
                    s.Failed = true;
                    s.Time = Lang.T("search.panel_failed");
                    s.Message.Text = Lang.T("search.failed");
                    Layout();
                    return;
                }
                var list = new List<Item>(r?.Items ?? new Item[0]);
                int received = list.Count;
                List<string> words = Words(q);
                if (s == people)
                {
                    // Seulement les personnes que le terme nomme : la recherche trouve aussi
                    // les alias et les orthographes proches (comme le web).
                    list = list.FindAll(p =>
                    {
                        List<string> name = Words(p.Name);
                        return words.TrueForAll(w => name.Exists(n => n.StartsWith(w, StringComparison.Ordinal)));
                    });
                }
                else list = Titles(list);
                // Les personnes gardent l'ordre du serveur, comme la version web (« de gaulle » :
                // Charles de Gaulle d'abord) ; les titres sont triés par correspondance.
                if (s != people) list = ByMatch(list, words);
                Probe.Log("search", s.Types + " « " + q + " » : " + received + " reçus, " + list.Count + " gardés, " + ms + " ms");
                Fill(s, list, ms);
                if (s != people && similarFor == null && list.Count > 0) ShowSimilar(list[0]);
                Layout();
            });
        }

        void Fill(Entry s, List<Item> list, long ms)
        {
            s.Loading = false;
            s.Time = Lang.T("search.panel_time", list.Count, (ms / 1000.0).ToString("0.0"));
            s.Message.Hide();
            for (int i = 0; i < list.Count; i++)
            {
                Item it = list[i];
                string url = s.Kind == Kind.People
                    ? Library.Image(it.Id, "Primary", it.ImageTags?.Primary, s.ImageH)
                    : Library.Poster(it, s.TileW);
                s.Tiles.Add(new Tile { Sec = s, Index = i, Item = it, Url = url });
            }
            EnsureBuilder();
        }

        // Seulement des titres, sans doublon (demande de l'auteur, 2026-11-11). Vu sur le
        // serveur pour « mobland » : sur 48 réponses, un seul vrai titre, des fichiers des
        // bibliothèques TorBox et Real-Debrid (chemin /aiostreams/library/, sans image) et
        // des fiches nommées par leur identifiant (« tt7661390 », « mlt-meta-tt7661390 »),
        // souvent en double. Retirés : fichiers des bibliothèques, épisodes, fiches sans
        // vrai nom ; une seule fiche par identifiant IMDb, TMDB ou TVDB, sinon par sorte,
        // nom et année (la première, dans l'ordre du serveur).
        static readonly System.Text.RegularExpressions.Regex IdName = new System.Text.RegularExpressions.Regex(
            @"^((mlt-meta-)?tt\d{5,}|(tmdb|tvdb|imdb|kitsu|mal|anilist)[:_-]\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static List<Item> Titles(List<Item> list)
        {
            var seen = new HashSet<string>();
            var kept = new List<Item>();
            foreach (Item it in list)
            {
                if (it.Type == "Episode") continue;
                if (it.Path != null && it.Path.StartsWith("/aiostreams/library/", StringComparison.Ordinal)) continue;
                string name = (it.Name ?? "").Trim();
                if (name.Length == 0 || IdName.IsMatch(name)) continue;
                if (!seen.Add(Identity(it))) continue;
                kept.Add(it);
            }
            return kept;
        }

        static string Identity(Item it)
        {
            ProviderIds ids = it.ProviderIds;
            if (!string.IsNullOrEmpty(ids?.Imdb)) return "imdb:" + ids.Imdb;
            if (!string.IsNullOrEmpty(ids?.Tmdb)) return "tmdb:" + it.Type + ":" + ids.Tmdb;
            if (!string.IsNullOrEmpty(ids?.Tvdb)) return "tvdb:" + it.Type + ":" + ids.Tvdb;
            return it.Type + "|" + string.Join(" ", Words(it.Name)) + "|" + it.ProductionYear;
        }

        // Tri par correspondance avec le texte tapé (demande de l'auteur) : titre identique,
        // puis qui commence par le texte, puis dont chaque mot tapé commence un mot, puis
        // qui contient le texte, puis le reste. À égalité, l'ordre du serveur.
        static List<Item> ByMatch(List<Item> list, List<string> words)
        {
            string q = string.Join(" ", words);
            var ranked = new List<(int rank, int index, Item item)>();
            for (int i = 0; i < list.Count; i++)
            {
                List<string> name = Words(list[i].Name);
                string n = string.Join(" ", name);
                int rank = n == q ? 0
                    : n.StartsWith(q, StringComparison.Ordinal) ? 1
                    : words.TrueForAll(w => name.Exists(x => x.StartsWith(w, StringComparison.Ordinal))) ? 2
                    : n.Contains(q) ? 3 : 4;
                ranked.Add((rank, i, list[i]));
            }
            ranked.Sort((a, b) => a.rank != b.rank ? a.rank.CompareTo(b.rank) : a.index.CompareTo(b.index));
            return ranked.ConvertAll(x => x.item);
        }

        // Mots d'un nom, sans accents ni majuscules (nameWords du web).
        static List<string> Words(string text)
        {
            string s = text ?? "";
            try { s = s.Normalize(NormalizationForm.FormD); }
            catch (Exception) { }
            var words = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark || cat == UnicodeCategory.EnclosingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) words.Add(sb.ToString());
            return words;
        }

        Entry NewSection(Kind kind, string titleKey, string types)
        {
            bool round = kind == Kind.People;
            var s = new Entry
            {
                Kind = kind, TitleKey = titleKey, Types = types,
                TileW = round ? 160 : 180, ImageH = round ? 150 : 270, TextH = TitleH + CaptionH, Step = round ? 180 : 200,
            };
            s.Visible = Math.Max(1, (1920 - RightX) / s.Step);
            s.Height = SectionTitleH + s.ImageH + 12 + s.TextH + SectionGap;
            s.Root = new View { Position = new Position(RightX, 0), Size = new Size(1920 - RightX, s.Height) };
            s.Title = new TextLabel { Size = new Size(1100, SectionLabelH), PointSize = SectionSize, TextColor = Color.White, VerticalAlignment = VerticalAlignment.Bottom, FontStyle = Theme.SemiBold(), Ellipsis = true, Text = titleKey != null ? Lang.T(titleKey) : "" };
            s.Root.Add(s.Title);
            s.Track = new View { Position = new Position(0, SectionTitleH) };
            s.Root.Add(s.Track);
            s.Message = new TextLabel { Position = new Position(0, SectionTitleH + 10), Size = new Size(1100, 44), PointSize = Theme.SizeBody, TextColor = Theme.Grey, Text = Lang.T("search.searching") };
            s.Root.Add(s.Message);
            s.Root.Hide();
            pane.Add(s.Root);
            return s;
        }

        // Une rangée d'une recherche précédente : retirée au tour de boucle suivant.
        void Drop(Entry s)
        {
            foreach (Tile t in s.Tiles) t.Dead = true;
            s.Slide?.Stop();
            s.Root.Hide();
            toDrop.Enqueue(s.Root);
            EnsureBuilder();
        }

        // ---- Mise en page de la droite ----

        void Layout()
        {
            if (Closed) return;
            bool showChips = typed.Trim().Length == 0 && history.Count > 0;
            BuildChips(showChips);
            int y = ChipsY;
            if (showChips)
            {
                chipsEntry.Y = y;
                chipsEntry.Height = ChipH;
                y += ChipH + 32;
            }

            bool found = false, waiting = false;
            var visible = new HashSet<Entry>(Sections());
            foreach (Entry s in AllSections())
            {
                if (!visible.Contains(s)) { s.Root.Hide(); continue; }
                if (s.Loading) waiting = true;
                if (s.Tiles.Count > 0) found = true;
                if (!s.Loading && !s.Failed && s.Tiles.Count == 0) { s.Root.Hide(); continue; }
                s.Y = y;
                s.Root.PositionY = y;
                s.Root.Show();
                y += s.Height;
            }
            bool failed = false;
            foreach (Entry s in Results()) if (s.Failed) failed = true;
            if (query.Length >= MinLength && !waiting && !found && !failed)
            {
                nothing.Text = Lang.T("search.nothing", query);
                nothing.Position = new Position(RightX, y);
                nothing.Show();
                y += 60;
            }
            else nothing.Hide();
            paneHeight = y + 40;

            stops.Clear();
            if (showChips) stops.Add(chipsEntry);
            foreach (Entry s in Sections()) if (s.Tiles.Count > 0 && s.Root.Visibility) stops.Add(s);
            if (focus != null && !stops.Contains(focus)) focus = null;
            if (lastRight != null && !stops.Contains(lastRight)) lastRight = null;
            Refocus();
        }

        // Pastilles des recherches récentes, refaites seulement si elles changent.
        void BuildChips(bool show)
        {
            var want = new List<string>();
            if (show) for (int i = 0; i < history.Count && i < ChipsMax; i++) want.Add(history[i]);
            string sig = show ? string.Join("\n", want) + "\n" + Lang.Current : "";
            if (sig == chipsSig) return;
            chipsSig = sig;
            foreach (Chip c in chipsEntry.chips) { c.Box.Hide(); toDrop.Enqueue(c.Box); }
            chipsEntry.chips.Clear();
            chipsEntry.Col = 0;
            if (!show) { EnsureBuilder(); return; }
            int x = RightX;
            foreach (string term in want) x = AddChip(term, "↻  " + term, x);
            AddChip(null, Lang.T("search.clear_history"), x);
            EnsureBuilder();
        }

        int AddChip(string term, string text, int x)
        {
            var c = new Chip { Term = term };
            c.Label = new TextLabel { Position = new Position(24, 0), PointSize = Theme.SizeBody, VerticalAlignment = VerticalAlignment.Center, Text = text };
            c.Box = new View { Position = new Position(x, ChipsY) }.Rounded(ChipH / 2f);
            c.Box.Add(c.Label);
            pane.Add(c.Box);
            // Largeur du texte : demandée à la TV (quelques pastilles courtes seulement).
            float w = c.Label.NaturalSize2D.Width;
            if (w <= 0) w = text.Length * 13;
            c.Label.Size = new Size(w + 2, ChipH);
            c.Box.Size = new Size(w + 50, ChipH);
            chipsEntry.chips.Add(c);
            return x + (int)w + 50 + Theme.Gap;
        }

        void PaintChips()
        {
            for (int i = 0; i < chipsEntry.chips.Count; i++)
            {
                Chip c = chipsEntry.chips[i];
                bool on = focus == chipsEntry && chipsEntry.Col == i;
                c.Box.BackgroundColor = on ? Color.White : c.Term == null ? Color.Transparent : Theme.Surface;
                c.Label.TextColor = on ? Theme.Ink : c.Term == null ? Theme.Grey : Color.White;
            }
        }

        // ---- Historique (12 termes par utilisateur, sur cette TV) ----

        static string HistoryKey => "search.history." + Session.UserId;

        static List<string> LoadHistory()
        {
            try
            {
                if (!Preference.Contains(HistoryKey)) return new List<string>();
                string raw = Preference.Get<string>(HistoryKey) ?? "";
                return new List<string>(raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
            }
            catch (Exception) { return new List<string>(); }
        }

        void SaveHistory()
        {
            Store.Set(HistoryKey, string.Join("\n", history)); // hors du fil d'interface
        }

        void AddHistory(string term)
        {
            term = (term ?? "").Trim();
            if (term.Length < MinLength) return;
            history.RemoveAll(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase));
            history.Insert(0, term);
            if (history.Count > HistoryMax) history.RemoveRange(HistoryMax, history.Count - HistoryMax);
            SaveHistory();
        }

        // ---- Touches, cartes et images, faites par petits morceaux ----

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

        bool BuildStep()
        {
            if (Closed) return false;
            // Les touches d'abord, autant que le temps le permet à chaque tour.
            if (keysBuilt < keys.Count)
            {
                long t0 = clock.ElapsedMilliseconds;
                while (keysBuilt < keys.Count && clock.ElapsedMilliseconds - t0 < KeyBudgetMs)
                    BuildKey(keys[keysBuilt++]);
                keysMs += clock.ElapsedMilliseconds - t0;
                return true;
            }
            if (toDrop.Count > 0)
            {
                View v = toDrop.Dequeue();
                v.Unparent();
                v.Dispose();
                return true;
            }
            Tile best = null;
            int bestPrio = int.MaxValue;
            foreach (Entry s in Sections())
                foreach (Tile t in s.Tiles)
                {
                    if (t.Built && t.Texted && (t.Requested || t.Url == null)) continue;
                    int p = Priority(t);
                    if (p < bestPrio) { bestPrio = p; best = t; }
                }
            if (best == null) return false;
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
                    if (p == int.MaxValue && !t.Dead) t.Requested = false;
                    return p;
                }, path => Guard("Tile", () => OnTileFile(t, path)));
            }
            return true;
        }

        // Plus petit = plus tôt. Seules les cartes visibles ou proches de l'écran sont traitées.
        int Priority(Tile t)
        {
            if (t.Dead || Closed) return int.MaxValue;
            Entry s = t.Sec;
            if (t.Index < s.First - 2 || t.Index > s.First + s.Visible + 2) return int.MaxValue;
            int rank = -1, frank = -1, i = 0;
            foreach (Entry e in Sections())
            {
                if (e == s) rank = i;
                if (e == focus) frank = i;
                i++;
            }
            if (rank < 0) return int.MaxValue;
            int ds = frank < 0 ? rank : Math.Abs(rank - frank);
            if (ds > 3) return int.MaxValue;
            return ds * 100 + Math.Abs(t.Index - s.Col);
        }

        void BuildTile(Tile t)
        {
            Entry s = t.Sec;
            t.Built = true;
            bool round = s.Kind == Kind.People;
            int w = round ? s.ImageH : s.TileW, x = round ? (s.TileW - s.ImageH) / 2 : 0;
            // Grossie autour du centre de l'image quand elle est regardée (charte) ; contour blanc
            // et liseré sombre, coins concentriques (rond pour une personne).
            t.Root = new View
            {
                Position = new Position(t.Index * s.Step, 0),
                Size = new Size(s.TileW, s.ImageH),
                PivotPoint = Tizen.NUI.PivotPoint.Center,
                PositionUsesPivotPoint = false,
                Opacity = t.Index < s.First ? 0f : 1f,
            };
            t.Ring = Theme.FocusRing(w, s.ImageH, round ? w / 2 : Theme.RadiusSmall);
            t.Ring.Position = new Position(x - Theme.RingOut, -Theme.RingOut);
            t.Frame = new View { Position = new Position(x, 0), Size = new Size(w, s.ImageH), BackgroundColor = Theme.Placeholder }.Rounded(round ? w / 2 : Theme.RadiusSmall);
            t.Ring.Hide();
            t.Root.Add(t.Ring);
            t.Root.Add(t.Frame);
            s.Track.Add(t.Root);
            PaintTile(t);
            if (focus == s && t.Index == s.Col) GrowFocused();
        }

        void TextTile(Tile t)
        {
            Entry s = t.Sec;
            Item it = t.Item;
            t.Texted = true;
            bool round = s.Kind == Kind.People;
            // Sous les affiches (charte, comme Favoris) : titre 18 pt, année 15 pt grise dessous,
            // selon le réglage du compte (module 13c) ; nom centré sous une photo.
            if (round || Prefs.PosterTitle)
                t.Root.Add(new TextLabel
                {
                    Position = new Position(0, s.ImageH + 12),
                    Size = new Size(s.TileW, TitleH),
                    PointSize = Theme.SizeBody,
                    Ellipsis = true,
                    TextColor = Color.White,
                    HorizontalAlignment = round ? HorizontalAlignment.Center : HorizontalAlignment.Begin,
                    Text = it.Name ?? "",
                });
            string caption = !round && Prefs.PosterYear ? Library.YearScore(it) : null;
            if (caption != null)
                t.Root.Add(new TextLabel
                {
                    Position = new Position(0, s.ImageH + 12 + (Prefs.PosterTitle ? TitleH : 0)),
                    Size = new Size(s.TileW, CaptionH),
                    PointSize = Theme.SizeInfo,
                    TextColor = Theme.Grey,
                    Ellipsis = true,
                    Text = caption,
                });
            if (!round && !Prefs.PosterTitle && t.Url == null) t.Frame.Add(Pictures.NoArt(it.Name, s.TileW, s.ImageH));
            if (round && t.Url == null)
                t.Frame.Add(new TextLabel { Size = new Size(s.ImageH, s.ImageH), PointSize = Theme.SizeTitle, TextColor = Theme.Grey, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = Initials(it.Name) });
            if (!round && it.UserData?.Played == true) t.Frame.Add(Check(s.TileW - 52, 12));
        }

        static View Check(int x, int y)
        {
            var b = new View { Name = CardMenu.CheckName, Position = new Position(x, y), Size = new Size(40, 32), BackgroundColor = Theme.Accent }.Rounded(16);
            b.Add(new TextLabel { Size = new Size(40, 32), PointSize = Theme.SizeSmall, TextColor = Theme.OnAccent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = "✓" });
            return b;
        }

        static string Initials(string name)
        {
            var sb = new StringBuilder();
            foreach (string part in (name ?? "").Split(' '))
                if (part.Length > 0 && sb.Length < 2) sb.Append(char.ToUpperInvariant(part[0]));
            return sb.ToString();
        }

        void OnTileFile(Tile t, string path)
        {
            if (t.Dead || Closed || path == null) return;
            Entry s = t.Sec;
            if (s.Kind == Kind.People)
            {
                ImageView c = Pictures.Circle(path, s.ImageH);
                if (c == null) return;
                t.Frame.Add(c);
                Fade(c);
                return;
            }
            View v = Pictures.Cover(path, s.TileW, s.ImageH, out ImageView img);
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
        readonly List<Animation> fades = new List<Animation>(); // gardées, sinon elles s'arrêtent

        // ---- Fiche du titre regardé ----

        string pendingId = "";

        // Le titre regardé, sinon le premier de l'écran ; une personne ne change rien.
        Item FeaturedNow(out Entry row)
        {
            row = null;
            if (focus != null && focus.Kind == Kind.Posters && focus.Col < focus.Tiles.Count) { row = focus; return focus.Tiles[focus.Col].Item; }
            if (focus != null && focus.Kind == Kind.People) return featured;
            foreach (Entry s in Sections()) if (s.Kind == Kind.Posters && s.Tiles.Count > 0) { row = s; return s.Tiles[0].Item; }
            return null;
        }

        // Le titre change : la fiche, et la rangée « même esprit » s'il vient d'une rangée
        // de résultats, suivent 0,3 s après l'arrêt du focus.
        void WantFeatured()
        {
            Item it = FeaturedNow(out Entry row);
            string id = it?.Id ?? "";
            if (id == pendingId) return;
            pendingId = id;
            bool title = row != null && (row == mixed || row == movies || row == shows);
            featureTimer?.Stop();
            featureTimer = new Timer(FeatureDelayMs);
            featureTimer.Tick += (s, e) =>
            {
                if (!Closed) Guard("Featured", () => { ShowFeatured(it); if (title) ShowSimilar(it); });
                return false;
            };
            featureTimer.Start();
        }

        void ShowFeatured(Item it)
        {
            featured = it;
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
            List<string> lands = Library.LandscapeCandidates(it, KbW);
            if (lands.Count == 0) ShowLand(null);
            else
            {
                imagesAsked++;
                ImageCache.RequestAny(lands, () => Closed || g != featureGen ? int.MaxValue : 0,
                    path => Guard("Landscape", () => { if (g == featureGen) ShowLand(path); }));
            }
            if (logo != null)
            {
                imagesAsked++;
                ImageCache.Request(logo, () => Closed || g != featureGen ? int.MaxValue : 0,
                    path => Guard("Logo", () => { if (g == featureGen) ShowLogo(path); }));
            }
        }

        // Informations et résumé, sous l'image ; le résumé et les genres manquent souvent
        // dans une liste : demandés à part (Library.Details), une fois par titre.
        void FillFiche(Item it)
        {
            ficheMeta.Text = Library.Meta(it);
            ficheText.Text = Pictures.Cut(it.Overview, 190) ?? "";
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
            int w = Fit(nw, nh, out int h);
            ImageView logo = Pictures.Decoded(path, w, h);
            if (logo == null) { ficheTitle.Show(); return; }
            logo.Position = new Position(18, FicheH - 18 - h);
            ficheLogo = logo;
            ficheFrame.Add(logo);
            Fade(logo);
        }

        static int Fit(int nw, int nh, out int h)
        {
            float k = Math.Min(1f, Math.Min((float)LogoW / nw, (float)LogoH / nh));
            h = Math.Max(1, (int)(nh * k));
            return Math.Max(1, (int)(nw * k));
        }

        // Image en largeur de la fiche, en fondu enchaîné : la nouvelle apparaît une fois
        // prête, les précédentes s'effacent puis sont détruites.
        readonly List<View> lands = new List<View>();

        void ShowLand(string path)
        {
            ImageView img = null;
            View v = path == null ? null : Pictures.Cover(path, KbW, FicheH, out img);
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

        // ---- Focus ----

        // Repeint le focus et fait défiler la droite pour que l'arrêt regardé soit visible.
        void Refocus()
        {
            if (focus != null) lastRight = focus;
            PaintKeys();
            PaintChips();
            foreach (Entry s in AllSections())
                foreach (Tile t in s.Tiles) PaintTile(t);
            int target = scroll;
            if (focus == null || focus == chipsEntry || (stops.Count > 0 && focus == stops[0])) target = 0;
            else
            {
                int top = focus.Y, bottom = focus.Y + focus.Height - SectionGap + 8;
                if (bottom - target > 1080 - ScrollMargin) target = bottom - (1080 - ScrollMargin);
                if (top - target < ScrollMargin) target = top - ScrollMargin;
            }
            target = Math.Max(0, Math.Min(target, Math.Max(0, paneHeight - 1080)));
            if (target != scroll)
            {
                scroll = target;
                scrollAnim?.Stop();
                scrollAnim = new Animation(ScrollMs);
                scrollAnim.AnimateTo(pane, "PositionY", (float)-scroll, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                scrollAnim.Play();
            }
            if (focus != null && focus.Kind != Kind.Chips && focus.Col < focus.Tiles.Count && !focus.Tiles[focus.Col].Built)
                BuildTile(focus.Tiles[focus.Col]);
            GrowFocused();
            EnsureBuilder();
            WantFeatured();
            // Arrière-plan d'après l'image : affiche regardée (une personne n'en donne pas).
            if (focus != null && focus.Kind != Kind.Chips && focus.Col < focus.Tiles.Count) ArtLayer.Hover(focus.Tiles[focus.Col].Item);
        }

        void PaintTile(Tile t)
        {
            if (t.Ring == null) return;
            if (focus == t.Sec && t.Index == t.Sec.Col) t.Ring.Show(); else t.Ring.Hide();
        }

        // La carte regardée grossit (x1,06 en 0,2 s, charte), l'ancienne revient à sa taille ;
        // chaque animation va jusqu'au bout et part de la taille affichée.
        Tile grown;
        readonly List<Animation> grows = new List<Animation>(); // gardées, sinon elles s'arrêtent

        void GrowFocused()
        {
            Tile want = focus != null && focus.Kind != Kind.Chips && focus.Col < focus.Tiles.Count ? focus.Tiles[focus.Col] : null;
            if (want != null && (want.Root == null || want.Dead)) want = null;
            if (want == grown) return;
            var a = new Animation(Theme.Slide);
            var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
            bool any = false;
            if (grown?.Root != null && !grown.Dead) { a.AnimateTo(grown.Root, "Scale", new Vector3(1f, 1f, 1f), ease); any = true; }
            if (want != null)
            {
                want.Root.RaiseToTop();
                a.AnimateTo(want.Root, "Scale", new Vector3(Theme.Grow, Theme.Grow, 1f), ease);
                any = true;
            }
            grown = want;
            if (!any) return;
            a.Play();
            grows.Add(a);
            if (grows.Count > 4) grows.RemoveAt(0);
        }

        // La carte regardée reste visible : la rangée glisse seulement quand elle atteint un bord.
        void ScrollTrack(Entry s)
        {
            int first = s.First;
            if (s.Col < first) first = s.Col;
            if (s.Col > first + s.Visible - 1) first = s.Col - s.Visible + 1;
            first = Math.Max(0, first);
            if (first == s.First) return;
            s.First = first;
            s.Slide?.Stop();
            s.Slide = new Animation(SlideMs);
            s.Slide.AnimateTo(s.Track, "PositionX", (float)(-first * s.Step), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            // Les cartes qui passent à gauche du bord s'effacent pendant le glissement
            // (sinon elles passeraient sous le clavier, la partie gauche étant transparente).
            foreach (Tile t in s.Tiles)
            {
                if (t.Root == null) continue;
                float want = t.Index < first ? 0f : 1f;
                if (t.Root.Opacity != want) s.Slide.AnimateTo(t.Root, "Opacity", want);
            }
            s.Slide.Play();
        }

        // ---- Touches ----

        public override string SidebarSection => "search";
        public override bool ArtBackdrop => true;

        public override bool OnKey(string key)
        {
            bool done = true;
            Guard("Key " + key, () => done = cardMenu.Key(key) || (focus == null ? KeyboardKeys(key) : RightKeys(key)));
            return done;
        }

        bool KeyboardKeys(string key)
        {
            if (keys.Count == 0) return false;
            Key cur = keys[keyFocus];
            switch (key)
            {
                case "Up":
                case "Down":
                {
                    int i = KeyAbove(key == "Up" ? -1 : 1);
                    if (i < 0) { Ignored(); return true; }
                    keyFocus = i;
                    PaintKeys();
                    return true;
                }
                case "Left":
                {
                    int i = keys.FindIndex(k => k.Row == cur.Row && k.Col + k.Span == cur.Col);
                    if (i < 0) { App.Current.ToSidebar(); return true; } // bord gauche : barre latérale
                    keyFocus = i;
                    PaintKeys();
                    return true;
                }
                case "Right":
                {
                    int i = keys.FindIndex(k => k.Row == cur.Row && k.Col == cur.Col + cur.Span);
                    if (i >= 0) { keyFocus = i; PaintKeys(); return true; }
                    // Bout de la rangée : à droite, sur le dernier arrêt regardé.
                    if (stops.Count == 0) { Ignored(); return true; }
                    focus = lastRight ?? stops[0];
                    Refocus();
                    return true;
                }
                case "Return":
                case "KP_Enter":
                    Press(cur);
                    return true;
            }
            return false;
        }

        bool RightKeys(string key)
        {
            Entry s = focus;
            switch (key)
            {
                case "Up":
                case "Down":
                {
                    int i = stops.IndexOf(s) + (key == "Up" ? -1 : 1);
                    if (i < 0 || i >= stops.Count) { Ignored(); return true; }
                    focus = stops[i];
                    Refocus();
                    return true;
                }
                case "Left":
                case "Right":
                {
                    int col = s.Col + (key == "Left" ? -1 : 1);
                    if (col < 0) { focus = null; Refocus(); return true; } // retour au clavier
                    if (col >= s.Count) { Ignored(); return true; }
                    s.Col = col;
                    if (s == chipsEntry) { PaintChips(); return true; }
                    ScrollTrack(s);
                    // La carte regardée existe tout de suite (sinon le cadre n'aurait rien à entourer).
                    if (!s.Tiles[col].Built) BuildTile(s.Tiles[col]);
                    foreach (Tile t in s.Tiles) PaintTile(t);
                    GrowFocused();
                    EnsureBuilder();
                    ImageCache.Pump();
                    WantFeatured();
                    ArtLayer.Hover(s.Tiles[col].Item); // arrière-plan d'après l'image
                    return true;
                }
                case "Return":
                case "KP_Enter":
                    Ok(s);
                    return true;
            }
            return false;
        }

        void Ok(Entry s)
        {
            if (s == chipsEntry)
            {
                Chip c = s.chips[s.Col];
                if (c.Term == null) { history.Clear(); SaveHistory(); focus = null; Layout(); return; }
                typed = c.Term;
                ShowTyped();
                searchTimer?.Stop();
                AddHistory(c.Term);
                focus = null;
                Search(c.Term);
                return;
            }
            if (s.Kind == Kind.People)
            {
                Item p = s.Tiles[s.Col].Item;
                if (p?.Id == null) { Ignored(); return; }
                if (!browse.Contains(s)) AddHistory(query);
                Nav.Push(new PersonScreen(p.Id, p.Name, p.ImageTags?.Primary));
                return;
            }
            Tile t = s.Tiles[s.Col];
            cardMenu.Press(t.Item, () =>
            {
                if (!browse.Contains(s)) AddHistory(query);
                Nav.Push(ItemScreen.For(t.Item));
            }, t.Frame);
        }

        static void Ignored() => App.Current.KeyIgnored();

        // ---- Mesures ----

        string NoteText()
        {
            if (Closed) return null;
            string titles = mixed?.Time ?? ((movies?.Time ?? "-") + " / " + (shows?.Time ?? "-"));
            var before = new List<string>();
            foreach (Entry s in browse) before.Add(s.Time);
            return Lang.T("search.panel", query.Length == 0 ? "-" : query, titles, people?.Time ?? "-",
                similar?.Time ?? "-", string.Join(" / ", before), keysBuilt, keysMs,
                imagesAsked, imagesShown, ImageCache.Queued, ImageCache.Running, landShown,
                guard.SlowCount, guard.Slowest(), guard.LastError ?? Lang.T("home.none"));
        }
    }
}
