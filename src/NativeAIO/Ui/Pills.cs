// Rangée de pastilles (Tout / Films / Séries) comme celles de Découvrir et de la version
// web (pill-tabs) : capsule à 8 %, pastille blanche du choix qui glisse (animation de la
// TV, 0,2 s), textes recolorés à mi-chemin ; focus : contour blanc et liseré sombre autour
// de toute la rangée (charte). Gauche/Droite choisit directement la voisine, sans OK.
using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class Pills
    {
        const int H = 56, PillH = 44, Pad = 6, MinW = 110, SlideMs = 200;
        static readonly Color Faint = new Color(1f, 1f, 1f, 0.08f);
        static readonly Color Text = new Color(1f, 1f, 1f, 0.85f);
        static readonly Color Ink = new Color(0.039f, 0.051f, 0.071f, 1f); // #0A0D12
        static readonly Color Back = new Color(0.039f, 0.039f, 0.039f, 1f); // #0A0A0A

        readonly View parent;
        readonly int x, y;
        readonly List<TextLabel> labels = new List<TextLabel>();
        readonly List<(int x, int w)> spots = new List<(int, int)>();
        View group, ring, thumb;
        Animation slide;
        Timer colorTimer; // gardé dans un champ, sinon il s'arrête
        int shown; // choix dont le texte est foncé
        bool focused;

        public int Chosen { get; private set; }
        public int Count => labels.Count;
        public int Width { get; private set; }

        public Pills(View parent, int x, int y)
        {
            this.parent = parent;
            this.x = x;
            this.y = y;
        }

        // Pose (ou repose, au changement de langue) les textes ; le choix est gardé.
        public void SetTexts(string[] texts)
        {
            foreach (View v in new[] { ring, group, thumb }) if (v != null) { v.Unparent(); v.Dispose(); }
            labels.Clear();
            spots.Clear();
            slide?.Stop();
            group = new View { Position = new Position(x, y) };
            thumb = new View { Size = new Size(MinW, PillH), CornerRadius = PillH / 2f, BackgroundColor = Color.White };
            group.Add(thumb);
            int px = Pad;
            foreach (string t in texts)
            {
                var label = new TextLabel { PointSize = 18, Text = t };
                float tw = label.NaturalSize2D.Width;
                if (tw <= 0) tw = t.Length * 12;
                int w = Math.Max(MinW, (int)tw + 44);
                label.Position = new Position(px, Pad);
                label.Size = new Size(w, PillH);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.VerticalAlignment = VerticalAlignment.Center;
                group.Add(label);
                labels.Add(label);
                spots.Add((px, w));
                px += w + 4;
            }
            Width = px - 4 + Pad;
            group.Size = new Size(Width, H);
            group.CornerRadius = H / 2f;
            group.BackgroundColor = Faint;
            ring = new View { Position = new Position(x - 6, y - 6), Size = new Size(Width + 12, H + 12), CornerRadius = H / 2f + 6, BackgroundColor = Color.White };
            ring.Add(new View { Position = new Position(3, 3), Size = new Size(Width + 6, H + 6), CornerRadius = H / 2f + 3, BackgroundColor = Back });
            parent.Add(ring);
            parent.Add(group);
            Chosen = Math.Max(0, Math.Min(Chosen, labels.Count - 1));
            thumb.Position = new Position(spots[Chosen].x, Pad);
            thumb.SizeWidth = spots[Chosen].w;
            shown = Chosen;
            Recolor();
            Focus(focused);
        }

        public void Focus(bool on)
        {
            focused = on;
            if (ring == null) return;
            if (on) ring.Show(); else ring.Hide();
        }

        // Gauche (-1) ou Droite (+1) : la voisine devient le choix. Faux au bout de la rangée.
        public bool Move(int step)
        {
            int next = Chosen + step;
            if (next < 0 || next >= labels.Count) return false;
            Chosen = next;
            slide?.Stop();
            slide = new Animation(SlideMs);
            var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
            slide.AnimateTo(thumb, "PositionX", (float)spots[next].x, ease);
            slide.AnimateTo(thumb, "SizeWidth", (float)spots[next].w, ease);
            slide.Play();
            colorTimer?.Stop();
            colorTimer = new Timer(SlideMs / 2);
            colorTimer.Tick += (s, e) => { shown = Chosen; Recolor(); return false; };
            colorTimer.Start();
            return true;
        }

        void Recolor()
        {
            for (int i = 0; i < labels.Count; i++) labels[i].TextColor = i == shown ? Ink : Text;
        }

        public void Stop()
        {
            colorTimer?.Stop();
        }
    }
}
