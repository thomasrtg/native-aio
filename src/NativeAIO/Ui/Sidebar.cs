// Barre latérale (module 13), comme la version web : une colonne d'icônes dans la marge
// de gauche des écrans principaux (Accueil, Découvrir, Recherche, Favoris, Historique,
// Calendrier, Paramètres), dans l'ordre de la version web.
// - En haut, le profil (photo, sinon l'initiale) : OK ouvre « Qui regarde ? ».
// - Puis les sections, Paramètres tout en bas. Icônes Boxicons de la version web.
// - On y entre par Gauche depuis le bord gauche d'un écran ; Droite ou Retour en sort.
// - La colonne ne s'élargit pas et n'affiche aucun nom (choix de l'auteur : les icônes
//   suffisent) ; une bulle à côté du profil donne seulement ses messages.
// - Section en cours : rond blanc à 24 % ; icône regardée : rond blanc, icône foncée
//   (charte : focus des boutons) ; profil regardé : contour blanc et liseré sombre (images).
using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Screens;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class Sidebar
    {
        const int D = 56, X = 20, IconSize = 32, TopY = 60, FirstY = 164, Step = 72, BottomY = 964;
        const int TipH = 44, TipX = X + D + 16;


        class Entry
        {
            public string Section, TextKey;
            public Func<Screen> Make; // null : l'accueil
            public int Y;
            public View Box;
            public ImageView Light, Dark;
        }

        readonly Navigator nav;
        readonly List<Entry> entries = new List<Entry>();
        View ring, avatar, tip;
        TextLabel initial, tipLabel;
        ImageView photo;
        string current, userShown, photoUrl;
        int focus;
        Func<string> message; // remplace le nom du profil dans la bulle (chargement, erreur)
        Screen owner; // écran qui a prêté son focus

        public View View { get; } = new View { Size = new Size(X + D + 20, 1080) };
        public bool Focused { get; private set; }

        public Sidebar(Navigator nav)
        {
            this.nav = nav;
            View.Hide();

            // Profil : contour (blanc, puis liseré sombre) caché hors focus, rond à l'accent du
            // thème avec l'initiale, photo par-dessus quand le serveur en a une.
            ring = Theme.RoundRing(D);
            ring.Position = new Position(X - Theme.RingOut, TopY - Theme.RingOut);
            ring.Hide();
            View.Add(ring);
            avatar = new View { Position = new Position(X, TopY), Size = new Size(D, D), CornerRadius = D / 2, BackgroundColor = Theme.Accent };
            initial = new TextLabel
            {
                Size = new Size(D, D),
                PointSize = Theme.SizeBody,
                TextColor = Theme.OnAccent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            avatar.Add(initial);
            View.Add(avatar);
            entries.Add(new Entry { Section = "profile", Y = TopY });

            Add("home", "nav_home", "nav.home", null, FirstY);
            Add("discover", "nav_compass", "nav.discover", () => new DiscoverScreen(), FirstY + Step);
            Add("search", "nav_search", "nav.search", () => new SearchScreen(), FirstY + 2 * Step);
            Add("favorites", "nav_heart", "nav.favorites", () => new FavoritesScreen(), FirstY + 3 * Step);
            Add("history", "nav_history", "nav.history", () => new HistoryScreen(), FirstY + 4 * Step);
            Add("calendar", "nav_calendar", "nav.calendar", () => new CalendarScreen(), FirstY + 5 * Step);
            Add("settings", "nav_cog", "nav.settings", () => new SettingsScreen(), BottomY);

            tip = new View { Size = new Size(200, TipH), CornerRadius = Theme.RadiusLarge, BackgroundColor = Theme.Panel };
            tipLabel = new TextLabel { Size = new Size(200, TipH), PointSize = Theme.SizeBody, TextColor = Color.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tip.Add(tipLabel);
            tip.Hide();
            View.Add(tip);
        }

        void Add(string section, string icon, string textKey, Func<Screen> make, int y)
        {
            var e = new Entry { Section = section, TextKey = textKey, Make = make, Y = y };
            e.Box = new View { Position = new Position(X, y), Size = new Size(D, D), CornerRadius = D / 2 };
            int o = (D - IconSize) / 2;
            e.Light = new ImageView { Position = new Position(o, o), Size = new Size(IconSize, IconSize), ResourceUrl = Theme.IconPath(icon) };
            e.Dark = new ImageView { Position = new Position(o, o), Size = new Size(IconSize, IconSize), ResourceUrl = Theme.IconPath(icon + "_dark") };
            e.Dark.Hide();
            e.Box.Add(e.Light);
            e.Box.Add(e.Dark);
            View.Add(e.Box);
            entries.Add(e);
        }

        // L'écran visible a changé : barre montrée ou cachée, section en cours marquée.
        public void Sync(Screen top)
        {
            string s = top?.SidebarSection;
            if (Focused) Leave();
            message = null;
            if (s == null) { View.Hide(); return; }
            current = s;
            View.Show();
            if (userShown != Session.UserId) ShowUser();
            ApplyTheme(); // thème changé dans les Paramètres
            Paint();
        }

        async void ShowUser()
        {
            userShown = Session.UserId;
            string name = Session.UserName ?? "";
            initial.Text = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?";
            if (photo != null) { photo.Unparent(); photo.Dispose(); photo = null; photoUrl = null; }
            string url = null;
            try { url = Accounts.Avatar(await Accounts.Users()); }
            catch (Exception) { }
            if (url == null || userShown != Session.UserId || url == photoUrl) return;
            photoUrl = url;
            // Comme « Qui regarde ? » : image distante sans DesiredWidth/Height (plante sinon).
            photo = new ImageView { Size = new Size(D, D), ResourceUrl = url, CornerRadius = D / 2 };
            avatar.Add(photo);
        }

        // Thème changé : rond du profil à l'accent, initiale claire ou sombre.
        public void ApplyTheme()
        {
            avatar.BackgroundColor = Theme.Accent;
            initial.TextColor = Theme.OnAccent;
        }

        public void ApplyTexts()
        {
            if (Focused) ShowTip();
        }

        // Gauche depuis le bord gauche d'un écran : la barre prend le focus, sur la section en cours.
        public bool Enter()
        {
            Screen top = nav.Top;
            if (top?.SidebarSection == null || Focused) return false;
            focus = Math.Max(0, entries.FindIndex(e => e.Section == current));
            Focused = true;
            owner = top;
            message = null;
            Paint();
            ShowTip();
            top.OnSidebar(true);
            return true;
        }

        public void Leave()
        {
            Focused = false;
            tip.Hide();
            Paint();
            Screen o = owner;
            owner = null;
            if (o != null && !o.Closed) o.OnSidebar(false);
        }

        public bool OnKey(string key)
        {
            switch (key)
            {
                case "Up": return Move(-1);
                case "Down": return Move(1);
                case "Right": Leave(); return true;
                case "Return":
                case "KP_Enter": Run(entries[focus]); return true;
            }
            return false;
        }

        bool Move(int step)
        {
            int next = focus + step;
            if (next < 0 || next >= entries.Count) return false;
            focus = next;
            message = null;
            Paint();
            ShowTip();
            return true;
        }

        void Run(Entry e)
        {
            Screen top = nav.Top;
            if (e.Section == "profile")
            {
                Accounts.Switch(top, make => { message = make; if (Focused) ShowTip(); });
                return;
            }
            if (e.Section == current) { Leave(); return; }
            Leave();
            nav.Section(e.Make?.Invoke());
        }

        void Paint()
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                bool on = Focused && i == focus;
                if (e.Box == null) { if (on) ring.Show(); else ring.Hide(); continue; }
                e.Box.BackgroundColor = on ? Color.White : e.Section == current ? Theme.Chosen : Color.Transparent;
                if (on) { e.Light.Hide(); e.Dark.Show(); }
                else { e.Dark.Hide(); e.Light.Show(); }
            }
        }

        // Pas de nom à côté des icônes (demande de l'auteur : les icônes suffisent). La bulle ne
        // sert qu'aux messages du profil (chargement, un seul utilisateur, erreur), à droite de
        // l'icône regardée ; largeur estimée (13 px par caractère à 18 pt).
        void ShowTip()
        {
            Entry e = entries[focus];
            string text = e.Box == null ? message?.Invoke() : null;
            if (string.IsNullOrEmpty(text)) { tip.Hide(); return; }
            int w = 40 + text.Length * 13;
            tipLabel.Text = text;
            tip.Size = new Size(w, TipH);
            tipLabel.Size = new Size(w, TipH);
            tip.Position = new Position(TipX, e.Y + (D - TipH) / 2);
            tip.Show();
            tip.RaiseToTop();
        }
    }
}
