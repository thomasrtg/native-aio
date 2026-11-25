// Grille d'affiches partagée par Favoris, Historique et la fiche d'une personne (module 13b),
// reprise de la grille de Découvrir et posée selon la charte :
// - affiches de 192×288 arrondies à 8 px, titre (18 pt) et précision grise (15 pt) dessous ;
// - affiche regardée : contour blanc de 3 px séparé par un liseré sombre, grossie ×1,06 en
//   0,2 s autour de son centre (animation de la TV) ;
// - pages de 60 demandées quand le focus approche de la fin ; la suite est demandée à
//   l'adresse suivante (début + 60), comme la filmographie du web : une page peut revenir
//   incomplète quand un titre n'est pas ouvrable ;
// - cartes, textes et images faits au tour de boucle suivant, près du focus ; les cartes
//   loin du focus sont détruites (LECONS.md : jamais de construction dans une touche).
// La grille fait défiler la vue qui la contient (pane) : l'en-tête de l'écran, posé dans la
// même vue, défile avec elle.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Ui
{
    class PosterGrid
    {
        // Taille moyenne : affiches de 192×288 tous les 216 px. Les autres tailles (réglage
        // « Taille des affiches », module 13c) gardent la largeur de la grille et changent le
        // nombre d'affiches par ligne ; titre et année montrés selon « Sous les affiches ».
        const int MediumStep = 216, TitleH = 34, CaptionH = 28;
        readonly int TileW, ImageH, Step, RowStep, TitleY, CaptionY, width;
        readonly bool showTitle, showYear;
        const int PageSize = 60, BuildRows = 2, KeepRows = 6, ScrollMargin = 40, ScrollMs = 300, GrowMs = 200, RefreshMax = 240;
        const float Grow = 1.06f;

        static readonly Color TileBack = new Color(0.122f, 0.129f, 0.161f, 1f); // #1F2129
        static readonly Color Back = new Color(0.039f, 0.039f, 0.039f, 1f);     // #0A0A0A
        static readonly Color Muted = new Color(0.612f, 0.639f, 0.686f, 1f);   // #9CA3AF

        class Tile
        {
            public int Index;
            public Item Item;
            public View Root, Frame;
            public string Url;
            public bool Built, Texted, Requested, Dead;
        }

        readonly View pane;
        readonly Guarded guard;
        readonly Func<bool> closed;
        readonly string name; // pour le journal de la sonde
        readonly int gx, gy, cols;
        readonly TextLabel message;

        readonly List<Tile> tiles = new List<Tile>();
        readonly HashSet<Tile> live = new HashSet<Tile>();
        readonly HashSet<string> ids = new HashSet<string>();
        readonly Queue<View> toDrop = new Queue<View>();
        readonly List<Animation> anims = new List<Animation>(); // gardées, sinon elles s'arrêtent
        Func<int, int, Task<ItemsResult>> fetch;
        Tile grown;
        Timer builder;
        Animation scrollAnim;
        int gen, offset, total, scroll;
        bool loading, failed, refreshing;

        // Deuxième ligne sous l'affiche (année, « S2 É9 · 12 nov. »…) ; coche « vu » montrée ou non.
        public Func<Item, string> Caption = Library.YearScore;
        public bool ShowPlayed = true;
        public Func<string> EmptyText, FailedText;
        // Nombre de titres, chargement ou échec changés : l'écran met son en-tête à jour.
        public Action Changed;

        public int Cur { get; private set; }
        public bool Focused { get; private set; }
        public int Count => tiles.Count;
        public int Total => total;
        public bool Loading => loading;
        public bool Failed => failed;
        public Item Current => Cur < tiles.Count ? tiles[Cur].Item : null;
        // Image de la carte regardée (place du menu d'une carte).
        public View CurrentFrame => Cur < tiles.Count ? tiles[Cur].Frame : null;

        // Coche « vu » de la carte de ce titre remise à jour (menu d'une carte).
        public void Mark(Item it)
        {
            if (!ShowPlayed) return;
            foreach (Tile t in tiles)
                if (t.Item == it && t.Frame != null) CardMenu.SetCheck(t.Frame, it.UserData?.Played == true, () => Check(TileW - 52, 12));
        }
        public int Built => live.Count;

        public PosterGrid(View pane, Guarded guard, Func<bool> closed, string name, int x, int y, int cols)
        {
            this.pane = pane;
            this.guard = guard;
            this.closed = closed;
            this.name = name;
            gx = x;
            gy = y;
            // cols : affiches par ligne à la taille moyenne.
            width = cols * MediumStep;
            this.cols = Prefs.GridCols(cols);
            Step = width / this.cols;
            TileW = Step - (MediumStep - 192);
            ImageH = TileW * 3 / 2;
            showTitle = Prefs.PosterTitle;
            showYear = Prefs.PosterYear;
            TitleY = ImageH + 12;
            CaptionY = TitleY + (showTitle ? 36 : 0);
            RowStep = ImageH + (showTitle || showYear ? 12 : 0) + (showTitle ? 36 : 0) + (showYear ? 28 : 0) + 24;
            message = new TextLabel { Position = new Position(x, y + 10), Size = new Size(width - 24, 100), PointSize = 18, MultiLine = true, TextColor = Muted };
            message.Hide();
            pane.Add(message);
        }

        bool Closed => closed();

        void Guard(string where, Action run) => guard.Run(where, run);

        // ---- Pages ----

        // Vide la grille et demande la première page de cette source.
        public void Load(Func<int, int, Task<ItemsResult>> source)
        {
            fetch = source;
            Clear();
            LoadPage();
        }

        void Clear()
        {
            gen++;
            foreach (Tile t in tiles) { Drop(t); t.Dead = true; }
            tiles.Clear();
            ids.Clear();
            offset = total = 0;
            Cur = 0;
            failed = false;
            refreshing = false;
            SetScroll(0, false);
        }

        async void LoadPage()
        {
            if (fetch == null || loading) return;
            int g = gen, start = offset;
            loading = true;
            failed = false;
            ShowState();
            long t0 = Environment.TickCount;
            ItemsResult r;
            try { r = await fetch(start, PageSize); }
            catch (Exception e)
            {
                Probe.Log("grid", name + " : page " + start + " en échec, " + e.GetType().Name + " " + e.Message);
                if (Closed || g != gen) return;
                Guard("Page", () => { loading = false; failed = true; ShowState(); });
                return;
            }
            if (Closed || g != gen) return;
            Guard("Page", () =>
            {
                Item[] items = r?.Items ?? new Item[0];
                total = r?.TotalRecordCount ?? 0;
                offset = start + PageSize;
                Probe.Log("grid", name + " : " + items.Length + " titres dès " + start + " sur " + total + ", " + (Environment.TickCount - t0) + " ms");
                Append(items);
                // Une page vide sans total annoncé met fin à la suite.
                if (items.Length == 0 && total <= start) total = tiles.Count;
                loading = false;
                ShowState();
                EnsureBuilder();
                MaybeMore();
            });
        }

        void Append(Item[] items)
        {
            foreach (Item it in items)
            {
                if (it?.Id == null || !ids.Add(it.Id)) continue; // sans doublon
                tiles.Add(new Tile { Index = tiles.Count, Item = it, Url = Library.Poster(it, TileW) });
            }
        }

        bool HasMore => !loading && !failed && fetch != null && offset < total;

        // La suite, quand le focus approche de la dernière ligne chargée.
        void MaybeMore()
        {
            if (!HasMore) return;
            int lastRow = Math.Max(0, tiles.Count - 1) / cols;
            if (Cur / cols >= lastRow - 2) LoadPage();
        }

        public void Retry()
        {
            if (failed) LoadPage();
        }

        // Retour sur l'écran (après la page d'un titre, une lecture) : le début de la liste
        // est redemandé ; s'il a changé (favori retiré, titre vu), la grille est refaite, le
        // focus sur le même titre s'il y est encore.
        public async void Refresh()
        {
            if (fetch == null || loading || refreshing || tiles.Count == 0) return;
            int g = gen, n = Math.Min(RefreshMax, Math.Max(PageSize, tiles.Count));
            refreshing = true;
            ItemsResult r;
            try { r = await fetch(0, n); }
            catch (Exception e)
            {
                Probe.Log("grid", name + " : actualisation en échec, " + e.GetType().Name);
                refreshing = false;
                return;
            }
            if (Closed || g != gen) return;
            refreshing = false;
            Guard("Refresh", () =>
            {
                Item[] items = r?.Items ?? new Item[0];
                int newTotal = r?.TotalRecordCount ?? 0;
                bool same = newTotal == total && items.Length <= tiles.Count;
                for (int i = 0; same && i < items.Length; i++) same = items[i]?.Id == tiles[i].Item.Id;
                if (same) { Probe.Log("grid", name + " : retour, rien de changé"); return; }
                string keep = Current?.Id;
                int was = Cur, oldScroll = scroll;
                gen++;
                foreach (Tile t in tiles) { Drop(t); t.Dead = true; }
                tiles.Clear();
                ids.Clear();
                Append(items);
                total = newTotal;
                offset = n;
                int at = keep == null ? -1 : tiles.FindIndex(t => t.Item.Id == keep);
                Cur = tiles.Count == 0 ? 0 : at >= 0 ? at : Math.Min(was, tiles.Count - 1);
                Probe.Log("grid", name + " : retour, liste refaite (" + tiles.Count + " titres sur " + total + ")");
                scroll = oldScroll;
                ShowState();
                Refocus();
            });
        }

        void ShowState()
        {
            string text = "";
            if (failed && tiles.Count == 0) text = FailedText?.Invoke() ?? "";
            else if (loading && tiles.Count == 0) text = Lang.T("home.row_loading");
            else if (!loading && !failed && tiles.Count == 0) text = EmptyText?.Invoke() ?? "";
            message.Text = text;
            if (text.Length > 0) message.Show(); else message.Hide();
            Changed?.Invoke();
        }

        // Textes posés par la grille (langue changée).
        public void ApplyTexts() => ShowState();

        int PaneHeight => gy + (tiles.Count + cols - 1) / cols * RowStep + 40;

        // ---- Focus et touches ----

        public void Focus(bool on)
        {
            Focused = on && tiles.Count > 0;
            if (Focused) Refocus();
            else { PaintAll(); GrowFocused(); }
        }

        // Remonte en haut (le focus va à l'en-tête de l'écran).
        public void ScrollTop() => SetScroll(0, true);

        // Faux : touche à traiter par l'écran (Haut sur la première ligne, Gauche sur la
        // première colonne). Les autres bords sont ignorés ici.
        public bool Key(string key)
        {
            int col = Cur % cols;
            switch (key)
            {
                case "Left":
                    if (col == 0) return false;
                    Cur--;
                    break;
                case "Right":
                    if (col == cols - 1 || Cur + 1 >= tiles.Count) { App.Current.KeyIgnored(); return true; }
                    Cur++;
                    break;
                case "Up":
                    if (Cur < cols) return false;
                    Cur -= cols;
                    break;
                case "Down":
                    if (Cur + cols < tiles.Count) Cur += cols;
                    else if (Cur / cols < (tiles.Count - 1) / cols) Cur = tiles.Count - 1; // dernière ligne incomplète
                    else { App.Current.KeyIgnored(); MaybeMore(); return true; }
                    break;
                default:
                    return false;
            }
            Refocus();
            return true;
        }

        // La ligne regardée reste entière à l'écran ; la première ligne remonte tout en haut
        // (en-tête visible).
        void Refocus()
        {
            PaintAll();
            int target = scroll;
            if (Cur < cols) target = 0;
            else
            {
                int top = gy + Cur / cols * RowStep - 10, bottom = top + RowStep;
                if (bottom - target > 1080 - ScrollMargin) target = bottom - (1080 - ScrollMargin);
                if (top - target < ScrollMargin) target = top - ScrollMargin;
            }
            target = Math.Max(0, Math.Min(target, Math.Max(0, PaneHeight - 1080)));
            SetScroll(target, true);
            if (Focused && Cur < tiles.Count && !tiles[Cur].Built) BuildTile(tiles[Cur]);
            GrowFocused();
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
            foreach (Tile t in live) Paint(t);
        }

        // Contour : premier enfant de la carte, caché hors focus.
        void Paint(Tile t)
        {
            if (t.Root == null) return;
            View ring = t.Root.GetChildAt(0);
            if (Focused && t.Index == Cur) ring.Show(); else ring.Hide();
        }

        // L'affiche regardée grossit, l'ancienne revient à sa taille ; chaque animation va
        // jusqu'au bout et part de la taille affichée.
        void GrowFocused()
        {
            Tile want = Focused && Cur < tiles.Count && tiles[Cur].Built ? tiles[Cur] : null;
            if (want == grown) return;
            if (want != null) ArtLayer.Hover(want.Item); // arrière-plan d'après l'image
            var a = new Animation(GrowMs);
            var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
            bool any = false;
            if (grown?.Root != null) { a.AnimateTo(grown.Root, "Scale", new Vector3(1f, 1f, 1f), ease); any = true; }
            if (want?.Root != null)
            {
                want.Root.RaiseToTop();
                a.AnimateTo(want.Root, "Scale", new Vector3(Grow, Grow, 1f), ease);
                any = true;
            }
            grown = want;
            if (!any) return;
            a.Play();
            Keep(a);
        }

        void Keep(Animation a)
        {
            anims.Add(a);
            if (anims.Count > 12) anims.RemoveAt(0);
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
            Tile best = null;
            int bestPrio = int.MaxValue, fr = Cur / cols;
            int from = Math.Max(0, (fr - 1) * cols), to = Math.Min(tiles.Count, (fr + BuildRows + 2) * cols);
            for (int i = from; i < to; i++)
            {
                Tile t = tiles[i];
                if (t.Built && t.Texted && (t.Requested || t.Url == null)) continue;
                int p = Priority(t);
                if (p < bestPrio) { bestPrio = p; best = t; }
            }
            if (best == null)
            {
                Tile far = null;
                foreach (Tile t in live) if (Math.Abs(t.Index / cols - fr) > KeepRows) { far = t; break; }
                if (far == null) return false;
                Drop(far);
                return true;
            }
            if (!best.Built) BuildTile(best);
            else if (!best.Texted) TextTile(best);
            else
            {
                best.Requested = true;
                Tile t = best;
                ImageCache.Request(t.Url, () =>
                {
                    int p = Priority(t);
                    if (p == int.MaxValue && t.Built) t.Requested = false; // redemandée au retour
                    return p;
                }, path => Guard("Tile", () => OnTileFile(t, path)));
            }
            return true;
        }

        // Plus petit = plus tôt : lignes visibles d'abord, puis une au-dessus et deux dessous.
        int Priority(Tile t)
        {
            if (Closed || t.Dead) return int.MaxValue;
            int d = t.Index / cols - Cur / cols;
            if (d < -1 || d > BuildRows + 1) return int.MaxValue;
            return Math.Abs(d) * 100 + Math.Abs(t.Index % cols - Cur % cols);
        }

        void BuildTile(Tile t)
        {
            t.Built = true;
            live.Add(t);
            t.Root = new View
            {
                Position = new Position(gx + t.Index % cols * Step, gy + t.Index / cols * RowStep),
                Size = new Size(TileW, ImageH),
                PivotPoint = Tizen.NUI.PivotPoint.Center,
                PositionUsesPivotPoint = false,
            };
            // Contour du focus (charte) : blanc de 3 px, liseré sombre de 3 px, arrondi 8 + 6.
            var ring = new View { Position = new Position(-6, -6), Size = new Size(TileW + 12, ImageH + 12), BackgroundColor = Color.White }.Rounded(14);
            ring.Add(new View { Position = new Position(3, 3), Size = new Size(TileW + 6, ImageH + 6), BackgroundColor = Back }.Rounded(11));
            t.Frame = new View { Size = new Size(TileW, ImageH), BackgroundColor = TileBack }.Rounded(8);
            t.Root.Add(ring);
            t.Root.Add(t.Frame);
            pane.Add(t.Root);
            Paint(t);
            if (t.Index == Cur) GrowFocused();
        }

        void TextTile(Tile t)
        {
            Item it = t.Item;
            t.Texted = true;
            if (showTitle)
                t.Root.Add(new TextLabel
                {
                    Position = new Position(0, TitleY),
                    Size = new Size(TileW, TitleH),
                    PointSize = 18,
                    Ellipsis = true,
                    TextColor = Color.White,
                    Text = Library.Title(it),
                });
            else if (t.Url == null) // affiche sans image : son titre reste montré (web)
                t.Frame.Add(Pictures.NoArt(Library.Title(it), TileW, ImageH));
            string caption = showYear ? Caption?.Invoke(it) : null;
            if (!string.IsNullOrEmpty(caption))
                t.Root.Add(new TextLabel
                {
                    Position = new Position(0, CaptionY),
                    Size = new Size(TileW, CaptionH),
                    PointSize = 15,
                    Ellipsis = true,
                    TextColor = Muted,
                    Text = caption,
                });
            if (ShowPlayed && it.UserData?.Played == true) t.Frame.Add(Check(TileW - 52, 12));
        }

        static View Check(int x, int y)
        {
            var b = new View { Name = CardMenu.CheckName, Position = new Position(x, y), Size = new Size(40, 32), BackgroundColor = Theme.Accent }.Rounded(16); // accent du thème (module 14)
            b.Add(new TextLabel { Size = new Size(40, 32), PointSize = Theme.SizeSmall, TextColor = Theme.OnAccent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = "✓" });
            return b;
        }

        void Drop(Tile t)
        {
            if (t.Root != null) { t.Root.Hide(); toDrop.Enqueue(t.Root); }
            live.Remove(t);
            if (t == grown) grown = null;
            t.Root = t.Frame = null;
            t.Built = t.Texted = t.Requested = false;
            EnsureBuilder();
        }

        void OnTileFile(Tile t, string path)
        {
            if (Closed || path == null || t.Frame == null || t.Dead) return;
            View v = Pictures.Cover(path, TileW, ImageH, out ImageView img);
            if (v == null) return;
            img.Rounded(8); // images arrondies au décodage (charte)
            t.Frame.Add(v);
            v.LowerToBottom();
            Fade(img);
        }

        // Image qui apparaît en fondu (0,3 s) une fois prête ; déjà prête : tout de suite.
        void Fade(ImageView img)
        {
            if (img == null || img.LoadingStatus == ImageView.LoadingStatusType.Ready) return;
            img.Opacity = 0;
            img.ResourceReady += (s, e) =>
            {
                if (Closed) return;
                var a = new Animation(300);
                a.AnimateTo(img, "Opacity", 1f);
                a.Play();
                Keep(a);
            };
        }

        public void Stop()
        {
            builder?.Stop();
            builder = null;
        }
    }
}
