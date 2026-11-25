// Note personnelle (demande de l'auteur, 2026-11-25, comme le web : components/rating.tsx) :
// dix étoiles par demi-point, sur l'échelle de 0 à 10 que garde le serveur (UserData.Rating).
// Ouverte par la ligne « Noter » du menu d'une carte, ou par le bouton étoile de la page d'un
// titre, posée à côté de ce qui l'a ouverte (comme le menu d'une carte).
// Gauche/Droite : un demi-point de moins ou de plus ; OK : envoie la note ; Bas : « Effacer la
// note » (si le titre en a une), Haut : retour aux étoiles ; Retour : ferme sans rien changer.
// Sans note, on part de 5 (le web part de 0 ; à la télécommande, une touche gardée enfoncée
// n'envoie qu'un appui, LECONS.md).
// Étoiles en images (Boxicons du web) : la TV ne coupe pas une vue (ClipChildren sans effet),
// une demi-étoile ne peut donc pas être une étoile pleine à moitié cachée.
using System;
using System.Globalization;
using NativeAIO.Core;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class RatingPanel
    {
        const int Stars = 10, StarW = 44, StarD = 36, Pad = 24, W = 2 * Pad + Stars * StarW;
        const int HeadY = 18, HeadH = 30, StarsY = 60, StarsH = 52, HintY = 120, HintH = 24, ClearY = 156, ClearH = 56;
        const double Step = 0.5, Start = 5;

        public View View { get; }
        public bool IsOpen { get; private set; }

        readonly TextLabel heading, value, hint, clearText;
        readonly View starsBox, clear;
        readonly ImageView[] stars = new ImageView[Stars];
        readonly string[] shownIcon = new string[Stars];
        double draft;
        bool rated, onClear;
        Action<double?> chosen;

        public RatingPanel()
        {
            View = new View { Size = new Size(W, 0), BackgroundColor = Theme.Menu }.Rounded(Theme.RadiusLarge + 8);
            heading = new TextLabel
            {
                Position = new Position(Pad, HeadY), Size = new Size(W / 2, HeadH), PointSize = Theme.SizeInfo, TextColor = Theme.Grey,
                FontStyle = Theme.SemiBold(), VerticalAlignment = VerticalAlignment.Center,
            };
            value = new TextLabel
            {
                Position = new Position(W / 2, HeadY), Size = new Size(W / 2 - Pad, HeadH), PointSize = Theme.SizeBody, TextColor = Color.White,
                FontStyle = Theme.SemiBold(), HorizontalAlignment = HorizontalAlignment.End, VerticalAlignment = VerticalAlignment.Center,
            };
            starsBox = new View { Position = new Position(Pad - 8, StarsY), Size = new Size(Stars * StarW + 16, StarsH) }.Rounded(Theme.RadiusLarge);
            for (int i = 0; i < Stars; i++)
            {
                stars[i] = new ImageView { Position = new Position(8 + i * StarW + (StarW - StarD) / 2, (StarsH - StarD) / 2), Size = new Size(StarD, StarD) };
                starsBox.Add(stars[i]);
            }
            hint = new TextLabel
            {
                Position = new Position(Pad, HintY), Size = new Size(W - 2 * Pad, HintH), PointSize = Theme.SizeSmall, TextColor = Theme.Grey,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            clear = new View { Position = new Position(Pad - 8, ClearY), Size = new Size(W - 2 * Pad + 16, ClearH) }.Rounded(Theme.RadiusLarge);
            clearText = new TextLabel
            {
                Size = new Size(W - 2 * Pad + 16, ClearH), PointSize = Theme.SizeBody,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            clear.Add(clearText);
            foreach (View v in new View[] { heading, value, starsBox, hint, clear }) View.Add(v);
            View.Hide();
        }

        static string Icon(string name) => Tizen.Applications.Application.Current.DirectoryInfo.Resource + "icons/" + name + ".png";

        // « 8.5 » : comme le web (formatRating).
        public static string Format(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);

        // rating : note actuelle (null : aucune) ; (cx, cy, cw, ch) : ce qui l'ouvre, à l'écran.
        // onChosen reçoit la note à envoyer, ou null pour l'effacer.
        public void Open(double? rating, float cx, float cy, float cw, float ch, Action<double?> onChosen)
        {
            const int Away = 24, Edge = 48;
            rated = rating != null;
            draft = rating != null ? Math.Min(Stars, Math.Max(Step, Math.Round(rating.Value * 2) / 2)) : Start;
            onClear = false;
            chosen = onChosen;
            heading.Text = Lang.T("rating.title");
            hint.Text = Lang.T("rating.hint");
            clearText.Text = Lang.T("rating.clear");
            if (rated) clear.Show(); else clear.Hide();
            float h = rated ? ClearY + ClearH + 16 : HintY + HintH + 16;
            View.SizeHeight = h;
            float x = cx + cw + Away;
            if (x + W > 1920 - Edge) x = Math.Max(Edge, cx - Away - W);
            float y = cy + (ch - h) / 2;
            View.Position = new Position(x, Math.Max(40, Math.Min(y, 1040 - h)));
            Paint();
            View.RaiseToTop();
            View.Show();
            IsOpen = true;
            Probe.Log("menu", $"note ouverte ({(rated ? Format(rating.Value) : "aucune")})");
        }

        public void Close()
        {
            IsOpen = false;
            chosen = null;
            View.Hide();
        }

        void Paint()
        {
            value.Text = Format(draft) + " / 10";
            for (int i = 0; i < Stars; i++)
            {
                double fill = draft - i;
                string name = fill >= 1 ? "star_full" : fill >= Step ? "star_half" : "star_empty";
                if (shownIcon[i] != name) { stars[i].ResourceUrl = Icon(name); shownIcon[i] = name; }
                // Icônes blanches teintées au mélange (LECONS.md) : accent, ou blanc à 30 % comme le web.
                stars[i].Color = name == "star_empty" ? new Color(1, 1, 1, 0.3f) : Theme.Accent;
            }
            starsBox.BackgroundColor = onClear ? Color.Transparent : Theme.Surface;
            clear.BackgroundColor = onClear ? Color.White : Theme.Surface;
            clearText.TextColor = onClear ? Theme.Ink : Theme.Text;
        }

        // true si la touche est prise (toutes le sont tant que le panneau est ouvert).
        public bool OnKey(string key)
        {
            switch (key)
            {
                case "Left":
                    if (onClear) return true;
                    draft = Math.Max(Step, draft - Step);
                    break;
                case "Right":
                    if (onClear) return true;
                    draft = Math.Min(Stars, draft + Step);
                    break;
                case "Down":
                    if (!rated || onClear) return true;
                    onClear = true;
                    break;
                case "Up":
                    if (!onClear) return true;
                    onClear = false;
                    break;
                case "Return":
                case "KP_Enter":
                    Action<double?> act = chosen;
                    double? v = onClear ? (double?)null : draft;
                    Close();
                    act?.Invoke(v);
                    return true;
                default:
                    return false;
            }
            Paint();
            return true;
        }
    }
}
