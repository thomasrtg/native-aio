// Libellés courants des écrans de connexion : titre, texte d'aide, message d'état (charte).
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    static class Labels
    {
        public const int Left = Theme.Margin;

        public static TextLabel Make(int y, int height, float size, Color color = null, int width = 1600)
        {
            return new TextLabel
            {
                Position = new Position(Left, y),
                Size = new Size(width, height),
                PointSize = size,
                TextColor = color ?? Color.White,
                MultiLine = true,
            };
        }

        // Titre d'écran (30 pt), premier élément à 60 px du haut.
        public static TextLabel Title() => Make(Theme.Top, 60, Theme.SizeTitle);

        // Texte d'aide sous le titre : gris, texte courant.
        public static TextLabel Hint() => Make(Theme.Top + 76, 90, Theme.SizeBody, Theme.Grey);

        // Message d'état : texte courant, jaune pour une erreur (SetStatus).
        public static TextLabel Status(int y) => Make(y, 90, Theme.SizeBody);

        public static void SetStatus(TextLabel label, string text, bool error)
        {
            label.Text = text;
            label.TextColor = error ? Theme.Error : Theme.Text;
        }
    }
}
