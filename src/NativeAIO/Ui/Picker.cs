// Boîte de choix commune (module 14, demande de l'auteur, 2026-11-19 : une seule boîte de
// dialogue partout) : liste déroulante des Paramètres, listes Catalogue et Genre de Découvrir,
// menus du lecteur (pistes, statistiques, réglages des sous-titres).
// - Fond des menus (Theme.Menu), coins concentriques autour du fond blanc de la ligne regardée.
// - Ligne regardée : fond blanc qui glisse d'une ligne à l'autre (Ui/Highlight), textes foncés.
// - Choix en cours : ✓ blanc à gauche (choix de l'auteur, comme les menus du lecteur).
// - Liste plus longue que la fenêtre : elle défile avec la même dynamique (Theme.Slide,
//   Theme.Ease) ; ▲ et ▼ à droite signalent la suite. La TV ne coupe pas ce qui dépasse : une
//   ligne qui sort de la fenêtre est cachée tout de suite, celle qui entre apparaît en fondu.
// - Lignes réutilisées (créer un texte coûte cher, LECONS.md) : seules les lignes près de la
//   fenêtre existent ; ouvrir la liste pose surtout des textes courts.
// - En option (menus du lecteur, comme le web) : intitulé gris en tête, précision grise sous le
//   nom (lignes de 64 px au lieu de 56 dans toute la boîte), › à droite d'une ligne qui ouvre un
//   autre menu, flèche vers le bas à la place du ✓ (« Autres langues »), ligne grise qu'on ne
//   regarde pas (« aucune piste »), note grise sous la liste.
// Haut/Bas, OK choisit, Retour ou Gauche ferme (OnKey). Le lecteur ne lui passe que Haut/Bas et
// traite lui-même OK, Gauche/Droite et Retour (la boîte reste ouverte, réglages, sous-menus).
// Pastilles de couleur devant les noms en option (page Thème).
// Menu d'une carte (accueil, comme le menu contextuel du web) : icône à la place du ✓, teintée
// comme le texte, et trait de séparation au-dessus d'une ligne (OpenMenu).
using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class Picker
    {
        public const int MaxItems = 10;
        const int DefaultWidth = 400, Pad = 8, Keep = 3; // Keep : lignes gardées de part et d'autre de la fenêtre
        const int LineH = 56, TwoLineH = 64, HeadH = 48;
        const int CheckX = 12, CheckW = 28, TextX = 48, SwatchD = 28, MoreW = 22, ArrowW = 20, IconD = 24;

        // Ligne d'une boîte : nom, précision grise dessous en option.
        public class Item
        {
            public string Text, Detail;
            public bool Sub;    // ouvre un autre menu : › à droite
            public bool Unfold; // déplie la suite (« Autres langues ») : flèche vers le bas à la place du ✓
            public bool Muted;  // texte gris, jamais regardée (« aucune piste »)
            public string Icon; // icône blanche (fichier) à la place du ✓
            public bool Rule;   // trait de séparation au-dessus de la ligne
        }

        class Line { public int Index = -1; public View Box, Rule; public TextLabel Label, Detail, Check, Arrow; public ImageView Icon; public Swatch Swatch; }

        public View View { get; }
        readonly View list; // les lignes, qui glissent pour défiler (le fond blanc est dedans)
        readonly Highlight light = new Highlight(Pad, DefaultWidth - 2 * Pad, Theme.RadiusLarge);
        readonly List<Line> pool = new List<Line>();
        readonly Dictionary<int, Line> lines = new Dictionary<int, Line>();
        readonly TextLabel heading, note;
        int width = DefaultWidth, count, focus, current, top, max = MaxItems, itemH = LineH, head;
        IList<Item> items;
        IList<(Color Fill, Color Dot)> swatches;
        Action<int> chosen;
        readonly TextLabel moreUp, moreDown; // ▲ ▼ : la liste continue
        Animation scroll; // gardée dans un champ, sinon elle s'arrête

        public bool IsOpen { get; private set; }
        public int Focus => focus;

        public Picker()
        {
            View = new View { Size = new Size(DefaultWidth, 0), BackgroundColor = Theme.Menu }.Rounded(Theme.RadiusLarge + Pad);
            list = new View();
            list.Add(light.View);
            View.Add(list);
            // Intitulé comme le web (DropdownMenuLabel : petit, demi-gras, gris).
            heading = new TextLabel { Position = new Position(24, 16), PointSize = Theme.SizeInfo, TextColor = Theme.Grey, FontStyle = Theme.SemiBold(), VerticalAlignment = VerticalAlignment.Center, Ellipsis = true };
            heading.Hide();
            View.Add(heading);
            note = new TextLabel { PointSize = Theme.SizeInfo, MultiLine = true, TextColor = Theme.Grey };
            note.Hide();
            View.Add(note);
            moreUp = new TextLabel { Text = "▲", PointSize = 10, TextColor = Theme.Grey, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            moreDown = new TextLabel { Text = "▼", PointSize = 10, TextColor = Theme.Grey, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            View.Add(moreUp);
            View.Add(moreDown);
            View.Hide();
        }

        int Shown => Math.Min(count, max);

        static List<Item> Plain(IList<string> options)
        {
            var l = new List<Item>(options.Count);
            foreach (string o in options) l.Add(new Item { Text = o });
            return l;
        }

        // Paramètres : bord droit de la liste ; rowTop/rowBottom : la ligne qui l'ouvre (écran).
        public void Open(IList<string> options, int selected, float right, float rowTop, float rowBottom, Action<int> onChosen,
            IList<(Color Fill, Color Dot)> swatches = null) =>
            Open(options, selected, right - DefaultWidth, DefaultWidth, rowTop, rowBottom, onChosen, swatches);

        // Liste de largeur w posée en x, sous la ligne (rowTop, rowBottom) qui l'ouvre, ou
        // au-dessus si elle dépasserait le bas de l'écran.
        public void Open(IList<string> options, int selected, float x, int w, float rowTop, float rowBottom, Action<int> onChosen,
            IList<(Color Fill, Color Dot)> swatches = null)
        {
            Fill(Plain(options), selected, selected, w, null, null, MaxItems, swatches);
            chosen = onChosen;
            float h = View.SizeHeight;
            float y = rowBottom + 8 + h <= 1040 ? rowBottom + 8 : Math.Max(40, rowTop - 8 - h);
            Show(x, y);
        }

        // Menu posé en x, son bas en bottom (au-dessus d'un bouton, comme le lecteur web).
        // selected : ligne cochée (-1 : aucune) ; wanted : ligne regardée. Rappelé pour remettre
        // la boîte à jour après un réglage (focus gardé, sans animation).
        public void OpenAbove(IList<Item> items, int selected, int wanted, float x, int w, float bottom,
            string title = null, string noteText = null, int maxItems = MaxItems)
        {
            Fill(items, selected, wanted, w, title, noteText, maxItems, null);
            chosen = null;
            Show(x, Math.Max(24, bottom - View.SizeHeight));
        }

        // Menu d'une carte (demande de l'auteur, 2026-11-21) : intitulé en tête, aucune ligne cochée,
        // à droite de la carte (cx, cy, cw, ch), à gauche s'il n'y a pas la place, centré sur sa
        // hauteur sans sortir de l'écran.
        public void OpenMenu(IList<Item> items, float cx, float cy, float cw, float ch, int w, string title, Action<int> onChosen)
        {
            const int Away = 24, Edge = 48;
            Fill(items, -1, 0, w, title, null, MaxItems, null);
            chosen = onChosen;
            float h = View.SizeHeight;
            float x = cx + cw + Away;
            if (x + w > 1920 - Edge) x = Math.Max(Edge, cx - Away - w);
            float y = cy + (ch - h) / 2;
            Show(x, Math.Max(40, Math.Min(y, 1040 - h)));
        }

        void Fill(IList<Item> items, int selected, int wanted, int w, string title, string noteText, int maxItems,
            IList<(Color Fill, Color Dot)> swatches)
        {
            ReleaseAll();
            this.items = items;
            this.swatches = swatches;
            width = w;
            max = Math.Max(1, maxItems);
            count = items.Count;
            current = selected;
            focus = Math.Max(0, Math.Min(count - 1, wanted));
            top = Math.Max(0, Math.Min(count - max, focus - max / 2));
            itemH = LineH;
            foreach (Item it in items) if (it.Detail != null) { itemH = TwoLineH; break; }
            head = title != null ? HeadH : 0;
            if (title != null) { heading.Text = title; heading.Size = new Size(width - 48, 32); heading.Show(); }
            else heading.Hide();
            float listBottom = head + Pad + Shown * itemH;
            int noteH = 0;
            if (noteText != null)
            {
                // Note en 15 pt gris : environ 8,5 px par caractère, 22 px par ligne.
                noteH = 12 + 22 * Lines(noteText, Math.Max(10, (width - 48) * 2 / 17));
                note.Text = noteText;
                note.Position = new Position(24, listBottom + 4);
                note.Size = new Size(width - 48, noteH);
                note.Show();
            }
            else note.Hide();
            View.Size = new Size(width, listBottom + Pad + noteH);
            scroll?.Stop();
            list.PositionY = head + Pad - top * itemH;
            light.View.SizeWidth = width - 2 * Pad;
            moreUp.Size = new Size(20, itemH);
            moreDown.Size = new Size(20, itemH);
            moreUp.Position = new Position(width - Pad - MoreW, head + Pad);
            moreDown.Position = new Position(width - Pad - MoreW, head + Pad + (Shown - 1) * itemH);
            light.Place(focus * itemH, itemH);
            if (count > 0 && !items[focus].Muted) light.View.Show(); else light.View.Hide();
            Ensure();
            foreach (Line l in lines.Values) l.Box.Opacity = InWindow(l.Index) ? 1f : 0f;
            Paint();
            More();
        }

        void Show(float x, float y)
        {
            View.Position = new Position(x, y);
            View.Show();
            View.RaiseToTop();
            IsOpen = true;
        }

        static int Lines(string text, int perLine)
        {
            int n = 0;
            foreach (string l in text.Split('\n')) n += Math.Max(1, (l.Length + perLine - 1) / perLine);
            return Math.Max(1, n);
        }

        public void Close()
        {
            IsOpen = false;
            light.Stop();
            scroll?.Stop();
            View.Hide();
            ReleaseAll();
        }

        bool InWindow(int i) => i >= top && i < top + max;

        // Lignes de la fenêtre et Keep de part et d'autre ; les autres retournent à la réserve.
        void Ensure()
        {
            int from = Math.Max(0, top - Keep), to = Math.Min(count, top + max + Keep);
            foreach (int k in new List<int>(lines.Keys))
                if (k < from || k >= to) Release(k);
            int rowW = width - 2 * Pad;
            for (int i = from; i < to; i++)
            {
                if (lines.ContainsKey(i)) continue;
                Item it = items[i];
                Line l = Take();
                l.Index = i;
                l.Box.PositionY = i * itemH;
                l.Box.Size = new Size(rowW, itemH);
                l.Check.SizeHeight = itemH;
                bool two = it.Detail != null;
                float textX = swatches != null ? TextX + SwatchD + 12 : TextX;
                float textW = rowW - textX - (it.Sub ? MoreW + ArrowW + 8 : 32);
                l.Label.Position = new Position(textX, two ? 8 : 0);
                l.Label.Size = new Size(textW, two ? 28 : itemH);
                l.Label.Text = it.Text;
                if (two) { l.Detail.Position = new Position(textX, 34); l.Detail.Size = new Size(textW, 24); l.Detail.Text = it.Detail; l.Detail.Show(); }
                else l.Detail.Hide();
                // « Autres langues » : flèche vers le bas à la place du ✓ (LuChevronDown du web).
                l.Check.Text = it.Unfold ? "›" : "✓";
                l.Check.Orientation = new Rotation(new Radian(it.Unfold ? (float)Math.PI / 2 : 0f), Vector3.ZAxis);
                // › à gauche de la colonne de ▲ ▼, pour ne pas les chevaucher.
                if (it.Sub) { l.Arrow.Position = new Position(rowW - MoreW - ArrowW, 0); l.Arrow.SizeHeight = itemH; l.Arrow.Show(); }
                else l.Arrow.Hide();
                if (swatches != null) { l.Swatch.View.PositionY = (itemH - SwatchD) / 2; l.Swatch.Set(swatches[i].Fill, swatches[i].Dot); l.Swatch.View.Show(); }
                else l.Swatch.View.Hide();
                if (it.Icon != null) { l.Icon.ResourceUrl = it.Icon; l.Icon.PositionY = (itemH - IconD) / 2; l.Icon.Show(); }
                else l.Icon.Hide();
                if (it.Rule) { l.Rule.SizeWidth = rowW; l.Rule.Show(); } else l.Rule.Hide();
                l.Box.Opacity = 0f; // montrée par l'appelant si elle est dans la fenêtre
                l.Box.Show();
                lines[i] = l;
            }
        }

        Line Take()
        {
            if (pool.Count > 0) { Line l = pool[pool.Count - 1]; pool.RemoveAt(pool.Count - 1); return l; }
            var n = new Line { Box = new View { PositionX = Pad } };
            n.Check = new TextLabel { Text = "✓", Position = new Position(CheckX, 0), Size = new Size(CheckW, LineH), PointSize = Theme.SizeBody, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            n.Swatch = new Swatch(SwatchD);
            n.Swatch.View.PositionX = TextX;
            n.Label = new TextLabel { PointSize = Theme.SizeBody, VerticalAlignment = VerticalAlignment.Center, Ellipsis = true };
            n.Detail = new TextLabel { PointSize = Theme.SizeInfo, Ellipsis = true };
            n.Arrow = new TextLabel { Text = "›", Size = new Size(ArrowW, LineH), PointSize = Theme.SizeBody, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            n.Icon = new ImageView { Position = new Position(CheckX + (CheckW - IconD) / 2, 0), Size = new Size(IconD, IconD) };
            n.Rule = new View { Size = new Size(0, 1), BackgroundColor = Theme.Marked }; // séparateur du web (bordure)
            n.Detail.Hide();
            n.Arrow.Hide();
            n.Icon.Hide();
            n.Rule.Hide();
            n.Box.Add(n.Rule);
            n.Box.Add(n.Icon);
            n.Box.Add(n.Check);
            n.Box.Add(n.Swatch.View);
            n.Box.Add(n.Label);
            n.Box.Add(n.Detail);
            n.Box.Add(n.Arrow);
            list.Add(n.Box);
            return n;
        }

        void Release(int i)
        {
            Line l = lines[i];
            lines.Remove(i);
            l.Index = -1;
            l.Box.Hide();
            pool.Add(l);
        }

        void ReleaseAll()
        {
            foreach (int k in new List<int>(lines.Keys)) Release(k);
        }

        bool Lit(int i) => i == focus && count > 0 && !items[focus].Muted;

        void Paint()
        {
            foreach (Line l in lines.Values)
            {
                Item it = items[l.Index];
                bool on = Lit(l.Index);
                l.Label.TextColor = on ? Theme.Ink : it.Muted ? Theme.Grey : Color.White;
                l.Check.TextColor = on ? Theme.Ink : Color.White;
                l.Detail.TextColor = on ? Theme.InkA(0.7f) : Theme.Grey;
                l.Arrow.TextColor = on ? Theme.Ink : Theme.Grey;
                l.Icon.Color = on ? Theme.Ink : Color.White; // icône blanche teinte au mélange (LECONS.md)
                if (l.Index == current || it.Unfold) l.Check.Show(); else l.Check.Hide();
                if (swatches != null) l.Swatch.OnWhite(on);
            }
            // ▲ ▼ posés sur le fond blanc quand la première ou la dernière ligne de la fenêtre est regardée.
            moreUp.TextColor = Lit(top) ? Theme.Ink : Theme.Grey;
            moreDown.TextColor = Lit(top + Shown - 1) ? Theme.Ink : Theme.Grey;
        }

        void More()
        {
            if (top > 0) moreUp.Show(); else moreUp.Hide();
            if (top + max < count) moreDown.Show(); else moreDown.Hide();
        }

        // Renvoie false pour une touche sans effet.
        public bool OnKey(string key)
        {
            switch (key)
            {
                case "Up":
                case "Down":
                    int i = focus + (key == "Up" ? -1 : 1);
                    if (i < 0 || i >= count) return false;
                    focus = i;
                    if (focus < top || focus >= top + max)
                    {
                        // La fenêtre avance d'une ligne : la liste glisse, le fond blanc la suit.
                        top = focus < top ? focus : focus - max + 1;
                        Ensure();
                        scroll?.Stop();
                        scroll = new Animation(Theme.Slide);
                        scroll.AnimateTo(list, "PositionY", (float)(head + Pad - top * itemH), Theme.Ease());
                        foreach (Line l in lines.Values)
                        {
                            if (InWindow(l.Index)) { if (l.Box.Opacity < 1f) scroll.AnimateTo(l.Box, "Opacity", 1f); }
                            else l.Box.Opacity = 0f; // sortie : cachée tout de suite (la TV ne coupe pas)
                        }
                        scroll.Play();
                        More();
                    }
                    light.SlideTo(focus * itemH, itemH, Paint);
                    return true;
                case "Return":
                case "KP_Enter":
                    int f = focus;
                    Action<int> c = chosen;
                    Close();
                    c?.Invoke(f);
                    return true;
                case "Left":
                case "XF86Back":
                    Close();
                    return true;
            }
            return false;
        }
    }
}
