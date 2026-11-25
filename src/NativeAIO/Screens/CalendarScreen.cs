// Calendrier (module 11), repris de pages/calendar.tsx de la version web, disposition validée
// par l'auteur :
// - en haut, le mois et trois boutons : mois précédent, mois suivant, Aujourd'hui ;
// - à gauche, la grille du mois (7 colonnes, semaine du lundi en français, du dimanche en
//   anglais) : numéro du jour, image du premier titre en fond assombri, noms des titres ;
// - à droite, la liste du jour regardé, changée 0,3 s après l'arrêt du focus. OK sur un jour,
//   ou Droite sur la dernière colonne, y entre ; OK sur un titre ouvre sa page.
// Titres : épisodes et films de la bibliothèque qui sortent pendant le mois (useCalendar du
// web : /Items avec MinPremiereDate et MaxPremiereDate, un jour de marge de chaque côté).
// La liste chronologique (« agenda ») du web n'est pas reprise (PERIMETRE.md).
// Fluidité : les cases (42 au plus) sont créées une fois, au tour de boucle suivant, et gardées
// d'un mois à l'autre ; leur contenu est refait quelques cases par tour, près du focus d'abord.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Screens
{
    class CalendarScreen : Screen
    {
        const int X0 = Theme.Margin, TitleY = 56, BtnY = Theme.Top, BtnH = Theme.ButtonHeight, DaysY = 136, GridY = 176;
        const int CellW = 164, CellH = 132, CellGap = 8, Cols = 7, MaxWeeks = 6;
        const int RX = X0 + Cols * (CellW + CellGap) - CellGap + 48, RW = 1824 - RX;
        const int DayTitleY = 120, ListY = 186, LineH = 116, BoxH = LineH - 12, ThumbW = 176, ThumbH = 99, ListBottom = 1040;
        const int ShowDelayMs = 300, CellsPerTick = 3, MaxLines = 40, ScrollMs = Theme.Slide;
        const string Muted = "#9CA3AF";

        // Couleurs de la charte (Ui/Theme.cs) ; fond des cases et des lignes : celui du thème.
        Cell grown; // case grossie (charte : ×1,06 sur la case regardée)
        Animation growAnim; // gardée ici, sinon elle s'arrête

        enum Zone { Header, Grid, List }

        // Une case de la grille, gardée d'un mois à l'autre (seuls son jour et son contenu changent).
        class Cell
        {
            public int Index;
            public DateTime Day;
            public View Root, Ring, Frame, Dot, Content;
            public TextLabel Number;
            public ImageView Image;
            public DateTime Numbered; // jour déjà écrit dans Number
            public bool Dirty;
            public int Gen; // change à chaque remplissage : les images d'avant sont ignorées
        }

        // Une ligne de la liste du jour.
        class Line
        {
            public Item Item;
            public View Root, Ring, Frame;
            public Spoilers.Veil Veil; // flou des épisodes pas commencés (Ui/Spoilers.cs)
        }

        View root, listView;
        TextLabel monthTitle, dayTitle, note;
        readonly View[] buttons = new View[3];
        readonly TextLabel[] buttonLabels = new TextLabel[3];
        readonly TextLabel[] weekDays = new TextLabel[Cols];
        readonly Cell[] cells = new Cell[Cols * MaxWeeks];
        readonly List<Line> lines = new List<Line>();
        Guarded guard;

        // Mois affiché et données.
        DateTime month;              // premier jour du mois, heure locale
        int weeks, weekStart, monthGen, fillGen, dayGen;
        readonly Dictionary<string, List<Item>> monthCache = new Dictionary<string, List<Item>>();
        Dictionary<DateTime, List<Item>> byDay; // null pendant la demande
        bool failed;

        // Focus.
        Zone zone = Zone.Grid;
        int focus, headerFocus, listFocus, listScroll;
        Cell shownCell; // jour dont la liste est affichée
        Timer dayTimer, builder; // gardés dans des champs, sinon ils s'arrêtent
        Animation scrollAnim;
        readonly List<Animation> fades = new List<Animation>();

        // Mesures (panneau, touche rouge ; journal de la sonde, catégorie calendar).
        readonly Stopwatch clock = Stopwatch.StartNew();
        string monthTime = "-";
        int monthItems, cellsMade, imagesAsked, imagesShown, loads;

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.ScreenBack(Theme.Background) };
            guard = new Guarded(root);
            monthTitle = new TextLabel { Position = new Position(X0, TitleY), Size = new Size(420, 64), PointSize = Theme.SizeTitle, TextColor = Color.White, VerticalAlignment = VerticalAlignment.Center };
            root.Add(monthTitle);
            int x = X0 + 440;
            int[] widths = { 64, 64, 220 };
            for (int i = 0; i < 3; i++)
            {
                buttons[i] = new View { Position = new Position(x, BtnY), Size = new Size(widths[i], BtnH) }.Rounded(BtnH / 2);
                buttonLabels[i] = new TextLabel { Size = new Size(widths[i], BtnH), PointSize = Theme.SizeBody, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                buttons[i].Add(buttonLabels[i]);
                root.Add(buttons[i]);
                x += widths[i] + Theme.Gap;
            }
            buttonLabels[0].Text = "‹";
            buttonLabels[1].Text = "›";
            for (int c = 0; c < Cols; c++)
            {
                weekDays[c] = new TextLabel
                {
                    Position = new Position(X0 + c * (CellW + CellGap), DaysY),
                    Size = new Size(CellW, 32),
                    PointSize = Theme.SizeSmall,
                    TextColor = Theme.Grey,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                root.Add(weekDays[c]);
            }
            dayTitle = new TextLabel { Position = new Position(RX, DayTitleY), Size = new Size(RW, 48), PointSize = Theme.SizeHeading, TextColor = Color.White, Ellipsis = true };
            root.Add(dayTitle);
            listView = new View();
            root.Add(listView);
            note = new TextLabel { Position = new Position(RX, ListY), Size = new Size(RW, 160), PointSize = Theme.SizeBody, MultiLine = true, TextColor = Theme.Grey };
            root.Add(note);
            month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            return root; // grille faite par ApplyTexts, appelée juste après
        }

        public override void ApplyTexts()
        {
            weekStart = int.TryParse(Lang.T("calendar.week_start"), out int ws) ? ws % 7 : 1;
            buttonLabels[2].Text = Lang.T("calendar.today");
            // Ouverture sur aujourd'hui ; après un changement de langue, le premier jour de la
            // semaine peut changer : grille refaite sur le même jour (titres déjà reçus gardés).
            DateTime keep = weeks > 0 ? cells[focus].Day : DateTime.Today;
            SetMonth(month, keep);
        }

        public override void OnShow() => Measure.Note = NoteText;
        public override void OnHide() { Measure.Note = null; }

        public override void OnClose()
        {
            builder?.Stop();
            dayTimer?.Stop();
            guard.Stop();
            Measure.Note = null;
        }

        void Guard(string where, Action run) => guard.Run(where, run);

        static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        static string Part(string key, int i)
        {
            string[] a = Lang.T(key).Split(',');
            return i >= 0 && i < a.Length ? a[i].Trim() : "";
        }

        static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

        static string MonthKey(DateTime d) => d.Year + "-" + d.Month.ToString("00");

        // « Vendredi 13 novembre ».
        static string LongDay(DateTime d) =>
            Capital(Lang.T("calendar.day", Part("date.days", (int)d.DayOfWeek), d.Day, Part("date.months", d.Month - 1)));

        // ---- Mois ----

        // Affiche le mois (premier jour) et pose le focus sur un jour de la grille.
        void SetMonth(DateTime first, DateTime focusDay)
        {
            month = first;
            monthGen++;
            int lead = ((int)first.DayOfWeek - weekStart + 7) % 7;
            int inMonth = DateTime.DaysInMonth(first.Year, first.Month);
            weeks = (lead + inMonth + Cols - 1) / Cols;
            monthTitle.Text = Capital(Lang.T("calendar.month", Part("date.months", first.Month - 1), first.Year));
            for (int c = 0; c < Cols; c++)
                weekDays[c].Text = Part("date.days_short", (c + weekStart) % 7).ToUpper();
            focus = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == null) cells[i] = new Cell { Index = i };
                Cell cell = cells[i];
                if (i >= weeks * Cols) { cell.Root?.Hide(); cell.Dirty = false; continue; }
                cell.Day = first.AddDays(i - lead);
                cell.Dirty = true;
                if (cell.Day == focusDay.Date) focus = i;
            }
            if (focusDay.Date < cells[0].Day || focusDay.Date > cells[weeks * Cols - 1].Day) focus = lead;
            shownCell = null;
            listFocus = 0;
            if (zone == Zone.List) zone = Zone.Grid;
            failed = false;
            if (monthCache.TryGetValue(MonthKey(first), out List<Item> known)) Apply(known);
            else
            {
                byDay = null;
                LoadMonth(first, monthGen);
            }
            Refocus();
        }

        // Le mois et les jours visibles des mois voisins, plus un jour de marge de chaque côté
        // pour les dates envoyées à minuit en temps universel (comme le web).
        async void LoadMonth(DateTime first, int g)
        {
            DateTime from = cells[0].Day.AddDays(-1), to = cells[weeks * Cols - 1].Day.AddDays(2);
            long t0 = clock.ElapsedMilliseconds;
            loads++;
            ItemsResult r;
            try { r = await Library.Calendar(from, to); }
            catch (Exception e)
            {
                if (Closed || g != monthGen) return;
                Probe.Log("calendar", "échec " + MonthKey(first) + " : " + e.Message);
                Guard("Calendar", () => { failed = true; ShowDay(); });
                return;
            }
            if (Closed || g != monthGen) return;
            monthTime = ((clock.ElapsedMilliseconds - t0) / 1000.0).ToString("0.0");
            var list = new List<Item>(r?.Items ?? new Item[0]);
            monthCache[MonthKey(first)] = list;
            Probe.Log("calendar", $"{MonthKey(first)} : {list.Count} titres en {monthTime} s");
            Guard("Calendar", () => Apply(list));
        }

        // Titres rangés par jour de sortie (à l'heure de la TV), cases à refaire.
        void Apply(List<Item> list)
        {
            byDay = new Dictionary<DateTime, List<Item>>();
            monthItems = 0;
            foreach (Item it in list)
            {
                DateTime? day = Library.AirDay(it.PremiereDate);
                if (day == null) continue;
                if (!byDay.TryGetValue(day.Value.Date, out List<Item> l)) byDay[day.Value.Date] = l = new List<Item>();
                l.Add(it);
                if (day.Value.Month == month.Month && day.Value.Year == month.Year) monthItems++;
            }
            for (int i = 0; i < weeks * Cols; i++) cells[i].Dirty = true;
            shownCell = null;
            EnsureBuilder();
            WantDay(true);
        }

        List<Item> ItemsOf(Cell c) => byDay != null && byDay.TryGetValue(c.Day, out List<Item> l) ? l : null;

        // ---- Cases ----

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

        // Quelques cases par tour, les plus proches du focus d'abord.
        bool BuildStep()
        {
            if (Closed) return false;
            for (int n = 0; n < CellsPerTick; n++)
            {
                Cell best = null;
                int bestD = int.MaxValue;
                for (int i = 0; i < weeks * Cols; i++)
                {
                    if (!cells[i].Dirty) continue;
                    int d = Math.Abs(i / Cols - focus / Cols) * Cols + Math.Abs(i % Cols - focus % Cols);
                    if (d < bestD) { bestD = d; best = cells[i]; }
                }
                if (best == null) return false;
                Fill(best);
            }
            return true;
        }

        void MakeCell(Cell c)
        {
            int i = c.Index;
            // Grossie autour de son centre sans changer de place (voir PosterGrid).
            c.Root = new View
            {
                Position = new Position(X0 + i % Cols * (CellW + CellGap), GridY + i / Cols * (CellH + CellGap)),
                Size = new Size(CellW, CellH),
                PivotPoint = Tizen.NUI.PivotPoint.Center,
                PositionUsesPivotPoint = false,
            };
            c.Ring = Theme.FocusRing(CellW, CellH, Theme.RadiusLarge);
            c.Ring.Position = new Position(-Theme.RingOut, -Theme.RingOut);
            c.Ring.Hide();
            c.Frame = new View { Size = new Size(CellW, CellH), BackgroundColor = Theme.Background }.Rounded(Theme.RadiusLarge);
            c.Frame.Add(new View { Size = new Size(CellW, CellH), BackgroundColor = Theme.Faint }.Rounded(Theme.RadiusLarge));
            c.Dot = new View { Position = new Position(8, 8), Size = new Size(36, 36), BackgroundColor = Theme.Accent }.Rounded(18);
            c.Dot.Hide();
            c.Number = new TextLabel
            {
                Position = new Position(8, 8),
                Size = new Size(36, 36),
                PointSize = Theme.SizeInfo,
                TextColor = Color.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            c.Root.Add(c.Ring);
            c.Root.Add(c.Frame);
            c.Frame.Add(c.Dot);
            c.Frame.Add(c.Number);
            root.Add(c.Root);
            cellsMade++;
        }

        // Numéro du jour, image du premier titre, noms des titres : une ligne par série (« ×6 »
        // pour six épisodes le même jour), deux au plus, puis « +n autres ». Une seule série :
        // son nom puis le code de l'épisode (ou le nombre d'épisodes).
        void Fill(Cell c)
        {
            c.Dirty = false;
            if (c.Root == null) MakeCell(c);
            c.Root.Show();
            if (c.Numbered != c.Day)
            {
                c.Number.Text = c.Day.Day.ToString();
                c.Numbered = c.Day;
            }
            bool today = c.Day == DateTime.Today;
            if (today) c.Dot.Show(); else c.Dot.Hide();
            c.Number.TextColor = today ? Theme.OnAccent : Color.White; // sombre sur un accent très clair
            if (c.Content != null) { c.Content.Unparent(); c.Content.Dispose(); c.Content = null; }
            c.Image = null;
            c.Gen = ++fillGen;
            List<Item> items = ItemsOf(c);
            if (items != null && items.Count > 0)
            {
                c.Content = new View { Size = new Size(CellW, CellH) };
                c.Content.Add(Gradient.Make(new Position(0, 0), new Size(CellW, CellH), false, t => t < 0.3f ? 0f : Math.Min(0.95f, (t - 0.3f) / 0.6f), 0.03f, Theme.RadiusLarge));
                c.Content.Add(new TextLabel
                {
                    Position = new Position(8, CellH - 66),
                    Size = new Size(CellW - 14, 62),
                    PointSize = 12,
                    MultiLine = true,
                    EnableMarkup = true,
                    TextColor = Color.White,
                    Text = CellText(items),
                });
                c.Frame.Add(c.Content);
                c.Dot.RaiseToTop();
                c.Number.RaiseToTop();
                RequestCellImage(c, items[0]);
            }
            Paint(c);
        }

        static string CellText(List<Item> items)
        {
            // Groupes dans l'ordre de sortie : une série, ou un film.
            var keys = new List<string>();
            var groups = new Dictionary<string, List<Item>>();
            foreach (Item it in items)
            {
                string k = it.Type == "Episode" && it.SeriesId != null ? it.SeriesId : it.Id ?? "";
                if (!groups.TryGetValue(k, out List<Item> g)) { groups[k] = g = new List<Item>(); keys.Add(k); }
                g.Add(it);
            }
            if (keys.Count == 1)
            {
                List<Item> g = groups[keys[0]];
                Item it = g[0];
                string second = g.Count > 1 ? Lang.T("calendar.episodes", g.Count) : Library.Code(it) ?? it.ProductionYear?.ToString();
                return Name(it, 16) + (second != null ? "\n<color value='" + Muted + "'>" + Esc(second) + "</color>" : "");
            }
            var parts = new List<string>();
            for (int i = 0; i < keys.Count && i < 2; i++)
            {
                List<Item> g = groups[keys[i]];
                parts.Add(Name(g[0], g.Count > 1 ? 12 : 16) + (g.Count > 1 ? " <color value='" + Muted + "'>×" + g.Count + "</color>" : ""));
            }
            if (keys.Count > 2) parts.Add("<color value='" + Muted + "'>" + Esc(Lang.T("calendar.more", keys.Count - 2)) + "</color>");
            return string.Join("\n", parts);
        }

        // Nom d'un titre, grisé s'il est déjà vu.
        static string Name(Item it, int len)
        {
            string n = Esc(Pictures.Cut(Library.Title(it), len));
            return it.UserData?.Played == true ? "<color value='" + Muted + "'>" + n + "</color>" : n;
        }

        void RequestCellImage(Cell c, Item it)
        {
            List<string> urls = Library.LandscapeCandidates(it, 2 * CellW);
            if (urls.Count == 0) return;
            int g = c.Gen;
            imagesAsked++;
            ImageCache.RequestAny(urls, () => Closed || c.Gen != g ? int.MaxValue : 10 + Math.Abs(c.Index - focus),
                path => Guard("CellImage", () => OnCellFile(c, g, path, it)));
        }

        void OnCellFile(Cell c, int g, string path, Item it, bool veiled = false)
        {
            if (Closed || path == null || c.Gen != g || c.Content == null) return;
            // Flou des épisodes pas commencés : la case montre la copie floue de l'image de
            // l'épisode, même regardée (le web la montre nette au survol ou au focus de la case ;
            // ici, la ligne regardée dans la liste du jour la montre nette). Échec : pas d'image.
            if (!veiled && it.ImageTags?.Primary != null && Spoilers.Hidden(it))
            {
                Spoilers.Request(path, file => Guard("CellVeil", () => OnCellFile(c, g, file, it, true)));
                return;
            }
            View v = Pictures.Cover(path, CellW, CellH, out ImageView img);
            if (v == null) return;
            img.Rounded(Theme.RadiusLarge);
            c.Image = img;
            c.Content.Add(v);
            v.LowerToBottom();
            Fade(img, () => ImageOpacity(c));
        }

        // Image en fondu une fois prête (voir ItemScreen.Fade), jusqu'à l'opacité voulue.
        void Fade(ImageView img, Func<float> to)
        {
            if (img.LoadingStatus == ImageView.LoadingStatusType.Ready) { imagesShown++; img.Opacity = to(); return; }
            img.Opacity = 0;
            img.ResourceReady += (s, e) =>
            {
                if (Closed) return;
                imagesShown++;
                var a = new Animation(Theme.Fade);
                a.AnimateTo(img, "Opacity", to());
                a.Play();
                fades.Add(a);
                if (fades.Count > 8) fades.RemoveAt(0);
            };
        }

        float ImageOpacity(Cell c) => c.Day == DateTime.Today || (zone != Zone.Header && c.Index == focus) ? 0.85f : 0.5f;

        // Case regardée : contour et grossissement ; jours des mois voisins pâlis.
        void Paint(Cell c)
        {
            if (c.Root == null) return;
            bool on = c.Index == focus && zone != Zone.Header;
            if (on) c.Ring.Show(); else c.Ring.Hide();
            if (on) GrowTo(c);
            else if (c == grown) GrowTo(null);
            bool outside = c.Day.Month != month.Month;
            c.Root.Opacity = outside && !on ? 0.35f : 1f;
            if (c.Image != null && c.Image.LoadingStatus == ImageView.LoadingStatusType.Ready) c.Image.Opacity = ImageOpacity(c);
        }

        // La case regardée grossit, l'ancienne revient à sa taille ; chaque animation va
        // jusqu'au bout et part de la taille affichée (comme PosterGrid).
        void GrowTo(Cell want)
        {
            if (want == grown) return;
            var a = new Animation(Theme.Slide);
            var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
            bool any = false;
            if (grown?.Root != null) { a.AnimateTo(grown.Root, "Scale", new Vector3(1f, 1f, 1f), ease); any = true; }
            if (want?.Root != null)
            {
                want.Root.RaiseToTop();
                a.AnimateTo(want.Root, "Scale", new Vector3(Theme.Grow, Theme.Grow, 1f), ease);
                any = true;
            }
            grown = want;
            if (!any) return;
            a.Play();
            growAnim = a;
        }

        void PaintButtons()
        {
            for (int i = 0; i < 3; i++)
            {
                bool on = zone == Zone.Header && i == headerFocus;
                buttons[i].BackgroundColor = on ? Color.White : Theme.Surface;
                buttonLabels[i].TextColor = on ? Theme.Ink : Theme.Text;
            }
        }

        void Refocus()
        {
            PaintButtons();
            for (int i = 0; i < weeks * Cols; i++) Paint(cells[i]);
            PaintLines();
            EnsureBuilder();
            ImageCache.Pump();
            WantDay(false);
        }

        // ---- Liste du jour ----

        // La liste suit le jour regardé, 0,3 s après l'arrêt du focus (now : tout de suite).
        void WantDay(bool now)
        {
            Cell c = cells[focus];
            if (c == shownCell && !now) return;
            dayTimer?.Stop();
            if (now) { ShowDay(); return; }
            dayTimer = new Timer(ShowDelayMs);
            dayTimer.Tick += (s, e) => { if (!Closed) Guard("Day", ShowDay); return false; };
            dayTimer.Start();
        }

        void ShowDay()
        {
            dayTimer?.Stop();
            Cell c = cells[focus];
            shownCell = c;
            dayGen++;
            foreach (Line l in lines) { l.Root.Unparent(); l.Root.Dispose(); }
            lines.Clear();
            listFocus = 0;
            SetScroll(0, false);
            dayTitle.Text = LongDay(c.Day);
            List<Item> items = ItemsOf(c);
            if (failed) note.Text = Lang.T("calendar.failed");
            else if (byDay == null) note.Text = Lang.T("calendar.loading");
            else if (items == null || items.Count == 0)
                note.Text = monthItems == 0 ? Lang.T("calendar.empty_month") : Lang.T("calendar.empty_day");
            else note.Text = "";
            if (items != null)
                for (int i = 0; i < items.Count && i < MaxLines; i++) lines.Add(MakeLine(items[i], i));
            if (zone == Zone.List && lines.Count == 0) zone = Zone.Grid;
            PaintLines();
            Paint(c);
        }

        Line MakeLine(Item it, int i)
        {
            var l = new Line { Item = it };
            l.Root = new View { Position = new Position(RX, ListY + i * LineH), Size = new Size(RW, BoxH) };
            l.Ring = Theme.FocusRing(RW, BoxH, Theme.RadiusLarge);
            l.Ring.Position = new Position(-Theme.RingOut, -Theme.RingOut);
            l.Ring.Hide();
            var box = new View { Size = new Size(RW, BoxH), BackgroundColor = Theme.Background }.Rounded(Theme.RadiusLarge);
            l.Frame = new View { Position = new Position(0, (BoxH - ThumbH) / 2), Size = new Size(ThumbW, ThumbH), BackgroundColor = Theme.Placeholder }.Rounded(Theme.RadiusSmall);
            bool played = it.UserData?.Played == true;
            int tx = ThumbW + 18, tw = RW - tx - 8;
            box.Add(l.Frame);
            int gen = dayGen;
            l.Veil = new Spoilers.Veil(l.Frame, ThumbW, ThumbH, Theme.RadiusSmall, Spoilers.Hidden(it), () => Closed || gen != dayGen, Guard);
            box.Add(new TextLabel
            {
                Position = new Position(tx, 8),
                Size = new Size(tw, 34),
                PointSize = Theme.SizeBody,
                Ellipsis = true,
                TextColor = played ? Theme.Grey : Color.White,
                Text = Library.Title(it),
            });
            var second = new TextLabel
            {
                Position = new Position(tx, 44),
                Size = new Size(tw, 56),
                PointSize = Theme.SizeInfo,
                MultiLine = true,
                TextColor = Theme.Grey,
                Text = LineText(it, true),
            };
            box.Add(second);
            l.Veil.Texts = shown => second.Text = LineText(it, shown);
            l.Root.Add(l.Ring);
            l.Root.Add(box);
            listView.Add(l.Root);
            List<string> urls = Library.LandscapeCandidates(it, 2 * ThumbW);
            if (urls.Count > 0)
            {
                int g = dayGen;
                imagesAsked++;
                ImageCache.RequestAny(urls, () => Closed || g != dayGen ? int.MaxValue : i,
                    path => Guard("LineImage", () =>
                    {
                        if (Closed || path == null || g != dayGen) return;
                        View v = Pictures.Cover(path, ThumbW, ThumbH, out ImageView img);
                        if (v == null) return;
                        img.Rounded(Theme.RadiusSmall);
                        l.Frame.Add(v);
                        if (it.ImageTags?.Primary != null && l.Veil.Take(img, path)) { imagesShown++; return; }
                        Fade(img, () => 1f);
                    }));
            }
            return l;
        }

        // « S02E09 · Nervous Breakdown », puis l'heure de sortie et « Vu ». Épisode caché (flou
        // des épisodes pas commencés, hors sélection) : « S02E09 » seul.
        static string LineText(Item it, bool shown)
        {
            string first;
            if (it.Type == "Episode")
            {
                string code = Library.Code(it);
                first = !shown ? code ?? "" : code != null && !string.IsNullOrEmpty(it.Name) ? code + " · " + it.Name : code ?? it.Name ?? "";
            }
            else first = Lang.T("calendar.movie") + (Library.YearScore(it) is string ys ? " · " + ys : "");
            var second = new List<string>();
            string time = Library.AirTime(it.PremiereDate);
            if (time != null) second.Add(time);
            if (it.UserData?.Played == true) second.Add(Lang.T("item.watched"));
            return second.Count > 0 ? first + "\n" + string.Join(" · ", second) : first;
        }

        void PaintLines()
        {
            for (int i = 0; i < lines.Count; i++)
            {
                bool on = zone == Zone.List && i == listFocus;
                if (on) lines[i].Ring.Show();
                else lines[i].Ring.Hide();
                lines[i].Veil?.Paint(on);
            }
            // La ligne regardée reste entière à l'écran.
            int visible = (ListBottom - ListY) / LineH;
            int target = listScroll;
            if (listFocus < target) target = listFocus;
            if (listFocus >= target + visible) target = listFocus - visible + 1;
            SetScroll(Math.Max(0, target), true);
            // Les lignes qui sortent par le haut passeraient sous le titre du jour.
            for (int i = 0; i < lines.Count; i++)
                if (i < listScroll) lines[i].Root.Hide(); else lines[i].Root.Show();
        }

        void SetScroll(int first, bool animate)
        {
            if (first == listScroll && animate) return;
            listScroll = first;
            scrollAnim?.Stop();
            if (!animate) { listView.PositionY = -first * LineH; return; }
            scrollAnim = new Animation(ScrollMs);
            scrollAnim.AnimateTo(listView, "PositionY", (float)(-first * LineH), new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            scrollAnim.Play();
        }

        // ---- Touches ----

        public override string SidebarSection => "calendar";
        public override bool ArtBackdrop => true;

        public override bool OnKey(string key)
        {
            bool done = true;
            Guard("Key " + key, () => done = zone == Zone.Header ? HeaderKeys(key) : zone == Zone.Grid ? GridKeys(key) : ListKeys(key));
            return done;
        }

        bool HeaderKeys(string key)
        {
            switch (key)
            {
                case "Left": if (headerFocus > 0) headerFocus--; else App.Current.ToSidebar(); PaintButtons(); return true;
                case "Right": if (headerFocus < 2) headerFocus++; else Ignored(); PaintButtons(); return true;
                case "Down": zone = Zone.Grid; Refocus(); return true;
                case "Up": Ignored(); return true;
                case "Return":
                case "KP_Enter":
                    if (headerFocus == 2)
                    {
                        DateTime today = DateTime.Today;
                        zone = Zone.Grid;
                        SetMonth(new DateTime(today.Year, today.Month, 1), today);
                    }
                    else
                    {
                        // Focus gardé sur le bouton ; la grille s'ouvrira sur le 1er du mois.
                        DateTime next = month.AddMonths(headerFocus == 0 ? -1 : 1);
                        SetMonth(next, next);
                    }
                    return true;
            }
            return false;
        }

        bool GridKeys(string key)
        {
            int col = focus % Cols, row = focus / Cols;
            switch (key)
            {
                case "Left":
                    if (col == 0) { App.Current.ToSidebar(); return true; } // bord gauche : barre latérale
                    focus--;
                    break;
                case "Right":
                    if (col == Cols - 1) { EnterList(); return true; }
                    focus++;
                    break;
                case "Up":
                    if (row == 0) { zone = Zone.Header; Refocus(); return true; }
                    focus -= Cols;
                    break;
                case "Down":
                    if (row == weeks - 1) { Ignored(); return true; }
                    focus += Cols;
                    break;
                case "Return":
                case "KP_Enter":
                    if (failed) { SetMonth(month, cells[focus].Day); return true; }
                    EnterList();
                    return true;
                default:
                    return false;
            }
            Refocus();
            return true;
        }

        void EnterList()
        {
            if (shownCell != cells[focus]) ShowDay();
            if (lines.Count == 0) { Ignored(); return; }
            zone = Zone.List;
            listFocus = 0;
            Refocus();
        }

        bool ListKeys(string key)
        {
            switch (key)
            {
                case "Up": if (listFocus > 0) listFocus--; else Ignored(); PaintLines(); return true;
                case "Down": if (listFocus < lines.Count - 1) listFocus++; else Ignored(); PaintLines(); return true;
                case "Left": zone = Zone.Grid; Refocus(); return true;
                case "Right": Ignored(); return true;
                case "Return":
                case "KP_Enter":
                    if (listFocus < lines.Count) Nav.Push(ItemScreen.For(lines[listFocus].Item));
                    return true;
            }
            return false;
        }

        public override bool OnBack()
        {
            if (zone != Zone.List) return false;
            zone = Zone.Grid;
            Guard("Back", Refocus);
            return true;
        }

        static void Ignored() => App.Current.KeyIgnored();

        // ---- Mesures ----

        string NoteText()
        {
            if (Closed) return null;
            return Lang.T("calendar.panel", MonthKey(month), monthItems, monthTime, loads, monthCache.Count,
                cellsMade, imagesAsked, imagesShown, ImageCache.Queued, ImageCache.Running,
                guard.SlowCount, guard.Slowest(), guard.LastError ?? Lang.T("home.none"));
        }
    }
}
