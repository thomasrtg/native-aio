// Pastille de couleur (page Thème, comme le web) : rond de la couleur dans un fin anneau,
// avec au centre un point d'une seconde couleur (fond et accent d'un préréglage).
// L'anneau est clair sur fond sombre, sombre sur la ligne regardée (fond blanc).
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class Swatch
    {
        public View View { get; }
        readonly View fill, dot;

        public Swatch(int d)
        {
            View = new View { Size = new Size(d, d), CornerRadius = d / 2f };
            fill = new View { Position = new Position(2, 2), Size = new Size(d - 4, d - 4), CornerRadius = (d - 4) / 2f };
            int s = d / 2 - 2;
            dot = new View { Position = new Position((d - 4 - s) / 2f, (d - 4 - s) / 2f), Size = new Size(s, s), CornerRadius = s / 2f };
            fill.Add(dot);
            View.Add(fill);
            OnWhite(false);
        }

        // dotColor null : rond plein.
        public void Set(Color fillColor, Color dotColor)
        {
            fill.BackgroundColor = fillColor;
            if (dotColor == null) dot.Hide();
            else { dot.BackgroundColor = dotColor; dot.Show(); }
        }

        public void OnWhite(bool on) => View.BackgroundColor = on ? Theme.InkA(0.24f) : Theme.White(0.25f);
    }
}
