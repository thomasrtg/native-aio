// « Qui regarde ? », repris de la version web (UserPicker de pages/sign-in.tsx, module 15) :
// logo, titre, les utilisateurs de la configuration (cinq par rangée), lien « Changer de serveur ».
// Rond à l'accent du thème à 30 % (bg-brand-500/30) avec l'initiale, photo par-dessus si le serveur
// en a une ; regardé = grossi ×1,1 (comme le web) avec le contour blanc. Un utilisateur protégé par
// un code (PIN ou mot de passe) n'est pas encore géré : un message le dit.
using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Screens
{
    class WhoScreen : Screen
    {
        const int Cols = 5, TileW = 192, Avatar = 168, NameGap = 18, NameH = 28, TileH = Avatar + NameGap + NameH, GapX = 36, GapY = 36;
        const float Grow = 1.1f;

        class Tile
        {
            public View Face;
            public View Ring;
            public Animation Anim; // gardée dans un champ
            public bool Big;
        }

        readonly PickableUser[] users;
        readonly List<Tile> tiles = new List<Tile>();
        readonly bool overScreen;
        AuthPage page;
        AuthPage.Item status;
        int focus;
        bool gridFocused, busy, shown;

        // overScreen : ouvert par-dessus l'écran visible (retour après une demi-heure) ;
        // choisir l'utilisateur actif y ramène.
        public WhoScreen(PickableUser[] users, bool overScreen = false) { this.users = users; this.overScreen = overScreen; }

        protected override View Build()
        {
            View root = AuthPage.Backdrop();
            page = new AuthPage(card: false);
            page.AddTitle(() => Lang.T("who.title"));

            int cols = Math.Min(users.Length, Cols);
            int rows = (users.Length + Cols - 1) / Cols;
            int gridW = cols * TileW + (cols - 1) * GapX;
            int gridH = rows * TileH + (rows - 1) * GapY;
            var grid = new View { Size = new Size(gridW, gridH) };
            for (int i = 0; i < users.Length; i++)
            {
                if (users[i].User?.Id == Session.UserId) focus = i;
                grid.Add(MakeTile(users[i], i));
            }
            AuthPage.Item gridItem = page.AddCustom(grid, gridW, gridH, GridKey, on => { gridFocused = on; Paint(); });

            status = page.AddStatus();
            page.AddLinks((() => Lang.T("signin.change_server"), () => Nav.Push(new ServerScreen())));
            page.Focus(gridItem);
            root.Add(page.View);
            return root;
        }

        View MakeTile(PickableUser u, int i)
        {
            var box = new View
            {
                Position = new Position((i % Cols) * (TileW + GapX), (i / Cols) * (TileH + GapY)),
                Size = new Size(TileW, TileH),
            };
            // Rond et contour grossis ensemble autour de leur centre (LECONS.md).
            var face = new View
            {
                Position = new Position((TileW - Avatar) / 2, 0),
                Size = new Size(Avatar, Avatar),
                PivotPoint = PivotPoint.Center,
                PositionUsesPivotPoint = false,
            };
            View ring = Theme.RoundRing(Avatar);
            ring.Position = new Position(-Theme.RingOut, -Theme.RingOut);
            ring.Hide();
            Color accent = Theme.Accent;
            var circle = new View
            {
                Size = new Size(Avatar, Avatar),
                BackgroundColor = new Color(accent.R, accent.G, accent.B, 0.3f),
            }.Rounded(Avatar / 2);
            string name = u.User?.Name ?? "?";
            // Initiale dessous, image par-dessus : si l'image ne vient pas, l'initiale reste.
            var initial = new TextLabel
            {
                Size = new Size(Avatar, Avatar),
                Text = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?",
                PointSize = Theme.SizeHuge,
                TextColor = Color.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            initial.FontStyle = Theme.SemiBold();
            circle.Add(initial);
            if (u.Avatar != null && u.Avatar.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                circle.Add(new ImageView
                {
                    Size = new Size(Avatar, Avatar),
                    ResourceUrl = u.Avatar, // sans DesiredWidth/Height : plante sur une image distante
                }.Rounded(Avatar / 2));
            }
            face.Add(ring);
            face.Add(circle);
            var label = new TextLabel
            {
                Position = new Position(0, Avatar + NameGap),
                Size = new Size(TileW, NameH),
                Text = name,
                PointSize = Theme.SizeBody,
                TextColor = Color.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Ellipsis = true,
            };
            box.Add(face);
            box.Add(label);
            tiles.Add(new Tile { Face = face, Ring = ring });
            return box;
        }

        public override void ApplyTexts() => page.ApplyTexts();

        public override void OnShow()
        {
            if (!shown) { shown = true; page.Appear(); Probe.Boot("« Qui regarde ? » posé"); Probe.BootFrame("« Qui regarde ? » : première image"); }
        }

        public override bool OnKey(string key) => page.OnKey(key);

        // Touches de la grille ; faux pour laisser la page passer au lien (Bas depuis la dernière rangée).
        bool GridKey(string key)
        {
            switch (key)
            {
                case "Left": if (focus % Cols > 0) Move(focus - 1); return true;
                case "Right": if (focus % Cols < Cols - 1 && focus + 1 < users.Length) Move(focus + 1); return true;
                case "Up": if (focus >= Cols) { Move(focus - Cols); return true; } return false;
                case "Down":
                    if (focus / Cols < (users.Length - 1) / Cols) { Move(Math.Min(focus + Cols, users.Length - 1)); return true; }
                    return false;
                case "Return":
                case "KP_Enter":
                    Choose(users[focus]);
                    return true;
            }
            return false;
        }

        void Move(int to)
        {
            if (to < 0 || to >= users.Length || to == focus) return;
            focus = to;
            Paint();
        }

        void Paint()
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile t = tiles[i];
                bool on = gridFocused && i == focus;
                if (on) t.Ring.Show(); else t.Ring.Hide();
                if (on == t.Big) continue;
                t.Big = on;
                // Part de la taille affichée, jamais arrêtée à mi-chemin (charte).
                t.Anim = new Animation(Theme.Slide);
                t.Anim.AnimateTo(t.Face, "Scale", on ? new Vector3(Grow, Grow, 1f) : new Vector3(1f, 1f, 1f), Theme.Ease());
                t.Anim.Play();
            }
        }

        async void Choose(PickableUser u)
        {
            if (busy || u.User?.Id == null) return;
            if (u.User.Id == Session.UserId) { if (overScreen) Nav.Back(); else Nav.Reset(new HomeScreen()); return; }
            if (u.Needs != null) { page.SetStatus(status, () => Lang.T("who.protected"), true); return; }
            busy = true;
            page.SetStatus(status, () => Lang.T("who.switching"), false);
            try
            {
                await Session.SwitchTo(u.User.Id);
                if (!Closed) Nav.Reset(new HomeScreen());
            }
            catch (ApiError e) { if (!Closed) page.SetStatus(status, () => SignInScreen.ErrorText(e), true); }
            catch (Exception) { if (!Closed) page.SetStatus(status, () => Lang.T("error.unknown"), true); }
            finally { busy = false; }
        }
    }
}
