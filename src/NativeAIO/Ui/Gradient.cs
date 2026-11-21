// Voile sombre en dégradé, dessiné par la TV (GradientVisual) : rendu continu, sans les paliers
// des bandes de View d'opacité croissante (voir LECONS.md, page d'un titre). Repris de
// ItemScreen.Shade, partagé avec le lecteur.
using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    static class Gradient
    {
        // De gauche à droite (horizontal) ou de haut en bas. L'opacité suit alpha(t), t de 0 à 1,
        // approchée par 9 arrêts de couleur ; shade : gris de la couleur (0 : noir).
        // radius : coins arrondis du voile, pour qu'il épouse une image arrondie (calendrier).
        public static View Make(Position at, Size size, bool horizontal, Func<float, float> alpha, float shade = 0f, float radius = 0f) =>
            Make(at, size, horizontal, alpha, new Color(shade, shade, shade, 1f), radius);

        // Même dégradé dans une couleur donnée (fond d'un panneau, teinté par le thème).
        public static View Make(Position at, Size size, bool horizontal, Func<float, float> alpha, Color color, float radius = 0f)
        {
            const int stops = 9;
            var offsets = new PropertyArray();
            var colors = new PropertyArray();
            for (int i = 0; i < stops; i++)
            {
                float t = i / (float)(stops - 1);
                offsets.PushBack(new PropertyValue(t));
                colors.PushBack(new PropertyValue(new Vector4(color.R, color.G, color.B, alpha(t))));
            }
            var map = new PropertyMap();
            map.Add(Visual.Property.Type, new PropertyValue((int)Visual.Type.Gradient));
            // Coordonnées de la vue, de -0,5 à 0,5.
            map.Add(GradientVisualProperty.StartPosition, new PropertyValue(horizontal ? new Vector2(-0.5f, 0f) : new Vector2(0f, -0.5f)));
            map.Add(GradientVisualProperty.EndPosition, new PropertyValue(horizontal ? new Vector2(0.5f, 0f) : new Vector2(0f, 0.5f)));
            map.Add(GradientVisualProperty.StopOffset, new PropertyValue(offsets));
            map.Add(GradientVisualProperty.StopColor, new PropertyValue(colors));
            if (radius > 0) map.Add(Visual.Property.CornerRadius, new PropertyValue(radius));
            return new View { Position = at, Size = size, Background = map };
        }

        // Dégradé radial (halo), en coordonnées de la vue (-0,5 à 0,5) : la TV l'étire à la vue, un
        // rond devient une ellipse si la vue n'est pas carrée (non vérifié sur la TV). Opacité
        // alpha(t) du centre (t = 0) au bord (t = 1), transparent au-delà.
        public static View Radial(Position at, Size size, Vector2 center, float radius, Color color, Func<float, float> alpha)
        {
            const int stops = 9;
            var offsets = new PropertyArray();
            var colors = new PropertyArray();
            for (int i = 0; i < stops; i++)
            {
                float t = i / (float)(stops - 1);
                offsets.PushBack(new PropertyValue(t));
                colors.PushBack(new PropertyValue(new Vector4(color.R, color.G, color.B, alpha(t))));
            }
            var map = new PropertyMap();
            map.Add(Visual.Property.Type, new PropertyValue((int)Visual.Type.Gradient));
            map.Add(GradientVisualProperty.Center, new PropertyValue(center));
            map.Add(GradientVisualProperty.Radius, new PropertyValue(radius));
            map.Add(GradientVisualProperty.StopOffset, new PropertyValue(offsets));
            map.Add(GradientVisualProperty.StopColor, new PropertyValue(colors));
            return new View { Position = at, Size = size, Background = map };
        }
    }
}
