// Fond blanc de l'élément regardé, qui glisse d'un élément à l'autre en 0,2 s depuis sa
// position affichée (demande de l'auteur, module 13a). Les textes passent en foncé à
// mi-chemin (en distance, pas en temps) : l'appelant les repeint dans le rappel « mid ».
using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class Highlight
    {
        const int SlideMs = Theme.Slide; // dynamique commune (Theme.Ease)
        // Moment où le fond blanc a fait la moitié du chemin : l'animation ralentit à la fin
        // (EaseOut de la TV, 1 - (1 - t)³), la moitié est atteinte à 21 % du temps. Recolorer
        // à la moitié du temps laissait l'ancien texte foncé sur fond noir, donc invisible,
        // pendant ~60 ms : « clignotement » vu par l'auteur (2026-11-12).
        const int MidMs = SlideMs * 21 / 100;

        public View View { get; }
        Animation slide; // gardée dans un champ, sinon elle s'arrête
        Timer half;

        public Highlight(float x, float width, float radius)
        {
            View = new View { PositionX = x, Size = new Size(width, 0), BackgroundColor = Color.White }.Rounded(radius);
        }

        // Pose sans animation.
        public void Place(float y, float height)
        {
            Stop();
            View.PositionY = y;
            View.SizeHeight = height;
        }

        // Glisse vers (y, height) ; mid est appelé à mi-chemin.
        public void SlideTo(float y, float height, Action mid)
        {
            float fromY = View.CurrentPosition.Y, fromH = View.CurrentSize.Height;
            Stop();
            View.PositionY = fromY;
            View.SizeHeight = fromH;
            slide = new Animation(SlideMs);
            var ease = Theme.Ease();
            slide.AnimateTo(View, "PositionY", y, ease);
            if (Math.Abs(fromH - height) > 0.5f) slide.AnimateTo(View, "SizeHeight", height, ease);
            slide.Play();
            half = new Timer(MidMs);
            half.Tick += (s, e) => { mid?.Invoke(); return false; };
            half.Start();
        }

        public void Stop()
        {
            slide?.Stop();
            slide = null;
            half?.Stop();
            half = null;
        }
    }
}
