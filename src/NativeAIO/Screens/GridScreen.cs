// Base des écrans en grille d'affiches (module 13b) : Favoris et fiche d'une personne
// (l'Historique a sa propre présentation par jour : HistoryScreen). Un titre suivi du
// nombre de titres en gris (charte : titre et sous-titre), la rangée de pastilles Tout / Films / Séries, puis la grille (Ui/PosterGrid).
// Touches : Gauche/Droite sur les pastilles choisit directement ; Bas descend dans la grille,
// Haut depuis sa première ligne remonte aux pastilles ; OK ouvre la page du titre.
// À l'ouverture, le focus est sur les pastilles, puis passe sur la première affiche dès
// qu'elle arrive si aucune touche n'a été appuyée.
using System;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Screens
{
    abstract class GridScreen : Screen
    {
        protected const int TopY = 60, PillsGap = 24, GridGap = 44, PillsH = 56;
        protected static readonly Color Back = new Color(0.039f, 0.039f, 0.039f, 1f);   // #0A0A0A
        protected static readonly Color Muted = new Color(0.612f, 0.639f, 0.686f, 1f); // #9CA3AF

        protected View root, pane;
        protected Guarded guard;
        protected PosterGrid grid;
        CardMenu cardMenu;
        Pills pills;
        TextLabel title, count;
        bool inGrid, touched, inSidebar, shown;

        // Disposition : bord gauche de l'en-tête et de la grille, affiches par ligne, taille du
        // titre (30 pt pour un titre d'écran, 22 pt pour une section) et de son sous-titre.
        protected virtual int X => 96;
        protected virtual int Cols => 8;
        protected virtual int TitleSize => 30;
        protected virtual int TitleH => 56;
        protected virtual int CountSize => 18;

        protected abstract string TitleText { get; }
        protected abstract string[] Types { get; }    // une sorte par pastille
        protected abstract string Name { get; }       // journal de la sonde
        protected abstract Task<ItemsResult> Fetch(string types, int start, int size);
        protected virtual string Caption(Item it) => it.ProductionYear?.ToString();
        protected virtual bool ShowPlayed => true;
        protected virtual string EmptyText => Lang.T("discover.empty");
        protected virtual string FailedText => Lang.T("grid.failed");
        protected virtual bool RefreshOnReturn => true;
        protected virtual void BuildMore() { }

        protected int PillsY => TopY + TitleH + PillsGap;
        protected int GridY => PillsY + PillsH + GridGap;

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.ScreenBack(Back) };
            guard = new Guarded(root);
            pane = new View { Size = new Size(1920, 1080) };
            root.Add(pane);
            title = new TextLabel { Position = new Position(X, TopY), Size = new Size(1200, TitleH), PointSize = TitleSize, TextColor = Color.White, VerticalAlignment = VerticalAlignment.Bottom };
            count = new TextLabel { Position = new Position(X, TopY), Size = new Size(300, TitleH), PointSize = CountSize, TextColor = Muted, VerticalAlignment = VerticalAlignment.Bottom };
            pane.Add(title);
            pane.Add(count);
            pills = new Pills(pane, X, PillsY);
            grid = new PosterGrid(pane, guard, () => Closed, Name, X, GridY, Cols)
            {
                Caption = Caption,
                ShowPlayed = ShowPlayed,
                EmptyText = () => EmptyText,
                FailedText = () => FailedText,
                Changed = OnGridChanged,
            };
            BuildMore();
            cardMenu = new CardMenu(this, Guard) { Changed = MenuDone };
            root.Add(cardMenu.View); // menu d'une carte, au-dessus de tout
            grid.Load((start, size) => Fetch(Types[pills.Chosen], start, size));
            return root;
        }

        public override void ApplyTexts()
        {
            title.Text = TitleText;
            pills.SetTexts(new[] { Lang.T("discover.all"), Lang.T("discover.movies"), Lang.T("discover.shows") });
            grid.ApplyTexts();
            Paint();
        }

        public override void OnShow()
        {
            // Retour depuis la page d'un titre ou le lecteur : favori retiré, titre vu…
            if (shown && RefreshOnReturn) grid.Refresh();
            shown = true;
        }

        public override void OnHide() => cardMenu?.Hide();
        public override bool OnBack() => cardMenu.Back();
        public override bool OnKeyUp(string key) => cardMenu.KeyUp(key);

        // Après un envoi du menu d'une carte : coche « vu » remise à jour, liste redemandée
        // (favori retiré…).
        void MenuDone(Item it)
        {
            grid.Mark(it);
            if (RefreshOnReturn) grid.Refresh();
        }

        public override void OnClose()
        {
            grid.Stop();
            pills.Stop();
            guard.Stop();
        }

        void Guard(string where, Action run) => guard.Run(where, run);

        // Le nombre de titres après le titre, à 12 px, aligné sur le bas des lettres.
        void OnGridChanged()
        {
            count.Text = grid.Total > 0 ? grid.Total.ToString() : "";
            float w = title.NaturalSize2D.Width;
            count.PositionX = X + (w > 0 ? w : TitleText.Length * TitleSize * 0.7f) + 12;
            // Premier lot arrivé sans qu'aucune touche ait été appuyée : focus sur la première affiche.
            if (!touched && !inGrid && !inSidebar && grid.Count > 0) { inGrid = true; Paint(); }
            // Liste vidée au retour (dernier favori retiré) : le focus remonte aux pastilles.
            else if (inGrid && grid.Count == 0) { inGrid = false; Paint(); }
        }

        void Paint()
        {
            pills.Focus(!inSidebar && !inGrid);
            grid.Focus(!inSidebar && inGrid);
        }

        public override void OnSidebar(bool focused)
        {
            inSidebar = focused;
            Paint();
        }

        // Gauche au bord gauche : la barre latérale (écrans qui la montrent), sinon rien.
        protected virtual bool LeftEdge()
        {
            if (SidebarSection != null) return false;
            App.Current.KeyIgnored();
            return true;
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
                    if (pills.Move(-1)) { Reload(); return true; }
                    return LeftEdge();
                case "Right":
                    if (pills.Move(1)) Reload(); else App.Current.KeyIgnored();
                    return true;
                case "Down":
                case "Return":
                case "KP_Enter":
                    if (grid.Count > 0) { inGrid = true; Paint(); }
                    else if (grid.Failed) grid.Retry();
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
            if (key == "Return" || key == "KP_Enter")
            {
                Item it = grid.Current;
                if (it != null) cardMenu.Press(it, () => Nav.Push(ItemScreen.For(it)), grid.CurrentFrame);
                return true;
            }
            if (grid.Key(key)) return true;
            if (key == "Up") { inGrid = false; grid.ScrollTop(); Paint(); return true; }
            if (key == "Left") return LeftEdge();
            return false;
        }

        void Reload()
        {
            grid.Load((start, size) => Fetch(Types[pills.Chosen], start, size));
        }
    }

    // Favoris : films et séries marqués favoris (pages/favourites.tsx du web).
    class FavoritesScreen : GridScreen
    {
        public override string SidebarSection => "favorites";
        public override bool ArtBackdrop => true;
        protected override string TitleText => Lang.T("favorites.title");
        protected override string[] Types => new[] { "Movie,Series", "Movie", "Series" };
        protected override string Name => "favoris";
        protected override string EmptyText => Lang.T("favorites.empty");
        protected override Task<ItemsResult> Fetch(string types, int start, int size) => Library.Favorites(types, start, size);
    }
}
