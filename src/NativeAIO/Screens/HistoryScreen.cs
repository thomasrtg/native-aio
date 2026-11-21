// Historique (module 13b), présenté comme la version web (pages/history.tsx, vue par jour),
// à la demande de l'auteur : une section par jour regardé, la plus récente en haut, titrée
// « mardi 10 novembre 2026 » et suivie du temps regardé ce jour-là ; dessous, des tuiles sur
// 3 colonnes, les épisodes d'une même série vus le même jour réunis en une seule tuile
// (« 18 épisodes  S1 É1 – S2 É8 »). Un seul épisode : « S1 É2 A Little Black Dove » et
// « il y a 1 j · ✓ Vu ×4 ». Pas de nom d'utilisateur ni de menu ⋮ (marquer non vu, retirer).
// Données : films et épisodes vus, le plus récent d'abord (useOwnHistory du web) ; le
// serveur ne donne que la dernière date de chaque titre.
// Touches : pastilles Tout / Films / Séries (Gauche/Droite choisit), Bas vers les tuiles,
// flèches d'une tuile à l'autre ; OK ouvre le titre (une série : sur le dernier épisode vu
// ce jour-là). Tuiles faites au tour de boucle suivant, près du focus ; les lointaines
// sont détruites. Après une nouvelle page ou au retour sur l'écran, la disposition est
// recalculée et les tuiles inchangées gardées.
using System;
using System.Collections.Generic;
using System.Globalization;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Screens
{
    class HistoryScreen : Screen
    {
        const int X = 96, TopY = 60, TitleH = 56, PillsY = TopY + TitleH + 24, StartY = PillsY + 56 + 44;
        const int Cols = 3, CardW = 560, CardH = 160, Gap = 24, HeadH = 40, HeadGap = 16, DayGap = 40;
        const int PosterW = 96, PosterH = 144, TextX = 8 + PosterW + 24, TextW = CardW - TextX - 16;
        const int PageSize = 60, RefreshMax = 240, BuildAround = 3, KeepRows = 6, ScrollMargin = 40, ScrollMs = 300;
        const string Blue = "#73BFE6", Grey = "#9CA3AF";

        static readonly string[] Types = { "Movie,Episode", "Movie", "Episode" };
        static readonly Color Back = new Color(0.039f, 0.039f, 0.039f, 1f);
        static readonly Color Muted = new Color(0.612f, 0.639f, 0.686f, 1f);
        static readonly Color Surface = new Color(1f, 1f, 1f, 0.08f);
        static readonly Color TileBack = new Color(0.122f, 0.129f, 0.161f, 1f);

        // Une tuile : un film, un épisode, ou les épisodes d'une série vus le même jour.
        class Tile
        {
            public string Key, Sig, Url;
            public readonly List<Item> Items = new List<Item>(); // le plus récent d'abord
            public bool Show;
            public int Row, Col, X, Y;
            public View Root, Poster;
            public bool Built, Requested, Dead;
        }

        class Head
        {
            public string Sig, Title, Sub;
            public int Y;
            public View Root;
        }

        class Row
        {
            public int Y;
            public Head Head; // titre du jour, sur la première ligne du jour
            public readonly List<Tile> Tiles = new List<Tile>();
        }

        View root, pane;
        Guarded guard;
        Pills pills;
        TextLabel title, message;
        readonly List<Item> entries = new List<Item>();
        readonly HashSet<string> ids = new HashSet<string>();
        readonly List<Row> rows = new List<Row>();
        readonly HashSet<Tile> live = new HashSet<Tile>();
        readonly Queue<View> toDrop = new Queue<View>();
        Timer builder;
        Animation scrollAnim;
        int gen, offset, total, scroll, r, c, wantCol;
        bool loading, failed, refreshing, inGrid, inSidebar, touched, shown;

        public override string SidebarSection => "history";
        public override bool ArtBackdrop => true;

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.ScreenBack(Back) };
            guard = new Guarded(root);
            pane = new View { Size = new Size(1920, 1080) };
            root.Add(pane);
            title = new TextLabel { Position = new Position(X, TopY), Size = new Size(1200, TitleH), PointSize = 30, TextColor = Color.White, VerticalAlignment = VerticalAlignment.Bottom };
            pane.Add(title);
            pills = new Pills(pane, X, PillsY);
            message = new TextLabel { Position = new Position(X, StartY + 10), Size = new Size(1700, 100), PointSize = 18, MultiLine = true, TextColor = Muted };
            message.Hide();
            pane.Add(message);
            cardMenu = new CardMenu(this, Guard) { Changed = it => Refresh() };
            root.Add(cardMenu.View); // menu d'une carte, au-dessus de tout
            Load();
            return root;
        }

        public override void ApplyTexts()
        {
            title.Text = Lang.T("history.title");
            pills.SetTexts(new[] { Lang.T("discover.all"), Lang.T("discover.movies"), Lang.T("discover.shows") });
            // Textes des tuiles et des jours : refaits.
            if (rows.Count > 0) Relayout(true);
            ShowState();
            Paint();
        }

        public override void OnShow()
        {
            if (shown) Refresh(); // retour depuis un titre ou le lecteur : épisode vu en plus…
            shown = true;
        }

        // Menu d'une carte (Ui/CardMenu.cs, demande de l'auteur, 2026-11-21 ; absent du web ici).
        // Une série : le dernier épisode vu ce jour-là. Après un envoi, la liste est redemandée.
        CardMenu cardMenu;

        public override void OnHide() => cardMenu?.Hide();
        public override bool OnBack() => cardMenu.Back();
        public override bool OnKeyUp(string key) => cardMenu.KeyUp(key);

        public override void OnClose()
        {
            builder?.Stop();
            pills.Stop();
            guard.Stop();
        }

        void Guard(string where, Action run) => guard.Run(where, run);

        static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // ---- Données ----

        void Load()
        {
            gen++;
            entries.Clear();
            ids.Clear();
            offset = total = 0;
            failed = refreshing = false;
            r = c = wantCol = 0;
            SetScroll(0, false);
            Relayout(false);
            LoadPage();
        }

        async void LoadPage()
        {
            if (loading) return;
            int g = gen, start = offset;
            loading = true;
            failed = false;
            ShowState();
            long t0 = Environment.TickCount;
            ItemsResult res;
            try { res = await Library.Played(Types[pills.Chosen], start, PageSize); }
            catch (Exception e)
            {
                Probe.Log("grid", "historique : page " + start + " en échec, " + e.GetType().Name);
                if (Closed || g != gen) return;
                Guard("Page", () => { loading = false; failed = true; ShowState(); });
                return;
            }
            if (Closed || g != gen) return;
            Guard("Page", () =>
            {
                Item[] items = res?.Items ?? new Item[0];
                total = res?.TotalRecordCount ?? 0;
                offset = start + PageSize;
                foreach (Item it in items) if (it?.Id != null && ids.Add(it.Id)) entries.Add(it);
                if (items.Length == 0 && total <= start) total = entries.Count;
                loading = false;
                Relayout(true);
                Probe.Log("grid", "historique : " + items.Length + " titres dès " + start + " sur " + total + ", "
                    + (Environment.TickCount - t0) + " ms ; " + rows.Count + " lignes");
                ShowState();
                if (!touched && !inGrid && !inSidebar && rows.Count > 0) { inGrid = true; Paint(); }
                MaybeMore();
            });
        }

        void MaybeMore()
        {
            if (loading || failed || offset >= total || entries.Count == 0) return;
            if (r >= rows.Count - 3) LoadPage();
        }

        // Le début de la liste redemandé ; la disposition n'est refaite que s'il a changé.
        async void Refresh()
        {
            if (loading || refreshing || entries.Count == 0) return;
            int g = gen, n = Math.Min(RefreshMax, Math.Max(PageSize, entries.Count));
            refreshing = true;
            ItemsResult res;
            try { res = await Library.Played(Types[pills.Chosen], 0, n); }
            catch (Exception) { refreshing = false; return; }
            if (Closed || g != gen) return;
            refreshing = false;
            Guard("Refresh", () =>
            {
                Item[] items = res?.Items ?? new Item[0];
                bool same = (res?.TotalRecordCount ?? 0) == total && items.Length <= entries.Count;
                for (int i = 0; same && i < items.Length; i++)
                    same = items[i]?.Id == entries[i].Id && items[i].UserData?.PlayCount == entries[i].UserData?.PlayCount;
                if (same) { Probe.Log("grid", "historique : retour, rien de changé"); return; }
                entries.Clear();
                ids.Clear();
                foreach (Item it in items) if (it?.Id != null && ids.Add(it.Id)) entries.Add(it);
                total = res?.TotalRecordCount ?? 0;
                offset = n;
                Relayout(true);
                Probe.Log("grid", "historique : retour, liste refaite (" + entries.Count + " titres)");
                ShowState();
                if (inGrid && rows.Count == 0) inGrid = false;
                Paint();
            });
        }

        void ShowState()
        {
            string text = "";
            if (failed && entries.Count == 0) text = Lang.T("grid.failed");
            else if (loading && entries.Count == 0) text = Lang.T("home.row_loading");
            else if (!loading && !failed && entries.Count == 0) text = Lang.T("history.empty");
            message.Text = text;
            if (text.Length > 0) message.Show(); else message.Hide();
        }

        // ---- Regroupement par jour et disposition ----

        static DateTime? Watched(Item it) => Library.Date(it.UserData?.LastPlayedDate);

        // Jours (heure de la TV), tuiles de chaque jour (une série = une tuile), puis lignes de
        // 3 tuiles. Les tuiles déjà construites dont le contenu et la place n'ont pas changé
        // sont gardées ; le focus reste sur la même tuile si possible.
        void Relayout(bool keepFocus)
        {
            string focusKey = keepFocus && r < rows.Count && c < rows[r].Tiles.Count ? rows[r].Tiles[c].Key : null;
            var old = new Dictionary<string, Tile>();
            var oldHeads = new Dictionary<string, Head>();
            foreach (Row row in rows)
            {
                foreach (Tile t in row.Tiles) if (t.Built) old[t.Sig] = t;
                if (row.Head?.Root != null) oldHeads[row.Head.Sig] = row.Head;
            }
            rows.Clear();
            live.Clear();

            // Jours, dans l'ordre reçu (le plus récent d'abord), les titres sans date à la fin.
            var dayKeys = new List<string>();
            var days = new Dictionary<string, (DateTime? date, List<Tile> tiles, long ticks)>();
            foreach (Item it in entries)
            {
                DateTime? d = Watched(it);
                string dk = d == null ? "-" : d.Value.ToLocalTime().Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (!days.TryGetValue(dk, out var day))
                {
                    day = (d, new List<Tile>(), 0);
                    dayKeys.Add(dk);
                }
                bool show = it.Type == "Episode" && it.SeriesId != null;
                string key = dk + "|" + (show ? it.SeriesId : it.Id);
                Tile tile = day.tiles.Find(t => t.Key == key);
                if (tile == null) { tile = new Tile { Key = key, Show = show }; day.tiles.Add(tile); }
                tile.Items.Add(it);
                if (d != null && it.RunTimeTicks > 0) day.ticks += it.RunTimeTicks.Value;
                days[dk] = day;
            }
            if (dayKeys.Remove("-")) dayKeys.Add("-");

            int y = StartY;
            foreach (string dk in dayKeys)
            {
                var day = days[dk];
                string when = day.date == null ? Lang.T("history.undated") : Library.DayLong(day.date.Value);
                string dur = day.ticks > 0 ? Library.Duration(day.ticks) : null;
                string sub = dur != null ? Lang.T("history.watched", dur) : "";
                var head = new Head { Title = when, Sub = sub, Y = y };
                head.Sig = dk + "|" + when + "|" + sub + "|" + y;
                if (oldHeads.TryGetValue(head.Sig, out Head oh)) { head.Root = oh.Root; oldHeads.Remove(head.Sig); }
                y += HeadH + HeadGap;
                for (int i = 0; i < day.tiles.Count; i += Cols)
                {
                    var row = new Row { Y = y, Head = i == 0 ? head : null };
                    for (int k = i; k < day.tiles.Count && k < i + Cols; k++)
                    {
                        Tile t = day.tiles[k];
                        t.Row = rows.Count;
                        t.Col = k - i;
                        t.X = X + t.Col * (CardW + Gap);
                        t.Y = y;
                        t.Url = Library.Poster(t.Items[0], PosterW);
                        t.Sig = t.Key + "|" + t.Items.Count + "|" + t.Items[0].Id + "|" + t.Items[0].UserData?.PlayCount + "|" + t.X + "|" + t.Y + "|" + Lang.T("item.watched");
                        if (old.TryGetValue(t.Sig, out Tile o))
                        {
                            t.Root = o.Root;
                            t.Poster = o.Poster;
                            t.Built = true;
                            t.Requested = o.Requested && o.Poster != null && o.Poster.ChildCount > 0;
                            old.Remove(t.Sig);
                            live.Add(t);
                        }
                        row.Tiles.Add(t);
                    }
                    rows.Add(row);
                    y += CardH + Gap;
                }
                y += DayGap - Gap;
            }
            foreach (Tile o in old.Values) { o.Dead = true; if (o.Root != null) { o.Root.Hide(); toDrop.Enqueue(o.Root); } }
            foreach (Head h in oldHeads.Values) { h.Root.Hide(); toDrop.Enqueue(h.Root); }
            paneHeight = y + 40;

            // Focus : même tuile, sinon même ligne au plus près.
            int fr = -1, fc = 0;
            if (focusKey != null)
                for (int i = 0; i < rows.Count && fr < 0; i++)
                    for (int k = 0; k < rows[i].Tiles.Count; k++)
                        if (rows[i].Tiles[k].Key == focusKey) { fr = i; fc = k; break; }
            if (fr < 0) { fr = Math.Min(r, Math.Max(0, rows.Count - 1)); fc = rows.Count == 0 ? 0 : Math.Min(c, rows[fr].Tiles.Count - 1); }
            r = fr;
            c = Math.Max(0, fc);
            if (inGrid && rows.Count > 0) Refocus();
            else { PaintAll(); EnsureBuilder(); }
        }

        int paneHeight;

        // ---- Focus et touches ----

        void Paint()
        {
            pills.Focus(!inSidebar && !inGrid);
            if (!inSidebar && inGrid && rows.Count > 0) Refocus(); else PaintAll();
        }

        public override void OnSidebar(bool focused)
        {
            inSidebar = focused;
            Paint();
        }

        public override bool OnKey(string key)
        {
            bool done = true;
            Guard("Key " + key, () =>
            {
                touched = true;
                if (cardMenu.Key(key)) return;
                done = inGrid ? GridKey(key) : PillsKey(key);
            });
            return done;
        }

        bool PillsKey(string key)
        {
            switch (key)
            {
                case "Left":
                    if (pills.Move(-1)) { Load(); return true; }
                    return false; // barre latérale
                case "Right":
                    if (pills.Move(1)) Load(); else App.Current.KeyIgnored();
                    return true;
                case "Down":
                case "Return":
                case "KP_Enter":
                    if (rows.Count > 0) { inGrid = true; r = 0; c = Math.Min(wantCol, rows[0].Tiles.Count - 1); Paint(); }
                    else if (failed) LoadPage();
                    else App.Current.KeyIgnored();
                    return true;
                case "Up":
                    App.Current.KeyIgnored();
                    return true;
            }
            return false;
        }

        bool GridKey(string key)
        {
            switch (key)
            {
                case "Left":
                    if (c == 0) return false; // barre latérale
                    c--;
                    wantCol = c;
                    break;
                case "Right":
                    if (c + 1 >= rows[r].Tiles.Count) { App.Current.KeyIgnored(); return true; }
                    c++;
                    wantCol = c;
                    break;
                case "Up":
                    if (r == 0) { inGrid = false; SetScroll(0, true); Paint(); return true; }
                    r--;
                    c = Math.Min(wantCol, rows[r].Tiles.Count - 1);
                    break;
                case "Down":
                    if (r + 1 >= rows.Count) { App.Current.KeyIgnored(); MaybeMore(); return true; }
                    r++;
                    c = Math.Min(wantCol, rows[r].Tiles.Count - 1);
                    break;
                case "Return":
                case "KP_Enter":
                    // Une série : sur le dernier épisode vu ce jour-là.
                    Tile t = rows[r].Tiles[c];
                    cardMenu.Press(t.Items[0], () => Nav.Push(ItemScreen.For(t.Items[0])), t.Poster);
                    return true;
                default:
                    return false;
            }
            Refocus();
            return true;
        }

        // La ligne regardée reste entière à l'écran, avec le titre de son jour s'il la précède ;
        // la première ligne remonte tout en haut.
        void Refocus()
        {
            PaintAll();
            int target = scroll;
            if (r == 0) target = 0;
            else
            {
                Row row = rows[r];
                int top = (row.Head != null ? row.Y - HeadH - HeadGap : row.Y) - 10, bottom = row.Y + CardH;
                if (bottom - target > 1080 - ScrollMargin) target = bottom - (1080 - ScrollMargin);
                if (top - target < ScrollMargin) target = top - ScrollMargin;
            }
            target = Math.Max(0, Math.Min(target, Math.Max(0, paneHeight - 1080)));
            SetScroll(target, true);
            Tile cur = rows[r].Tiles[c];
            if (!cur.Built) BuildTile(cur);
            if (cur.Items.Count > 0) ArtLayer.Hover(cur.Items[0]); // arrière-plan d'après l'image
            PaintAll();
            EnsureBuilder();
            ImageCache.Pump();
            MaybeMore();
        }

        void SetScroll(int target, bool animate)
        {
            if (target == scroll && animate) return;
            scroll = target;
            scrollAnim?.Stop();
            if (!animate) { pane.PositionY = -scroll; return; }
            scrollAnim = new Animation(ScrollMs);
            scrollAnim.AnimateTo(pane, "PositionY", (float)-scroll, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            scrollAnim.Play();
        }

        void PaintAll()
        {
            bool on = inGrid && !inSidebar;
            foreach (Tile t in live)
            {
                View ring = t.Root.GetChildAt(0);
                if (on && t.Row == r && t.Col == c) ring.Show(); else ring.Hide();
            }
        }

        // ---- Construction, par petits morceaux ----

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
            if (toDrop.Count > 0)
            {
                View v = toDrop.Dequeue();
                v.Unparent();
                v.Dispose();
                return true;
            }
            // Le plus proche du focus d'abord : titres des jours, tuiles, puis affiches.
            Tile best = null;
            Head head = null;
            int bestPrio = int.MaxValue;
            for (int i = Math.Max(0, r - 1); i < rows.Count && i <= r + BuildAround; i++)
            {
                Row row = rows[i];
                int d = Math.Abs(i - r) * 10;
                if (row.Head != null && row.Head.Root == null && d < bestPrio) { bestPrio = d; head = row.Head; best = null; }
                foreach (Tile t in row.Tiles)
                {
                    if (t.Built && (t.Requested || t.Url == null)) continue;
                    int p = d + Math.Abs(t.Col - c) + (t.Built ? 5 : 0);
                    if (p < bestPrio) { bestPrio = p; best = t; head = null; }
                }
            }
            if (head != null) { BuildHead(head); return true; }
            if (best == null)
            {
                Tile far = null;
                foreach (Tile t in live) if (Math.Abs(t.Row - r) > KeepRows) { far = t; break; }
                if (far == null) return false;
                DropTile(far);
                return true;
            }
            if (!best.Built) { BuildTile(best); return true; }
            best.Requested = true;
            Tile tile = best;
            ImageCache.Request(tile.Url, () =>
            {
                if (Closed || tile.Dead || !tile.Built || Math.Abs(tile.Row - r) > BuildAround + 1)
                {
                    if (tile.Built) tile.Requested = false;
                    return int.MaxValue;
                }
                return Math.Abs(tile.Row - r) * 10 + Math.Abs(tile.Col - c);
            }, path => Guard("Poster", () => OnPoster(tile, path)));
            return true;
        }

        // « mardi 10 novembre 2026 » en 22 pt, puis le temps regardé en gris (15 pt), aligné
        // sur le bas des lettres.
        void BuildHead(Head h)
        {
            h.Root = new View { Position = new Position(X, h.Y), Size = new Size(1700, HeadH) };
            var t = new TextLabel { Size = new Size(1200, HeadH), PointSize = 22, TextColor = Color.White, VerticalAlignment = VerticalAlignment.Bottom, Text = h.Title };
            h.Root.Add(t);
            if (h.Sub.Length > 0)
            {
                float w = t.NaturalSize2D.Width;
                if (w <= 0) w = h.Title.Length * 15;
                h.Root.Add(new TextLabel { Position = new Position((int)w + 12, 0), Size = new Size(500, HeadH), PointSize = 15, TextColor = Muted, VerticalAlignment = VerticalAlignment.Bottom, Text = h.Sub });
            }
            pane.Add(h.Root);
        }

        // Tuile : fond léger arrondi à 12 px, petite affiche à gauche, trois lignes de texte.
        void BuildTile(Tile t)
        {
            t.Built = true;
            t.Dead = false;
            live.Add(t);
            t.Root = new View { Position = new Position(t.X, t.Y), Size = new Size(CardW, CardH) };
            // Contour du focus (charte, cartes) : blanc de 3 px, liseré sombre de 3 px.
            var ring = new View { Position = new Position(-6, -6), Size = new Size(CardW + 12, CardH + 12), CornerRadius = 18, BackgroundColor = Color.White };
            ring.Add(new View { Position = new Position(3, 3), Size = new Size(CardW + 6, CardH + 6), CornerRadius = 15, BackgroundColor = Back });
            t.Root.Add(ring);
            t.Root.Add(new View { Size = new Size(CardW, CardH), CornerRadius = 12, BackgroundColor = Surface });
            t.Poster = new View { Position = new Position(8, 8), Size = new Size(PosterW, PosterH), CornerRadius = 8, BackgroundColor = TileBack };
            t.Root.Add(t.Poster);

            Item it = t.Items[0];
            string name = t.Show ? (it.SeriesName ?? it.Name) : it.Name;
            string second, third;
            DateTime? when = Watched(it);
            string status = Status(it);
            string ago = when == null ? null : Library.Ago(when.Value);
            string tail = ago == null ? status : ago + "  ·  " + status;
            if (t.Show && t.Items.Count > 1)
            {
                // Épisodes du jour, du premier au dernier dans l'ordre de la série.
                Item first = t.Items[0], last = t.Items[0];
                foreach (Item e in t.Items)
                {
                    if (Order(e) < Order(first)) first = e;
                    if (Order(e) > Order(last)) last = e;
                }
                second = "<color value='" + Blue + "'>" + Esc(Lang.T("history.episodes", t.Items.Count)) + "</color>  <color value='" + Grey + "'>"
                    + Esc(Code(first) + " – " + Code(last)) + "</color>";
                third = null;
            }
            else if (t.Show)
            {
                second = "<color value='" + Blue + "'>" + Esc(Code(it)) + "</color>  " + Esc(it.Name);
                third = tail;
            }
            else
            {
                second = "<color value='" + Grey + "'>" + Esc(tail) + "</color>";
                third = null;
            }
            t.Root.Add(Text(name, 18, Color.White, 18, false));
            t.Root.Add(Text(second, 15, Color.White, 58, true));
            if (third != null) t.Root.Add(Text(third, 15, Muted, 94, false));
            pane.Add(t.Root);
            PaintAll();
        }

        static TextLabel Text(string text, int size, Color color, int y, bool markup) => new TextLabel
        {
            Position = new Position(TextX, y),
            Size = new Size(TextW, size == 18 ? 34 : 28),
            PointSize = size,
            Ellipsis = true,
            EnableMarkup = markup,
            TextColor = color,
            Text = text ?? "",
        };

        static long Order(Item e) => (e.ParentIndexNumber ?? 0) * 100000L + (e.IndexNumber ?? 0);

        static string Code(Item e) =>
            e.ParentIndexNumber != null && e.IndexNumber != null ? Lang.T("episode.code", e.ParentIndexNumber, e.IndexNumber) : "";

        // « ✓ Vu », « ✓ Vu ×4 ».
        static string Status(Item it)
        {
            int n = it.UserData?.PlayCount ?? 0;
            return n > 1 ? "✓ " + Lang.T("history.watched_times", n) : "✓ " + Lang.T("item.watched");
        }

        void DropTile(Tile t)
        {
            if (t.Root != null) { t.Root.Hide(); toDrop.Enqueue(t.Root); }
            live.Remove(t);
            t.Root = t.Poster = null;
            t.Built = t.Requested = false;
        }

        void OnPoster(Tile t, string path)
        {
            if (Closed || path == null || t.Dead || t.Poster == null || t.Poster.ChildCount > 0) return;
            View v = Pictures.Cover(path, PosterW, PosterH, out ImageView img);
            if (v == null) return;
            img.CornerRadius = 8;
            t.Poster.Add(v);
        }
    }
}
