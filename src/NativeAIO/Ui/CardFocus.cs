// Focus d'une liste de cartes dans un panneau (liste des liens, tiroir des versions du lecteur,
// bandes-annonces ; module 14, étape 3, demande de l'auteur, 2026-11-19) : assombrissement de 20 %
// sous les cartes et contour au centre transparent par-dessus, une seule pièce qui glisse d'une
// carte à l'autre avec la dynamique commune (Theme.Slide, Theme.Ease), depuis sa place affichée ;
// sa hauteur suit celle de la carte. Elle apparaît et disparaît en fondu de 0,2 s, sur place.
// À poser dans la vue qui porte les cartes (elle défile avec elles).
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class CardFocus
    {
        readonly View dim;
        readonly ImageView ring;
        Animation move, fade; // gardées dans des champs, sinon elles s'arrêtent
        bool shown;

        // x, width : colonne des cartes dans parent ; radius : arrondi des cartes.
        public CardFocus(View parent, float x, int width, int radius)
        {
            dim = new View { PositionX = x, Size = new Size(width, 0), CornerRadius = radius, BackgroundColor = Theme.Shade, Opacity = 0f };
            ring = Theme.HollowRing(width, 0, radius);
            ring.PositionX = x - Theme.RingOut;
            ring.Opacity = 0f;
            parent.Add(dim);
            dim.LowerToBottom(); // sous les cartes, même celles ajoutées ensuite
            parent.Add(ring);
        }

        // Contour remis au-dessus des cartes ajoutées après lui.
        public void Raise() => ring.RaiseToTop();

        // Vers la carte (y, h) : glisse si le focus est montré, sinon y est posé puis apparaît.
        public void To(float y, float h)
        {
            if (!shown)
            {
                move?.Stop();
                move = null;
                Set(y, h);
                Show(true);
                return;
            }
            float fromY = dim.CurrentPosition.Y, fromH = dim.CurrentSize.Height;
            if (System.Math.Abs(fromY - y) < 0.5f && System.Math.Abs(fromH - h) < 0.5f) return;
            move?.Stop();
            Set(fromY, fromH);
            var ease = Theme.Ease();
            move = new Animation(Theme.Slide);
            move.AnimateTo(dim, "PositionY", y, ease);
            move.AnimateTo(ring, "PositionY", y - Theme.RingOut, ease);
            if (System.Math.Abs(fromH - h) > 0.5f)
            {
                move.AnimateTo(dim, "SizeHeight", h, ease);
                move.AnimateTo(ring, "SizeHeight", h + 2 * Theme.RingOut, ease);
            }
            move.Play();
        }

        // Focus sorti de la liste : efface sur place.
        public void Hide()
        {
            if (shown) Show(false);
        }

        // Liste vidée : caché tout de suite.
        public void Clear()
        {
            move?.Stop();
            move = null;
            fade?.Stop();
            fade = null;
            shown = false;
            dim.Opacity = 0f;
            ring.Opacity = 0f;
        }

        void Set(float y, float h)
        {
            dim.PositionY = y;
            dim.SizeHeight = h;
            ring.PositionY = y - Theme.RingOut;
            ring.SizeHeight = h + 2 * Theme.RingOut;
        }

        // Fondu depuis l'opacité affichée (jamais repris de zéro à mi-chemin).
        void Show(bool on)
        {
            shown = on;
            fade?.Stop();
            float o = on ? 1f : 0f;
            fade = new Animation(Theme.Slide);
            fade.AnimateTo(dim, "Opacity", o);
            fade.AnimateTo(ring, "Opacity", o);
            fade.Play();
        }
    }
}
