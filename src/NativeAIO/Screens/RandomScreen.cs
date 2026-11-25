// Tirage au sort (demande de l'auteur, 2026-11-25, comme le web : components/random-pick.tsx) :
// « Épisode aléatoire » d'une série (menu d'une carte, bouton de la page de la série) et
// « Tirage au sort » des favoris (écran Favoris, selon la pastille choisie).
// Panneau au centre, l'écran voilé dessous. Animation du web : 14 images d'autres titres défilent
// sur la carte, chacune un peu plus longtemps que la précédente (45 ms + i² × 1,6 ms, ~2 s en
// tout), puis le titre tiré s'y pose : contour à la couleur d'accent et léger rebond (1,04).
// Une image pas encore téléchargée n'est pas montrée (la précédente reste) ; on attend celle du
// titre tiré 1,5 s au plus. Chaque image est préparée cachée dès son arrivée, pour que la TV
// n'ait plus qu'à l'afficher (essai : la TV suit-elle ce rythme ? à voir sur la TV).
// Boutons : Un autre, Ouvrir, Lecture (ou Reprendre), le focus sur Lecture une fois posé.
// Série tirée dans les favoris : Lecture lance l'épisode suivant (NextUp), sinon ouvre la série.
// Épisodes : ceux du serveur, sans les épisodes absents (Virtual) ni les hors-série (saison 0).
// Favoris : tous s'ils tiennent en 24, sinon 24 autour d'un rang tiré au sort.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Screens
{
    class RandomScreen : Screen
    {
        const int Frames = 14, Window = 24, ImageWaitMs = 1500, LandMs = 350;
        const int PW = 760, Pad = 40, BtnH = 56, Ring = 4;
        static int FrameMs(int i) => (int)(45 + i * i * 1.6);

        readonly Item series;  // épisode aléatoire de cette série
        readonly string types; // ou favoris de ces sortes (« Movie,Series », « Movie », « Series »)
        bool Posters => series == null;
        int CardW => Posters ? 240 : 640;
        const int CardH = 360;

        static readonly Random random = new Random();

        View root, panel, card, ring, placeholder;
        TextLabel heading, sub, reelTitle, title, detail, overview, message;
        readonly List<(View Box, TextLabel Label, ImageView Icon, string Key, string IconName)> buttons = new List<(View, TextLabel, ImageView, string, string)>();
        Guarded guard;

        // Tirage en cours.
        List<Item> pool;  // épisodes de la série, ou favoris s'ils tiennent en une fois
        int total = -1;   // nombre de favoris
        List<Item> items; // ceux du tirage
        int pick;
        Item target;      // posé (null pendant le défilement)
        bool rolling, failed;
        int gen;          // tirage en cours : les réponses d'un tirage abandonné sont ignorées
        int focus;
        readonly Dictionary<string, View> views = new Dictionary<string, View>(); // image prête, par adresse
        View shown;
        Timer stepTimer, waitTimer; // gardés dans des champs, sinon ils s'arrêtent
        Animation land;

        public override bool Overlay => true;
        public override bool FadeOut => true;

        RandomScreen(Item series, string types)
        {
            this.series = series;
            this.types = types;
        }

        public static void Episode(Screen owner, Item series)
        {
            if (series?.Id == null) return;
            owner.Nav.Push(new RandomScreen(series, null));
        }

        public static void Favourite(Screen owner, string types) => owner.Nav.Push(new RandomScreen(null, types));

        int PanelH => Pad + 34 + 30 + 24 + CardH + 24 + 40 + 30 + 84 + 24 + BtnH + Pad;

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.Veil };
            guard = new Guarded(root);
            int h = PanelH;
            panel = new View { Position = new Position((1920 - PW) / 2, (1080 - h) / 2), Size = new Size(PW, h), BackgroundColor = Theme.Menu }.Rounded(Theme.RadiusLarge + 8);
            root.Add(panel);
            int y = Pad;
            heading = Label(y, 34, Theme.SizeHeading, Color.White, true);
            y += 34;
            sub = Label(y, 30, Theme.SizeInfo, Theme.Grey, false);
            y += 30 + 24;
            int cx = (PW - CardW) / 2;
            ring = new View { Position = new Position(cx - Ring, y - Ring), Size = new Size(CardW + 2 * Ring, CardH + 2 * Ring), BackgroundColor = Theme.Accent }.Rounded(Theme.RadiusLarge + Ring);
            ring.Hide();
            panel.Add(ring);
            card = new View { Position = new Position(cx, y), Size = new Size(CardW, CardH), BackgroundColor = Theme.Surface }.Rounded(Theme.RadiusLarge);
            placeholder = new ImageView { ResourceUrl = Theme.IconPath("menu_shuffle"), Size = new Size(64, 64), Position = new Position((CardW - 64) / 2, (CardH - 64) / 2), Color = new Color(1, 1, 1, 0.2f) };
            card.Add(placeholder);
            panel.Add(card);
            y += CardH + 24;
            reelTitle = Label(y, 40, Theme.SizeInfo, Theme.Grey, false);
            title = Label(y, 40, Theme.SizeHeading, Color.White, true);
            y += 40;
            detail = Label(y, 30, Theme.SizeInfo, Theme.Grey, false);
            y += 30;
            overview = new TextLabel { Position = new Position(Pad, y + 4), Size = new Size(PW - 2 * Pad, 80), PointSize = Theme.SizeInfo, TextColor = Theme.Text, MultiLine = true };
            panel.Add(overview);
            message = Label(y - 70, 40, Theme.SizeBody, Theme.Text, false);
            y += 84 + 24;
            AddButton("again", "menu_shuffle", y);
            AddButton("open", "menu_open", y);
            AddButton("play", "menu_play", y);
            Roll(null);
            return root;
        }

        TextLabel Label(int y, int h, float size, Color color, bool bold)
        {
            var l = new TextLabel
            {
                Position = new Position(Pad, y), Size = new Size(PW - 2 * Pad, h), PointSize = size, TextColor = color,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Ellipsis = true,
            };
            if (bold) l.FontStyle = Theme.SemiBold();
            panel.Add(l);
            return l;
        }

        void AddButton(string key, string icon, int y)
        {
            var box = new View { PositionY = y, Size = new Size(200, BtnH) }.Rounded(BtnH / 2);
            var img = new ImageView { ResourceUrl = Theme.IconPath(icon), Size = new Size(24, 24), Position = new Position(24, (BtnH - 24) / 2) };
            var label = new TextLabel { Position = new Position(56, 0), Size = new Size(140, BtnH), PointSize = Theme.SizeBody, VerticalAlignment = VerticalAlignment.Center };
            box.Add(img);
            box.Add(label);
            panel.Add(box);
            buttons.Add((box, label, img, key, icon));
        }

        public override void ApplyTexts()
        {
            heading.Text = Lang.T(Posters ? "random.favourite" : "random.episode");
            sub.Text = Posters ? (types == "Movie" ? Lang.T("discover.movies") : types == "Series" ? Lang.T("discover.shows") : "") : series.Name ?? "";
            PaintButtons();
        }

        string ButtonText(string key) =>
            key == "again" ? Lang.T(failed ? "start.retry" : "random.again")
            : key == "open" ? Lang.T("menu.open")
            : Lang.T(target != null && target.Type != "Series" && CardMenu.ResumeTicks(target) > 0 ? "menu.resume" : "menu.play");

        bool Usable(string key) =>
            key == "again" ? !rolling && (failed || (items != null && items.Count > 1))
            : !rolling && target != null;

        // Boutons côte à côte, centrés ; ceux qu'on ne peut pas utiliser sont cachés.
        void PaintButtons()
        {
            var visible = new List<int>();
            for (int i = 0; i < buttons.Count; i++)
                if (Usable(buttons[i].Key)) visible.Add(i); else buttons[i].Box.Hide();
            if (visible.Count > 0 && !visible.Contains(focus)) focus = visible[visible.Count - 1];
            int totalW = 0;
            foreach (int i in visible)
            {
                string text = ButtonText(buttons[i].Key);
                int w = 56 + 28 + text.Length * 13;
                buttons[i].Label.Text = text;
                buttons[i].Label.SizeWidth = w - 56;
                buttons[i].Box.SizeWidth = w;
                totalW += w;
            }
            totalW += Math.Max(0, visible.Count - 1) * 16;
            int x = (PW - totalW) / 2;
            foreach (int i in visible)
            {
                var b = buttons[i];
                bool on = i == focus;
                b.Box.PositionX = x;
                x += (int)b.Box.SizeWidth + 16;
                b.Box.BackgroundColor = on ? Color.White : Theme.Surface;
                b.Label.TextColor = on ? Theme.Ink : Theme.Text;
                b.Icon.Color = on ? Theme.Ink : Color.White; // icône blanche teinte au mélange (LECONS.md)
                b.Box.Show();
            }
        }

        // ---- Tirage ----

        static List<T> Shuffled<T>(List<T> list)
        {
            var o = new List<T>(list);
            for (int i = o.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (o[i], o[j]) = (o[j], o[i]);
            }
            return o;
        }

        // N'importe lequel sauf previous, s'il y en a un autre.
        static int PickFrom(List<Item> l, string previous)
        {
            var others = new List<int>();
            for (int i = 0; i < l.Count; i++) if (l[i].Id != previous) others.Add(i);
            return others.Count > 0 ? others[random.Next(others.Count)] : 0;
        }

        async Task<(List<Item>, int)> Draw(string previous)
        {
            if (pool == null && total < 0)
            {
                if (!Posters)
                {
                    ItemsResult r = await Library.Episodes(series.Id, null);
                    pool = new List<Item>();
                    if (r?.Items != null)
                        foreach (Item e in r.Items)
                            if (e.LocationType != "Virtual" && e.ParentIndexNumber != 0) pool.Add(e);
                }
                else
                {
                    ItemsResult first = await Library.Favorites(types, 0, Window, "Overview");
                    total = first?.TotalRecordCount ?? 0;
                    if (total <= Window) pool = new List<Item>(first?.Items ?? new Item[0]);
                }
            }
            if (pool != null) return (pool, PickFrom(pool, previous));
            int at = random.Next(total);
            int start = Math.Min(Math.Max(0, at - Window / 2), total - Window);
            ItemsResult page = await Library.Favorites(types, start, Window, "Overview");
            var l = new List<Item>(page?.Items ?? new Item[0]);
            int p = Math.Min(at - start, l.Count - 1);
            if (p >= 0 && l[p].Id == previous) p = PickFrom(l, previous);
            return (l, Math.Max(0, p));
        }

        async void Roll(string previous)
        {
            int g = ++gen;
            StopTimers();
            Trim();
            rolling = true;
            failed = false;
            target = null;
            ring.Hide();
            card.Scale = new Vector3(1, 1, 1);
            message.Text = "";
            title.Text = detail.Text = overview.Text = "";
            reelTitle.Text = "";
            PaintButtons();
            List<Item> got;
            int p;
            try { (got, p) = await Draw(previous); }
            catch (Exception e)
            {
                Probe.Log("random", "échec du tirage : " + e.Message);
                if (Closed || g != gen) return;
                guard.Run("RandomFailed", () =>
                {
                    rolling = false;
                    failed = true;
                    message.Text = Lang.T("random.failed");
                    focus = 0;
                    PaintButtons();
                });
                return;
            }
            if (Closed || g != gen) return;
            guard.Run("RandomDraw", () => Start(got, p, g));
        }

        string ImageOf(Item it) => Posters ? Library.Poster(it, CardW) : Library.Landscape(it, CardW);

        void Start(List<Item> got, int p, int g)
        {
            items = got;
            pick = p;
            if (items.Count == 0)
            {
                rolling = false;
                message.Text = Lang.T(Posters ? "random.no_favourites" : "random.no_episodes");
                PaintButtons();
                return;
            }
            Item t = items[pick];
            var others = new List<Item>();
            for (int i = 0; i < items.Count; i++) if (i != pick) others.Add(items[i]);
            others = Shuffled(others);
            var frames = new List<Item>();
            if (others.Count > 0) for (int i = 0; i < Frames; i++) frames.Add(others[i % others.Count]);
            Probe.Log("random", $"tirage : « {t.Name} » parmi {items.Count} ({frames.Count} images de défilement)");
            foreach (Item f in frames) Load(f, g);
            bool started = false;
            void Go() { if (started || g != gen || Closed) return; started = true; waitTimer?.Stop(); Step(frames, 0, g); }
            Load(t, g, Go);
            waitTimer = new Timer(ImageWaitMs);
            waitTimer.Tick += (s, e) => { guard.Run("RandomWait", Go); return false; };
            waitTimer.Start();
        }

        // Image téléchargée puis préparée cachée sur la carte ; ready : appelé quand elle l'est (ou sans image).
        void Load(Item it, int g, Action ready = null)
        {
            string url = ImageOf(it);
            if (url == null || views.ContainsKey(url)) { ready?.Invoke(); return; }
            views[url] = null;
            ImageCache.Request(url, () => Closed || g != gen ? int.MaxValue : 0, path => guard.Run("RandomImage", () =>
            {
                if (Closed) return;
                View v = Pictures.Cover(path, CardW, CardH, out ImageView img);
                if (v != null)
                {
                    img.Rounded(Theme.RadiusLarge);
                    v.Hide();
                    card.Add(v);
                    views[url] = v;
                }
                if (g == gen) ready?.Invoke();
            }));
        }

        void Show(Item it)
        {
            string url = ImageOf(it);
            View v = url != null && views.TryGetValue(url, out View found) ? found : null;
            if (v == null) return; // pas prête : l'image précédente reste
            if (shown != null && shown != v) shown.Hide();
            v.Show();
            shown = v;
            placeholder.Hide();
        }

        void Step(List<Item> frames, int i, int g)
        {
            if (g != gen || Closed) return;
            if (i < frames.Count)
            {
                Item f = frames[i];
                Show(f);
                reelTitle.Text = NameOf(f);
                stepTimer = new Timer((uint)FrameMs(i));
                stepTimer.Tick += (s, e) => { guard.Run("RandomStep", () => Step(frames, i + 1, g)); return false; };
                stepTimer.Start();
                return;
            }
            Landed(items[pick]);
        }

        void Landed(Item t)
        {
            target = t;
            rolling = false;
            string url = ImageOf(t);
            if (url == null || !views.TryGetValue(url, out View v) || v == null)
            {
                shown?.Hide();
                shown = null;
                placeholder.Show();
            }
            else Show(t);
            reelTitle.Text = "";
            title.Text = NameOf(t);
            detail.Text = DetailOf(t);
            overview.Text = Pictures.Cut(t.Overview, 210) ?? "";
            ring.Show();
            land?.Stop();
            land = new Animation(LandMs);
            land.AnimateTo(card, "Scale", new Vector3(1.04f, 1.04f, 1f), 0, LandMs / 2, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            land.AnimateTo(card, "Scale", new Vector3(1f, 1f, 1f), LandMs / 2, LandMs, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            land.AnimateTo(ring, "Scale", new Vector3(1.04f, 1.04f, 1f), 0, LandMs / 2, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            land.AnimateTo(ring, "Scale", new Vector3(1f, 1f, 1f), LandMs / 2, LandMs, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseIn));
            land.Play();
            focus = buttons.FindIndex(b => b.Key == "play");
            PaintButtons();
            Probe.Log("random", $"posé : « {t.Name} »");
        }

        static string NameOf(Item it) =>
            it.Type == "Episode" ? ((Library.Code(it) ?? "") + "  " + (it.Name ?? "")).Trim() : it.Name ?? "";

        // Ligne grise sous le nom : celle des cartes (Library.Meta), sans le code ni le nom d'un
        // épisode, déjà dans le titre.
        static string DetailOf(Item it)
        {
            if (it.Type != "Episode") return Library.Meta(it);
            var parts = new List<string>();
            string score = Library.Score(it);
            if (score != null) parts.Add(score);
            if (it.RunTimeTicks > 0 && Library.Duration(it.RunTimeTicks.Value) is string d) parts.Add(d);
            if (it.UserData?.Played == true) parts.Add(Lang.T("item.watched"));
            return string.Join("  ·  ", parts);
        }

        // Images préparées gardées d'un tirage à l'autre, 30 au plus (une image de 640 × 360
        // décodée occupe ~0,9 Mo) : au-delà, toutes sauf celle à l'écran sont jetées.
        void Trim()
        {
            if (views.Count <= 30) return;
            var keep = new Dictionary<string, View>();
            foreach (var kv in views)
            {
                if (kv.Value != null && kv.Value == shown) { keep[kv.Key] = kv.Value; continue; }
                if (kv.Value == null) continue;
                kv.Value.Unparent();
                kv.Value.Dispose();
            }
            views.Clear();
            foreach (var kv in keep) views[kv.Key] = kv.Value;
        }

        void StopTimers()
        {
            stepTimer?.Stop();
            waitTimer?.Stop();
            land?.Stop();
        }

        // Nouveau tirage : les images déjà prêtes sont gardées (même liste), la carte repart.
        void Again()
        {
            string previous = target?.Id;
            Roll(previous);
        }

        // ---- Actions ----

        void Open()
        {
            Item t = target;
            Nav.Replace(ItemScreen.For(t));
        }

        async void Play()
        {
            Item t = target;
            if (t.Type != "Series")
            {
                Nav.Replace(new VersionsScreen(t, CardMenu.ResumeTicks(t), Posters ? null : series));
                return;
            }
            Item next = null;
            try { next = await Library.NextUp(t.Id); } catch (Exception e) { Probe.Log("random", "épisode suivant : " + e.Message); }
            if (Closed) return;
            guard.Run("RandomPlay", () =>
            {
                if (next != null) Nav.Replace(new VersionsScreen(next, CardMenu.ResumeTicks(next), t));
                else Nav.Replace(ItemScreen.For(t));
            });
        }

        public override bool OnKey(string key)
        {
            bool done = true;
            guard.Run("Key " + key, () => done = Keys(key));
            return done;
        }

        bool Keys(string key)
        {
            var visible = new List<int>();
            for (int i = 0; i < buttons.Count; i++) if (Usable(buttons[i].Key)) visible.Add(i);
            int at = visible.IndexOf(focus);
            switch (key)
            {
                case "Left":
                case "Right":
                    int to = at + (key == "Left" ? -1 : 1);
                    if (at < 0 || to < 0 || to >= visible.Count) { App.Current.KeyIgnored(); return true; }
                    focus = visible[to];
                    PaintButtons();
                    return true;
                case "Up":
                case "Down":
                    App.Current.KeyIgnored();
                    return true;
                case "Return":
                case "KP_Enter":
                    if (at < 0) { App.Current.KeyIgnored(); return true; }
                    string k = buttons[focus].Key;
                    if (k == "again") Again();
                    else if (k == "open") Open();
                    else Play();
                    return true;
            }
            return false;
        }

        public override void OnClose()
        {
            gen++;
            StopTimers();
            guard.Stop();
        }

        // Ouverture en fondu de 0,2 s, comme les autres panneaux.
        Animation appear; // gardée dans un champ
        bool appeared;

        public override void OnShow()
        {
            if (appeared) return;
            appeared = true;
            root.Opacity = 0f;
            appear = new Animation(Theme.Slide);
            appear.AnimateTo(root, "Opacity", 1f);
            appear.Play();
        }
    }
}
