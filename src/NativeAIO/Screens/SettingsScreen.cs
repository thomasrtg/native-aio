// Paramètres (module 13c, étape 2), ouverts par la barre latérale, à la présentation de
// la version web (docs/PARAMETRES.md) :
// - à gauche, les catégories en deux groupes (Regarder, Application) ; une catégorie
//   s'ouvre quand le focus y reste 0,35 s, comme le web sur une TV ;
// - à droite, la page de la catégorie (Ui/SettingsPage.cs) ; Droite ou OK y entre,
//   Gauche ou Retour revient aux catégories, Gauche depuis les catégories va à la barre.
// Une page est construite à sa première ouverture puis gardée ; les autres se
// construisent une à une quand aucune touche n'a été pressée depuis un moment.
// Réglages branchés : langue, Actualiser l'accueil, compte, cache et Qui regarde (gardés sur la TV) ;
// pages Lecture, Audio, Sous-titres, Interface et Thème (étape 3, lus et écrits sur le compte
// comme le web, Core/Prefs.cs). Les réglages des fonctions non faites restent pâlis (« Bientôt »).
// Le thème (accent, fond) s'applique tout de suite à cet écran ; les autres le prendront au module 14.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tizen.Applications;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Screens
{
    class SettingsScreen : Screen
    {
        // Même numéro que tizen-manifest.xml.

        const int X0 = 96, TitleY = 60, TopY = 150, ColW = 320, EntryH = 56, GroupPad = 8, GroupLabelH = 32, GroupGap = 16;
        const int PageX = X0 + ColW + 48, ViewBottom = 1040;
        const int RestMs = 350, PrebuildMs = 500, QuietMs = 400;

        class Category
        {
            public string Icon, LabelKey, DescKey;
            public bool Watching;
            public Action<SettingsPage> Fill;
            public View Box;
            public ImageView Light, Dark;
            public TextLabel Name;
            public float Y;
            public SettingsPage Page;
        }

        enum Zone { Column, Page }

        readonly List<Category> cats = new List<Category>();
        readonly List<TextLabel> groupLabels = new List<TextLabel>();
        View root;
        TextLabel title;
        Highlight colLight;
        readonly Picker picker = new Picker();
        int focus, painted, current;
        Zone zone = Zone.Column;
        bool side;
        Timer rest, prebuild; // gardés dans des champs, sinon ils s'arrêtent
        int lastKey;

        // Lignes dont le texte change sans changement de langue.
        readonly List<(SettingsPage page, SettingsPage.Row row)> dynamic = new List<(SettingsPage, SettingsPage.Row)>();
        SettingsPage.Row switchRow;
        Func<string> userMessage; // remplace l'aide de « Changer d'utilisateur »

        public override string SidebarSection => "settings";
        public override bool ArtBackdrop => true;

        static Func<string> T(string key) => () => Lang.T(key);
        static readonly Func<string> OnDevice = T("set.on_device");
        static readonly Func<string> OnAccount = T("set.on_account");
        static readonly Func<string> FollowsYou = T("set.follows_you");

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.ScreenBack(Theme.Background) };
            themeSeen = Theme.Version;
            title = new TextLabel { Position = new Position(X0, TitleY), Size = new Size(ColW, 56), PointSize = 30, TextColor = Color.White, VerticalAlignment = VerticalAlignment.Center };
            root.Add(title);

            Cat("playback", "set.playback", "set.playback_desc", true, FillPlayback);
            Cat("audio", "set.audio", "set.audio_desc", true, FillAudio);
            Cat("subtitles", "set.subtitles", "set.subtitles_desc", true, FillSubtitles);
            Cat("interface", "set.interface", "set.interface_desc", false, FillInterface);
            Cat("theme", "set.theme", "set.theme_desc", false, FillTheme);
            Cat("cache", "set.cache", "set.cache_desc", false, FillCache);
            Cat("account", "set.account", "set.account_desc", false, FillAccount);
            Cat("about", "set.about", "set.about_desc", false, FillAbout);
            BuildColumn();

            root.Add(picker.View);
            focus = painted = current = 0;
            Open(0);
            PaintColumn();
            return root;
        }

        void Cat(string icon, string label, string desc, bool watching, Action<SettingsPage> fill) =>
            cats.Add(new Category { Icon = icon, LabelKey = label, DescKey = desc, Watching = watching, Fill = fill });

        // Cartes des groupes, puis le fond blanc glissant, puis les catégories (ordre de dessin).
        void BuildColumn()
        {
            int y = TopY;
            var labels = new List<View>();
            foreach (bool watching in new[] { true, false })
            {
                List<Category> list = cats.FindAll(c => c.Watching == watching);
                int h = GroupPad + GroupLabelH + list.Count * EntryH + GroupPad;
                root.Add(new View { Position = new Position(X0, y), Size = new Size(ColW, h), BackgroundColor = Theme.White(0.04f) }.Rounded(12 + GroupPad));
                var label = new TextLabel { Position = new Position(X0 + GroupPad + 16, y + GroupPad), Size = new Size(ColW - 2 * GroupPad - 32, GroupLabelH), PointSize = 12, TextColor = Theme.Grey, VerticalAlignment = VerticalAlignment.Center };
                groupLabels.Add(label);
                labels.Add(label);
                float ey = y + GroupPad + GroupLabelH;
                foreach (Category c in list) { c.Y = ey; ey += EntryH; }
                y += h + GroupGap;
            }
            colLight = new Highlight(X0 + GroupPad, ColW - 2 * GroupPad, 12);
            root.Add(colLight.View);
            foreach (View l in labels) root.Add(l);
            foreach (Category c in cats)
            {
                c.Box = new View { Position = new Position(X0 + GroupPad, c.Y), Size = new Size(ColW - 2 * GroupPad, EntryH) }.Rounded(12);
                c.Light = new ImageView { Position = new Position(16, 14), Size = new Size(28, 28), ResourceUrl = Theme.IconPath("set_" + c.Icon) };
                c.Dark = new ImageView { Position = new Position(16, 14), Size = new Size(28, 28), ResourceUrl = Theme.IconPath("set_" + c.Icon + "_dark") };
                c.Name = new TextLabel { Position = new Position(60, 0), Size = new Size(ColW - 2 * GroupPad - 72, EntryH), PointSize = 18, VerticalAlignment = VerticalAlignment.Center, Ellipsis = true };
                c.Box.Add(c.Light);
                c.Box.Add(c.Dark);
                c.Box.Add(c.Name);
                root.Add(c.Box);
            }
            colLight.Place(cats[0].Y, EntryH);
        }

        public override void ApplyTexts()
        {
            title.Text = Lang.T("set.title");
            groupLabels[0].Text = Lang.T("set.group_watching");
            groupLabels[1].Text = Lang.T("set.group_app");
            foreach (Category c in cats)
            {
                c.Name.Text = Lang.T(c.LabelKey);
                c.Page?.ApplyTexts();
            }
        }

        // ---- Catégories ----

        void PaintColumn()
        {
            bool colFocus = zone == Zone.Column && !side;
            for (int i = 0; i < cats.Count; i++)
            {
                Category c = cats[i];
                bool on = colFocus && i == painted;
                // Catégorie affichée marquée en gris seulement quand le focus est ailleurs :
                // dans la colonne, la page suit le focus (un gris laissé sur l'ancienne
                // catégorie pendant 0,35 s faisait un effet bizarre, vu par l'auteur).
                c.Box.BackgroundColor = i == current && !colFocus ? Theme.White(0.24f) : Color.Transparent;
                c.Name.TextColor = on ? Theme.Ink : i == current ? Color.White : Theme.Text;
                if (on) { c.Dark.Show(); c.Light.Hide(); } else { c.Light.Show(); c.Dark.Hide(); }
            }
            if (colFocus) colLight.View.Show(); else { colLight.Stop(); colLight.View.Hide(); }
        }

        void MoveColumn(int step)
        {
            int i = focus + step;
            if (i < 0 || i >= cats.Count) { App.Current.KeyIgnored(); return; }
            bool sameGroup = cats[i].Watching == cats[focus].Watching;
            focus = i;
            // D'un groupe à l'autre, le fond blanc saute au lieu de glisser par-dessus le titre du groupe.
            if (sameGroup)
            {
                PaintColumn();
                colLight.SlideTo(cats[i].Y, EntryH, () => { if (!Closed) { painted = focus; PaintColumn(); } });
            }
            else
            {
                colLight.Place(cats[i].Y, EntryH);
                painted = focus;
                PaintColumn();
            }
            rest?.Stop();
            rest = new Timer(RestMs);
            rest.Tick += (s, e) => { if (!Closed && zone == Zone.Column) Open(focus); return false; };
            rest.Start();
        }

        void Open(int i)
        {
            Category c = cats[i];
            if (c.Page == null) BuildPage(c);
            if (i != current) cats[current].Page?.View.Hide();
            current = i;
            c.Page.View.Show();
            RefreshDynamic(c.Page);
            PaintColumn();
        }

        void BuildPage(Category c)
        {
            var p = new SettingsPage(c.Icon, T(c.LabelKey), T(c.DescKey));
            c.Fill(p);
            p.ApplyTexts();
            p.View.Position = new Position(PageX, TopY);
            p.View.Hide();
            root.Add(p.View);
            picker.View.RaiseToTop();
            c.Page = p;
        }

        // Une page de plus, quand rien ne se passe.
        bool Prebuild()
        {
            if (Closed) return false;
            if (unchecked(Environment.TickCount - lastKey) < QuietMs) return true;
            Category next = cats.Find(c => c.Page == null);
            if (next == null) return false;
            BuildPage(next);
            return cats.Exists(c => c.Page == null);
        }

        void EnterPage()
        {
            SettingsPage p = cats[current].Page;
            // Page sans ligne à choisir : on y entre seulement si elle a de quoi défiler.
            if (!p.HasFocusable && p.Fits(TopY, ViewBottom)) { App.Current.KeyIgnored(); return; }
            rest?.Stop();
            zone = Zone.Page;
            focus = painted = current;
            colLight.Place(cats[current].Y, EntryH);
            p.SetActive(true);
            p.Reveal(TopY, ViewBottom);
            PaintColumn();
        }

        void LeavePage()
        {
            zone = Zone.Column;
            cats[current].Page.SetActive(false);
            PaintColumn();
        }

        // ---- Écran ----

        public override void OnShow()
        {
            userMessage = null;
            Prefs.Changed -= PrefsChanged;
            Prefs.Changed += PrefsChanged;
            Prefs.Sync(); // réglages changés ailleurs (version web)
            RefreshDynamic(null); // utilisateur ou serveur changés entre-temps
            if (prebuild == null && cats.Exists(c => c.Page == null))
            {
                prebuild = new Timer(PrebuildMs);
                prebuild.Tick += (s, e) => { bool more = Prebuild(); if (!more) prebuild = null; return more; };
                prebuild.Start();
            }
        }

        public override void OnHide()
        {
            if (picker.IsOpen) picker.Close();
            rest?.Stop();
        }

        public override void OnClose()
        {
            Prefs.Changed -= PrefsChanged;
            rest?.Stop();
            confirmTimer?.Stop();
            prebuild?.Stop();
            prebuild = null;
        }

        void RefreshDynamic(SettingsPage only)
        {
            foreach (var (page, row) in dynamic)
                if (only == null || page == only) page.Refresh(row);
        }

        public override void OnSidebar(bool focused)
        {
            side = focused;
            if (focused) { if (picker.IsOpen) picker.Close(); if (zone == Zone.Page) cats[current].Page.SetActive(false); }
            zone = Zone.Column;
            focus = painted = current;
            colLight.Place(cats[current].Y, EntryH);
            PaintColumn();
        }

        // ---- Touches ----

        public override bool OnKey(string key)
        {
            lastKey = Environment.TickCount;
            if (picker.IsOpen)
            {
                if (!picker.OnKey(key)) App.Current.KeyIgnored();
                return true;
            }
            if (zone == Zone.Column)
            {
                switch (key)
                {
                    case "Up": MoveColumn(-1); return true;
                    case "Down": MoveColumn(1); return true;
                    case "Left": App.Current.ToSidebar(); return true;
                    case "Right":
                    case "Return":
                    case "KP_Enter":
                        if (focus != current) Open(focus);
                        EnterPage();
                        return true;
                }
                return false;
            }
            SettingsPage p = cats[current].Page;
            switch (key)
            {
                case "Up":
                case "Down":
                    if (!p.HasFocusable)
                    {
                        if (!p.Scroll(key == "Up" ? -1 : 1, TopY, ViewBottom)) App.Current.KeyIgnored();
                    }
                    else if (!p.Move(key == "Up" ? -1 : 1, TopY, ViewBottom)) App.Current.KeyIgnored();
                    else if (userMessage != null) { userMessage = null; p.Refresh(switchRow); }
                    return true;
                case "Left": LeavePage(); return true;
                case "Right": App.Current.KeyIgnored(); return true;
                case "Return":
                case "KP_Enter":
                    SettingsPage.Row r = p.Current;
                    if (r == null || r.Kind == SettingsPage.Kind.Soon || r.Run == null) App.Current.KeyIgnored();
                    else r.Run();
                    return true;
            }
            return false;
        }

        public override bool OnBack()
        {
            if (picker.IsOpen) { picker.Close(); return true; }
            if (zone == Zone.Page) { LeavePage(); return true; }
            return false;
        }

        // Ouvre la liste de choix sous la ligne regardée.
        void Pick(SettingsPage p, IList<string> options, int selected, Action<int> chosen, IList<(Color, Color)> swatches = null)
        {
            float top = p.FocusScreenTop();
            float bottom = top + p.Current.Height;
            picker.Open(options, selected, PageX + SettingsPage.Width - 24, top, bottom, chosen, swatches);
        }

        // ---- Pages ----

        void FillPlayback(SettingsPage p)
        {
            p.AddSection(T("set.versions"), FollowsYou);
            PrefToggle(p, T("set.auto_first"), T("set.auto_first_help"), () => Prefs.AutoPlayFirst, v => Prefs.AutoPlayFirst = v);
            PrefToggle(p, T("set.preload"), T("set.preload_help"), () => Prefs.PreloadVersions, v => Prefs.PreloadVersions = v);
            p.AddSection(T("set.next"), FollowsYou);
            PrefToggle(p, T("set.next_auto"), T("set.next_auto_help"), () => Prefs.NextAuto, v => Prefs.NextAuto = v);
            PrefToggle(p, T("set.next_first"), T("set.next_first_help"), () => Prefs.NextFallbackFirst, v => Prefs.NextFallbackFirst = v);
            PrefChoice(p, T("set.next_prompt"), T("set.next_prompt_help"), Prefs.Prompts, () => Prefs.NextPrompt, v => Prefs.NextPrompt = v, v => Lang.T("set.prompt_" + v));
            PrefChoice(p, T("set.next_lead"), T("set.next_lead_help"), Prefs.Leads, () => Prefs.NextLead, v => Prefs.NextLead = v, Duration);
            PrefChoice(p, T("set.next_countdown"), null, Prefs.Countdowns, () => Prefs.NextCountdown, v => Prefs.NextCountdown = v, Duration);
            PrefChoice(p, T("set.still"), T("set.still_help"), Prefs.StillAfter, () => Prefs.StillWatching, v => Prefs.StillWatching = v,
                n => n == 0 ? Lang.T("set.never") : Lang.T("set.episodes_n", n.ToString()));
            p.Soon(T("set.skip_fillers"), T("set.skip_fillers_help"));
            p.Soon(T("set.skip_recaps"), T("set.skip_recaps_help"));
            p.AddSection(T("set.skipping"), FollowsYou);
            string[] segKeys = { "set.seg_intro", "set.seg_recap", "set.seg_outro", "set.seg_preview", "set.seg_ads" };
            for (int i = 0; i < Prefs.SegmentTypes.Length; i++)
            {
                string type = Prefs.SegmentTypes[i];
                PrefChoice(p, T(segKeys[i]), type == "Outro" ? T("set.seg_outro_help") : null, Prefs.SegmentActions,
                    () => Prefs.Segment(type), v => Prefs.SetSegment(type, v), v => Lang.T("set.seg_" + v));
            }
            PrefToggle(p, T("set.chapters"), T("set.chapters_help"), () => Prefs.ChapterSkips, v => Prefs.ChapterSkips = v);
            p.AddSection(T("set.controls"), OnDevice);
            PrefChoice(p, T("set.skip_length"), T("set.skip_length_help"), Prefs.SeekSteps, () => Prefs.SeekStep, v => Prefs.SeekStep = v, Duration);
            PrefChoice(p, T("set.hold_rate"), T("set.hold_rate_help"), Prefs.HoldRates, () => Prefs.HoldRate, v => Prefs.HoldRate = v, RateText);
        }

        // « 2× », « 1,5× ».
        public static string RateText(float rate) =>
            Lang.T("set.rate", rate.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo(Lang.Current == "fr" ? "fr-FR" : "en-US")));

        // « 30 s », « 1 min », « 1,5 min ».
        static string Duration(int seconds) =>
            seconds < 60 ? Lang.T("set.seconds", seconds.ToString())
                : Lang.T("set.minutes", (seconds / 60.0).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo(Lang.Current == "fr" ? "fr-FR" : "en-US")));

        // Lignes des réglages gardés sur le compte (Core/Prefs.cs), redessinées quand le serveur a été relu.
        readonly List<(SettingsPage page, SettingsPage.Row row)> synced = new List<(SettingsPage, SettingsPage.Row)>();

        SettingsPage.Row PrefToggle(SettingsPage p, Func<string> label, Func<string> help, Func<bool> get, Action<bool> set)
        {
            SettingsPage.Row row = null;
            row = p.Toggle(label, help, get, () => { set(!get()); p.Changed(row); });
            synced.Add((p, row));
            return row;
        }

        SettingsPage.Row PrefChoice<V>(SettingsPage p, Func<string> label, Func<string> help, V[] values, Func<V> get, Action<V> set, Func<V, string> name)
        {
            SettingsPage.Row row = null;
            row = p.Choice(label, help, () => name(get()), () =>
            {
                var names = new List<string>();
                foreach (V v in values) names.Add(name(v));
                Pick(p, names, Math.Max(0, Array.IndexOf(values, get())), i => { set(values[i]); p.Changed(row); });
            });
            synced.Add((p, row));
            return row;
        }

        // Pages dont des lignes apparaissent ou disparaissent selon les réglages (listes).
        readonly List<Action> listUpdates = new List<Action>();

        void PrefsChanged()
        {
            if (Closed) return;
            foreach (var (page, row) in synced) page.Refresh(row);
            foreach (Action a in listUpdates) a();
            if (Theme.Version != themeSeen) ApplyTheme();
        }

        // ---- Thème ----

        int themeSeen;
        Action themeRows;         // pastilles et bouton de la page Thème
        View userBox;             // rond de l'utilisateur (Compte)
        TextLabel userInitial;

        // Thème changé (ici ou sur le web) : fond, pages déjà construites, rond de l'utilisateur.
        void ApplyTheme()
        {
            themeSeen = Theme.Version;
            root.BackgroundColor = Theme.ScreenBack(Theme.Background);
            Window.Instance.BackgroundColor = App.Background; // fond des écrans sans fond propre
            ArtLayer.Refresh(); // couleur de fond sous l'arrière-plan d'après l'image
            foreach (Category c in cats) c.Page?.ApplyTheme();
            if (userBox != null) { userBox.BackgroundColor = Theme.Accent; userInitial.TextColor = Theme.OnAccent; }
            themeRows?.Invoke();
        }

        // Nom du préréglage qui a cette couleur, sinon son code (couleur choisie sur le web
        // avec le nuancier libre).
        static string ColourName(string hex, bool accent)
        {
            foreach (var t in Theme.Presets)
                if ((accent ? t.Accent : t.Background) == hex) return Lang.T("set.preset_" + t.Key);
            return hex.ToUpperInvariant();
        }

        static int PresetIndex() =>
            Array.FindIndex(Theme.Presets, t => t.Accent == Prefs.AccentColor && t.Background == Prefs.BackgroundColor);

        static bool DefaultTheme() => Prefs.AccentColor == Prefs.DefaultAccent && Prefs.BackgroundColor == Prefs.DefaultBackground;

        static List<string> PresetNames()
        {
            var names = new List<string>();
            foreach (var t in Theme.Presets) names.Add(Lang.T("set.preset_" + t.Key));
            return names;
        }

        void FillAudio(SettingsPage p)
        {
            p.AddSection(T("set.language"), OnAccount);
            OrderedList(p, T("set.audio_langs"), T("set.audio_langs_help"), null, () => Prefs.AudioLanguages, Prefs.SetAudioLanguages,
                c => Languages.Name(c) ?? c, AudioLanguageOptions, () => Prefs.AudioLists ? 0 : 1);
            PrefToggle(p, T("set.audio_default"), T("set.audio_default_help"), () => Prefs.PlayDefaultAudio, v => Prefs.PlayDefaultAudio = v)
                .Visible = () => Prefs.AudioLanguages.Count > 0;
            OrderedList(p, T("set.audio_types"), T("set.audio_types_help"), () => Prefs.AudioTypesKnown, () => Prefs.AudioTypes, v => Prefs.AudioTypes = v,
                t => Lang.T("set.atype_" + t), null, () => 0);
            PrefToggle(p, T("set.remember"), T("set.audio_remember_help"), () => Prefs.RememberAudio, v => Prefs.RememberAudio = v);
        }

        // « Langue originale », puis l'anglais, puis les autres par ordre alphabétique de leur
        // nom affiché (le web les range par leur nom anglais, même en français).
        static List<string> AudioLanguageOptions()
        {
            List<string> codes = SubtitleLanguageOptions();
            codes.Insert(0, Languages.Original);
            return codes;
        }

        // L'anglais, puis les autres par ordre alphabétique (web : languageOptions, sans « Langue originale »).
        static List<string> SubtitleLanguageOptions()
        {
            var codes = new List<string>(Languages.Codes);
            codes.Remove("eng");
            var culture = System.Globalization.CultureInfo.GetCultureInfo(Lang.Current == "fr" ? "fr-FR" : "en-US");
            codes.Sort((a, b) => string.Compare(Languages.Name(a) ?? a, Languages.Name(b) ?? b, culture, System.Globalization.CompareOptions.None));
            codes.Insert(0, "eng");
            return codes;
        }

        // Liste ordonnée, comme LanguageList du web : une ligne d'en-tête (libellé, aide), une
        // ligne par élément (« 1.  Français ») dont OK propose Monter, Descendre, Supprimer, puis
        // « Ajouter une langue ». options null : liste fixe, seulement réordonnée. max 0 : sans
        // limite. shown null : toujours montrée.
        void OrderedList(SettingsPage p, Func<string> label, Func<string> help, Func<bool> shown, Func<List<string>> get, Action<List<string>> set,
            Func<string, string> name, Func<List<string>> options, Func<int> max)
        {
            bool Shown() => shown == null || shown();
            p.Info(label, help).Visible = Shown;
            var items = new List<SettingsPage.Row>();
            SettingsPage.Row add = null;

            SettingsPage.Row NewItem(int i) => new SettingsPage.Row
            {
                Kind = SettingsPage.Kind.Choice,
                Joined = true,
                Label = () => { List<string> l = get(); return i < l.Count ? Lang.T("set.list_item", i + 1, name(l[i])) : ""; },
                Visible = () => Shown() && i < get().Count,
                Run = () => ItemActions(i),
            };

            // Assez de lignes pour la liste (elles ne sont jamais retirées, seulement cachées).
            void Ensure()
            {
                int n = get().Count;
                while (items.Count < n)
                    items.Add(add != null ? p.InsertBefore(add, NewItem(items.Count)) : p.Add(NewItem(items.Count)));
            }

            // Après un changement : focus sur la ligne voulue, page replacée.
            void Changed(SettingsPage.Row focus)
            {
                Ensure();
                p.Update(focus != null ? p.Rows.IndexOf(focus) : -1);
                if (zone == Zone.Page && cats[current].Page == p) p.Reveal(TopY, ViewBottom);
            }

            void ItemActions(int i)
            {
                List<string> l = get();
                if (i >= l.Count) return;
                var acts = new List<int>(); // 0 monter, 1 descendre, 2 supprimer
                if (i > 0) acts.Add(0);
                if (i < l.Count - 1) acts.Add(1);
                if (options != null) acts.Add(2);
                if (acts.Count == 0) { App.Current.KeyIgnored(); return; }
                var names = new List<string>();
                foreach (int a in acts) names.Add(Lang.T(a == 0 ? "set.move_up" : a == 1 ? "set.move_down" : "set.remove"));
                Pick(p, names, -1, k =>
                {
                    List<string> next = get();
                    if (i >= next.Count) return;
                    string v = next[i];
                    next.RemoveAt(i);
                    int to = i;
                    if (acts[k] == 0) to = i - 1;
                    else if (acts[k] == 1) to = i + 1;
                    if (acts[k] != 2) next.Insert(to, v);
                    set(next);
                    Ensure();
                    // Supprimé : la ligne suivante, sinon « Ajouter » (web : focus repris pareil).
                    Changed(acts[k] != 2 ? items[to] : i < next.Count ? items[i] : add != null && add.Visible() ? add : i > 0 ? items[i - 1] : null);
                });
            }

            if (options != null)
            {
                add = p.Choice(() => get().Count > 0 ? Lang.T("set.add_other_lang") : Lang.T("set.add_lang"), null, null, () =>
                {
                    List<string> have = get();
                    var codes = options().FindAll(c => !have.Exists(h => h == c || (c != Languages.Original && Languages.Same(h, c))));
                    var names = new List<string>();
                    foreach (string c in codes) names.Add(name(c));
                    Pick(p, names, -1, k =>
                    {
                        List<string> next = get();
                        next.Add(codes[k]);
                        set(next);
                        Ensure();
                        Changed(add.Visible() ? add : items[next.Count - 1]);
                    });
                });
                add.Joined = true;
                add.Visible = () => Shown() && (max() == 0 || get().Count < max());
            }
            Ensure();
            listUpdates.Add(() => Changed(null));
        }

        void FillSubtitles(SettingsPage p)
        {
            p.AddSection(T("set.language"), OnAccount);
            OrderedList(p, T("set.sub_langs"), T("set.sub_langs_help"), null, () => Prefs.SubtitleLanguages, Prefs.SetSubtitleLanguages,
                c => Languages.Name(c) ?? c, SubtitleLanguageOptions, () => Prefs.SubtitleLists ? 0 : 1);
            PrefChoice(p, T("set.sub_mode"), () => Lang.T("set.smode_" + Prefs.SubtitleMode + "_help"), Prefs.SubtitleModes,
                () => Prefs.SubtitleMode, v => Prefs.SubtitleMode = v, v => Lang.T("set.smode_" + v));
            OrderedList(p, T("set.sub_types"), T("set.sub_types_help"), () => Prefs.SubtitleTypesKnown, () => Prefs.SubtitleTypes, v => Prefs.SubtitleTypes = v,
                t => Lang.T("set.stype_" + t), null, () => 0);
            PrefToggle(p, T("set.sub_only_mine"), T("set.sub_only_mine_help"), () => Prefs.SubtitleOnlyMine, v => Prefs.SubtitleOnlyMine = v)
                .Visible = () => Prefs.SubtitleOnlyMineKnown;
            PrefToggle(p, T("set.remember"), T("set.sub_remember_help"), () => Prefs.RememberSubtitles, v => Prefs.RememberSubtitles = v);
            p.AddSection(T("set.preview"), null);
            p.Custom(Preview(), PreviewH);
            p.AddSection(T("set.text"), FollowsYou);
            PrefChoice(p, T("set.size"), null, Prefs.SubtitleSizes, () => Prefs.SubtitleSize, v => Prefs.SubtitleSize = v,
                v => Lang.T("sub.size" + Array.IndexOf(Prefs.SubtitleSizes, v)));
            PrefChoice(p, T("set.font"), null, Prefs.SubtitleFonts, () => Prefs.SubtitleFont, v => Prefs.SubtitleFont = v, v => Lang.T("set.sfont_" + v));
            ColourChoice(p, () => Prefs.SubtitleColor, v => Prefs.SubtitleColor = v);
            PrefToggle(p, T("set.bold"), null, () => Prefs.SubtitleBold, v => Prefs.SubtitleBold = v);
            PrefChoice(p, T("set.height"), T("set.height_help"), Steps(Prefs.PositionMin, Prefs.PositionMax, 1), () => Prefs.SubtitlePosition,
                v => Prefs.SubtitlePosition = v, Percent);
            p.AddSection(T("set.outline"), null);
            PrefChoice(p, T("set.outline_width"), null, Prefs.SubtitleOutlines, () => Prefs.SubtitleOutline, v => Prefs.SubtitleOutline = v,
                v => Lang.T("set.soutline_" + v));
            PrefToggle(p, T("set.shadow"), null, () => Prefs.SubtitleShadow, v => Prefs.SubtitleShadow = v);
            ColourChoice(p, () => Prefs.SubtitleOutlineColor, v => Prefs.SubtitleOutlineColor = v);
            p.AddSection(T("set.background"), null);
            ColourChoice(p, () => Prefs.SubtitleBackgroundColor, v => Prefs.SubtitleBackgroundColor = v);
            PrefChoice(p, T("set.opacity"), T("set.opacity_help"), Steps(0, 100, 5), () => Prefs.SubtitleBackgroundOpacity,
                v => Prefs.SubtitleBackgroundOpacity = v, Percent);
        }

        const float PreviewH = 300;

        // Aperçu (web : carte « Aperçu ») : la phrase d'exemple au style choisi, sur un fond
        // dégradé gris, plus petite qu'à la lecture ; redessinée à chaque changement.
        View Preview()
        {
            float w = SettingsPage.Width - 48, h = PreviewH - 48;
            var box = new View { Position = new Position(24, 24), Size = new Size(w, h), BackgroundColor = new Color(0.22f, 0.25f, 0.32f, 1f), ClippingMode = ClippingModeType.ClipChildren }.Rounded(10);
            box.Add(Gradient.Make(new Position(0, 0), new Size(w, h), true, t => 0.85f * t));
            var look = new SubtitleLook(w - 80, h - 40, 0.6f);
            look.View.Position = new Position(40, 0);
            box.Add(look.View);
            void Update() { look.Apply(); look.SetText(Lang.T("set.preview_text")); }
            Update();
            listUpdates.Add(Update);
            return box;
        }

        static int[] Steps(int from, int to, int step)
        {
            var l = new List<int>();
            for (int v = from; v <= to; v += step) l.Add(v);
            return l.ToArray();
        }

        static string Percent(int v) => v + " %";

        // Couleur : les 9 du web sur TV ; une autre couleur choisie ailleurs est montrée en « #rrggbb ».
        void ColourChoice(SettingsPage p, Func<string> get, Action<string> set) =>
            PrefChoice(p, T("set.colour"), null, Prefs.SubtitleColors, get, set, v =>
            {
                int i = Array.IndexOf(Prefs.SubtitleColors, v);
                return i >= 0 ? Lang.T("set.scolor_" + Prefs.SubtitleColorNames[i]) : v;
            });

        void FillInterface(SettingsPage p)
        {
            p.AddSection(T("set.language"), OnDevice);
            p.Choice(T("set.app_lang"), T("set.app_lang_help"), T("set.lang_value"), () =>
                Pick(p, new[] { "Français", "English" }, Lang.Current == "fr" ? 0 : 1, i =>
                {
                    string code = i == 0 ? "fr" : "en";
                    if (code == Lang.Current) return;
                    Lang.Set(code);
                    App.Current.ApplyTexts();
                }));
            // Accueil et grilles, dans l'ordre du web (« À la une sur l'accueil » non repris : lu
            // sur le compte, choisi dans la version web). Effet à la prochaine ouverture de l'écran
            // touché ; l'accueil, toujours ouvert sous les autres écrans, se recharge au retour si
            // « Fusionner » ou la bannière ont changé.
            p.AddSection(T("set.home_grids"), FollowsYou);
            // Bannière à la une : « Désactivée » est propre à Native AIO et reste sur la TV ; les trois
            // autres choix sont ceux du web (module 14b). Puis le temps passé sur chaque titre
            // quand elle défile (toujours affiché, comme le web).
            PrefChoice(p, T("set.hero"), T("set.hero_help"), new[] { "off", "rotate", "follow", "corner" }, () => Prefs.Hero, v => Prefs.Hero = v, v => Lang.T("set.hero_" + v));
            PrefChoice(p, T("set.hero_seconds"), T("set.hero_seconds_help"), Prefs.HeroSecondsChoices, () => Prefs.HeroSeconds, v => Prefs.HeroSeconds = v, Duration);
            PrefToggle(p, T("set.merge_next"), T("set.merge_next_help"), () => Prefs.MergeNextUp, v => Prefs.MergeNextUp = v);
            PrefToggle(p, T("set.combine"), T("set.combine_help"), () => Prefs.CombineSearch, v => Prefs.CombineSearch = v);
            PrefChoice(p, T("set.poster_size"), T("set.poster_size_help"), Prefs.PosterSizes, () => Prefs.PosterSize, v => Prefs.PosterSize = v, v => Lang.T("set.size_" + v));
            PrefChoice(p, T("set.under_posters"), T("set.under_posters_help"), Prefs.PosterTexts, () => Prefs.PosterText, v => Prefs.PosterText = v,
                v => Lang.T("set.lines_" + (v == "title,year" ? "both" : v)));
            // Comme le web, après l'accueil et les grilles. Effet à la prochaine ouverture d'une page.
            p.AddSection(T("set.spoilers"), FollowsYou);
            PrefToggle(p, T("set.blur_spoilers"), T("set.blur_spoilers_help"), () => Prefs.BlurSpoilers, v => Prefs.BlurSpoilers = v);
            p.AddSection(null, null);
            p.Button(T("set.refresh"), T("set.refresh_help"), T("set.refresh_button"), RefreshHome);
        }

        // Comme le web : préréglages (fond et accent), accent et fond choisis à part parmi les
        // couleurs des préréglages (pas de nuancier libre sur TV), Réinitialiser hors du
        // thème par défaut. Pas de CSS.
        void FillTheme(SettingsPage p)
        {
            p.AddSection(T("set.theme"), FollowsYou);
            var presets = Theme.Presets;
            var both = new List<(Color, Color)>();
            var accents = new List<(Color, Color)>();
            var backgrounds = new List<(Color, Color)>();
            foreach (var t in presets)
            {
                both.Add((Theme.Hex(t.Background), Theme.Hex(t.Accent)));
                accents.Add((Theme.Hex(t.Accent), null));
                backgrounds.Add((Theme.Hex(t.Background), null));
            }

            SettingsPage.Row preset = p.Choice(T("set.presets"), null,
                () => { int i = PresetIndex(); return i >= 0 ? Lang.T("set.preset_" + presets[i].Key) : Lang.T("set.theme_custom"); },
                () => Pick(p, PresetNames(), Math.Max(0, PresetIndex()), i => Prefs.SetTheme(presets[i].Accent, presets[i].Background), both));
            SettingsPage.Row accent = p.Choice(T("set.accent"), T("set.accent_help"), () => ColourName(Prefs.AccentColor, true),
                () => Pick(p, PresetNames(), Math.Max(0, Array.FindIndex(presets, t => t.Accent == Prefs.AccentColor)),
                    i => Prefs.SetTheme(presets[i].Accent, Prefs.BackgroundColor), accents));
            SettingsPage.Row background = p.Choice(T("set.background"), T("set.background_help"), () => ColourName(Prefs.BackgroundColor, false),
                () => Pick(p, PresetNames(), Math.Max(0, Array.FindIndex(presets, t => t.Background == Prefs.BackgroundColor)),
                    i => Prefs.SetTheme(Prefs.AccentColor, presets[i].Background), backgrounds));
            // Arrière-plan d'après l'image : effet à la prochaine ouverture d'une page de titre.
            PrefToggle(p, T("set.art_background"), T("set.art_background_help"), () => Prefs.ArtBackground, v => { Prefs.ArtBackground = v; ApplyTheme(); });
            p.Button(T("set.reset_theme"), null, T("set.reset_button"), () => Prefs.SetTheme(Prefs.DefaultAccent, Prefs.DefaultBackground))
                .Visible = () => !DefaultTheme();

            var presetSwatch = new Swatch(28);
            var accentSwatch = new Swatch(28);
            var backgroundSwatch = new Swatch(28);
            p.SetSwatch(preset, presetSwatch);
            p.SetSwatch(accent, accentSwatch);
            p.SetSwatch(background, backgroundSwatch);
            Action paint = () =>
            {
                presetSwatch.Set(Theme.Background, Theme.Accent);
                accentSwatch.Set(Theme.Accent, null);
                backgroundSwatch.Set(Theme.Background, null);
            };
            paint();
            synced.Add((p, preset));
            synced.Add((p, accent));
            synced.Add((p, background));
            // Bouton Réinitialiser montré ou caché : page replacée (focus gardé au plus près).
            themeRows = () => { paint(); p.Update(); };
        }

        // ---- Cache (gardé sur la TV) ----

        void FillCache(SettingsPage p)
        {
            p.AddSection(null, OnDevice);
            SettingsPage.Row limit = null;
            limit = p.Choice(T("set.cache_space"), CacheHelp, () => Megabytes(Prefs.CacheLimitMb), () =>
            {
                var names = new List<string>();
                foreach (int mb in Prefs.CacheLimits) names.Add(Megabytes(mb));
                Pick(p, names, Math.Max(0, Array.IndexOf(Prefs.CacheLimits, Prefs.CacheLimitMb)), i =>
                {
                    Prefs.CacheLimitMb = Prefs.CacheLimits[i];
                    // Limite baissée : les plus anciennes partent tout de suite. L'accueil, resté
                    // dessous, garde les fichiers de ses cartes : rechargé au retour (sinon carte
                    // noire, vu à la sonde après « Tout effacer »).
                    if (ImageCache.Trim() > 0) HomeScreen.ReloadWanted = true;
                    p.Changed(limit);
                    p.Refresh(limit);
                });
            });
            dynamic.Add((p, limit));
            SettingsPage.Row clear = null;
            bool cleared = false;
            clear = p.Button(T("set.cache_clear"), T("set.cache_clear_help"), () => Lang.T(cleared ? "set.cache_cleared" : "set.cache_clear_button"), () =>
            {
                ImageCache.Clear();
                HomeScreen.ReloadWanted = true;
                cleared = true;
                p.Refresh(clear);
                p.Refresh(limit);
            });
        }

        static string CacheHelp()
        {
            string used = Bytes(ImageCache.TotalBytes);
            long free = ImageCache.FreeBytes();
            return free < 0 ? Lang.T("set.cache_space_help", used) : Lang.T("set.cache_space_help_free", used, Bytes(free));
        }

        // Taille lisible : « 12 Mo », « 3,4 Go ».
        static string Bytes(long n)
        {
            double mb = n / (1024.0 * 1024.0);
            if (mb < 1000) return Lang.T("set.mb", ((long)Math.Round(mb)).ToString());
            return Lang.T("set.gb", (mb / 1024.0).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo(Lang.Current == "fr" ? "fr-FR" : "en-US")));
        }

        static string Megabytes(int mb) => mb < 1000 ? Lang.T("set.mb", mb.ToString()) : Lang.T("set.gb", (mb / 1000).ToString());

        // ---- Compte ----

        SettingsPage.Row signOutRow;
        bool confirmSignOut;
        Timer confirmTimer; // gardé dans un champ, sinon il s'arrête

        void FillAccount(SettingsPage p)
        {
            p.AddSection(null, null);
            signOutRow = p.Button(() => Session.UserName ?? "",
                () => confirmSignOut ? Lang.T("set.sign_out_ask") : Session.ServerName ?? "",
                () => Lang.T(confirmSignOut ? "set.sign_out_confirm" : "home.sign_out"), SignOut);
            p.SetLead(signOutRow, UserPicture());
            dynamic.Add((p, signOutRow));
            switchRow = p.Button(T("home.switch_user"), () => userMessage?.Invoke() ?? Lang.T("set.switch_help"), T("set.switch_button"), SwitchUser);
            dynamic.Add((p, switchRow));
            dynamic.Add((p, p.Button(T("set.server"), () => Session.Base ?? "", T("set.change_server"), () => Nav.Push(new ServerScreen()))));
            // Le web ne montre cette section que s'il y a plusieurs utilisateurs ; ici toujours,
            // comme « Changer d'utilisateur ».
            p.AddSection(T("set.who"), OnDevice);
            SettingsPage.Row who = null;
            who = p.Toggle(T("set.who_ask"), T("set.who_ask_help"), () => Prefs.AskWho, () => { Prefs.AskWho = !Prefs.AskWho; p.Changed(who); });
        }

        // Comme le web, la déconnexion se confirme : premier OK, le bouton devient « Confirmer »
        // pendant 4 s ; second OK, déconnexion.
        void SignOut()
        {
            if (confirmSignOut) { confirmTimer?.Stop(); Accounts.SignOut(this); return; }
            confirmSignOut = true;
            SettingsPage p = cats[current].Page;
            p.Refresh(signOutRow);
            p.Changed(signOutRow);
            confirmTimer?.Stop();
            confirmTimer = new Timer(4000);
            confirmTimer.Tick += (s, e) =>
            {
                if (!Closed) { confirmSignOut = false; p.Refresh(signOutRow); p.Changed(signOutRow); }
                return false;
            };
            confirmTimer.Start();
        }

        // Rond à la couleur d'accent avec l'initiale, photo par-dessus si le serveur en a une
        // (comme la barre latérale).
        View UserPicture()
        {
            const int D = 56;
            var box = new View { Size = new Size(D, D), BackgroundColor = Theme.Accent }.Rounded(D / 2);
            string name = Session.UserName ?? "";
            userInitial = new TextLabel
            {
                Size = new Size(D, D),
                PointSize = 20,
                Text = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?",
                TextColor = Theme.OnAccent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            box.Add(userInitial);
            userBox = box;
            AddPhoto(box, D);
            return box;
        }

        async void AddPhoto(View box, int d)
        {
            string url = null;
            try { url = Accounts.Avatar(await Accounts.Users()); }
            catch (Exception) { }
            if (url == null || Closed) return;
            // Image distante sans DesiredWidth/Height (plante sinon, LECONS.md).
            box.Add(new ImageView { Size = new Size(d, d), ResourceUrl = url }.Rounded(d / 2));
        }

        // ---- À propos ----

        ServerAbout about;

        void FillAbout(SettingsPage p)
        {
            p.AddSection(null, null);
            p.Info(() => App.DisplayName, T("set.unofficial"), () => App.AppVersion);
            dynamic.Add((p, p.Info(T("set.server"), null, () =>
            {
                string name = about?.ServerName ?? Session.ServerName ?? "";
                string v = about?.AioVersion();
                return v != null ? name + " (" + v + ")" : name;
            })));
            dynamic.Add((p, p.Info(T("set.address"), () => Session.Base ?? "")));
            dynamic.Add((p, p.Info(T("set.jellyfin_api"), null, () => about?.Version ?? "…")));
            p.AddSection(T("set.credits"), null);
            p.Info(() => "AIOStreams", T("set.aiostreams_help"));
            p.Info(() => "Jellyfin", T("set.jellyfin_help"));
            p.Info(T("set.license"), T("set.license_help"));
            p.Info(T("set.icons"), T("set.icons_help"));
            p.Info(T("set.fonts"), T("set.fonts_help"));
            p.Info(T("set.qr"), T("set.qr_help"));
            // Signaler un problème (module 18) : QR code vers un ticket GitHub prérempli.
            p.AddSection(T("set.report"), null);
            PrefToggle(p, T("set.report_ask"), T("set.report_ask_help"), () => Crash.Ask, v => Crash.Ask = v);
            p.Button(T("set.report_now"), T("set.report_now_help"), T("set.report_open"), () => Nav.Push(new ReportScreen(Crash.Manual(), false)));
            LoadAbout(p);
        }

        async void LoadAbout(SettingsPage p)
        {
            try { about = await Session.Client.Get<ServerAbout>("/System/Info/Public"); }
            catch (Exception) { return; }
            if (!Closed) RefreshDynamic(p);
        }

        void SwitchUser()
        {
            SettingsPage p = cats[current].Page;
            Accounts.Switch(this, make => { userMessage = make; if (!Closed) p.Refresh(switchRow); });
        }

        void RefreshHome()
        {
            HomeScreen.ReloadWanted = true;
            Nav.Section(null);
        }
    }
}
