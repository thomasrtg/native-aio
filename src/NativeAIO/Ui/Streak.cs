// Barre de chargement : une traînée blanche parcourt une barre fine de gauche à droite et,
// arrivée au bout, reprend à gauche sans coupure, sa traînée continuant (demande de l'auteur).
// ClipChildren ne coupe rien sur la TV : la barre est faite de petits segments jointifs dont
// la luminosité est animée, la tête passant d'un segment au suivant. L'animation est jouée
// par la TV elle-même, sans travail du fil d'interface.
using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class Streak
    {
        const int SegmentW = 4;
        const float Base = 0.22f;  // luminosité de la barre hors traînée
        const float Trail = 0.45f; // longueur de la traînée, en part de la barre

        public View View { get; }
        readonly Animation anim; // gardée dans un champ

        // width : largeur de la barre en pixels ; period : durée d'un tour en ms.
        public Streak(int width, int height, int period)
        {
            View = new View { Size = new Size(width, height) };
            int n = Math.Max(2, width / SegmentW);
            anim = new Animation(period) { Looping = true };
            for (int i = 0; i < n; i++)
            {
                int x0 = width * i / n, x1 = width * (i + 1) / n; // jointifs : ni trou ni recouvrement
                var seg = new View { Position = new Position(x0, 0), Size = new Size(x1 - x0, height), BackgroundColor = Color.White, Opacity = Base };
                View.Add(seg);
                anim.AnimateBetween(seg, "Opacity", Frames(i / (float)n));
            }
            View.Hide();
        }

        // Luminosité d'un segment selon son retard sur la tête (0 : la tête est dessus).
        static float Light(float behind)
        {
            if (behind >= Trail) return Base;
            float k = 1f - behind / Trail;
            return Base + (1f - Base) * k * k;
        }

        // Images clés d'un segment placé en h (0 à 1) : la tête y passe à l'instant h du tour.
        static KeyFrames Frames(float h)
        {
            var times = new List<float> { 0f, 1f };
            foreach (float d in new[] { -0.002f, 0f, Trail / 4, Trail / 2, Trail * 3 / 4, Trail })
            {
                float t = h + d;
                t -= (float)Math.Floor(t);
                times.Add(t);
            }
            times.Sort();
            var kf = new KeyFrames();
            float last = -1f;
            foreach (float t in times)
            {
                if (t - last < 0.0005f) continue;
                last = t;
                float behind = t - h;
                behind -= (float)Math.Floor(behind);
                kf.Add(t, new PropertyValue(Light(behind)));
            }
            return kf;
        }

        public void Play()
        {
            View.Show();
            anim.Play();
        }

        public void Stop()
        {
            anim.Stop();
            View.Hide();
        }
    }
}
