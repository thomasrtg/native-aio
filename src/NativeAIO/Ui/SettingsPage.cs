// Une page des Paramètres, à la présentation de la version web (module 13c, voir
// docs/PARAMETRES.md) : en-tête (icône dans un carré teinté de l'accent, titre,
// description), puis des sections (titre, précision grise, carte sombre aux lignes
// séparées). Une ligne : libellé, aide grise dessous, et à droite un interrupteur, une
// valeur à choisir, un bouton ou une simple information.
// La page entière est une vue qui défile (PositionY animée) pour garder la ligne
// regardée à l'écran. Le fond blanc de la ligne regardée glisse sous les cartes.
// Les hauteurs des lignes dépendent de la longueur de l'aide, estimée (lire LineCount
// forcerait la mise en page, voir LECONS.md) ; tout est replacé au changement de langue.
// Listes (langues audio) : une ligne d'en-tête (Info) suivie de lignes « jointes », sans
// séparateur et plus basses ; une ligne peut être cachée (Visible) et des lignes insérées
// (InsertBefore) ; Update() replace tout après un changement de la liste.
using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class SettingsPage
    {
        public enum Kind { Toggle, Choice, Button, Info, Soon }

        public class Row
        {
            public Kind Kind;
            public Func<string> Label, Help, Value;
            public Func<bool> On;     // interrupteur
            public Action Run;        // OK (interrupteur, bouton, choix)
            public View Box, Track, Knob, Pill;
            public TextLabel Name, HelpLabel, ValueLabel, Chevron;
            public float Y, Height;   // dans la carte
            public Section Section;
            public Func<bool> Visible; // null : toujours montrée
            public bool Joined;        // suite de la ligne du dessus (liste), sans séparateur
            public bool Shown = true;  // Visible évalué au dernier placement
            public View Extra;         // contenu propre (aperçu), à la place du libellé
            public View Lead;          // vue à gauche du libellé (photo du compte)
            public Swatch Swatch;      // pastille de couleur dans la valeur (page Thème)
            public float ExtraH;
            public bool Focusable => Kind != Kind.Info && Shown;
        }

        public class Section
        {
            public Func<string> Title, Precision;
            public TextLabel TitleLabel, PrecisionLabel;
            public View Card;
            public readonly List<View> Separators = new List<View>();
            public readonly List<Row> Rows = new List<Row>();
        }

        public const int Width = 1360;
        const int Pad = 24, ControlW = 420, PillH = 44, TrackW = 56, TrackH = 32, Knob = 24;
        const int HeaderH = 56, HeaderGap = 40, SectionGap = 40, SectionTitleH = 30, PrecisionH = 24;
        const int LabelH = 28, HelpLine = 22, RowPad = 18, MinRowH = 72, JoinedH = 56;
        const float HelpCharPx = 6.8f; // 15 pt : ~6,2 px par caractère mesurés sur la capture de la sonde
        const int RevealMargin = 120;  // la ligne regardée reste à cette distance des bords, la suivante se voit
        const float SlideMs = 200;

        public View View { get; } = new View();
        public readonly List<Row> Rows = new List<Row>();
        readonly List<Section> sections = new List<Section>();
        readonly Highlight light = new Highlight(0, Width, 12);
        readonly Func<string> title, description;
        TextLabel titleLabel, descLabel;
        View iconBox;
        ImageView icon;
        Animation scroll;
        public float Height { get; private set; }

        public int Focus = -1;   // ligne regardée (index dans Rows)
        int painted = -1;        // ligne écrite en foncé (rejoint Focus à mi-glissement)
        bool active;             // la page a le focus

        public SettingsPage(string icon, Func<string> title, Func<string> description)
        {
            this.title = title;
            this.description = description;
            View.Size = new Size(Width, 0);
            View.Add(light.View); // sous les cartes
            light.View.Hide();
            iconBox = new View { Size = new Size(HeaderH, HeaderH), CornerRadius = 12, BackgroundColor = Theme.AccentA(0.15f) };
            // Icône blanche teintée par la couleur de mélange.
            this.icon = new ImageView { Position = new Position(12, 12), Size = new Size(32, 32), ResourceUrl = Theme.IconPath("set_" + icon + "_big"), Color = Theme.Accent };
            iconBox.Add(this.icon);
            View.Add(iconBox);
            titleLabel = new TextLabel { Position = new Position(HeaderH + 16, 0), Size = new Size(Width - HeaderH - 16, 32), PointSize = 22, TextColor = Color.White };
            descLabel = new TextLabel { Position = new Position(HeaderH + 16, 32), Size = new Size(Width - HeaderH - 16, 24), PointSize = 15, TextColor = Theme.Grey, Ellipsis = true };
            View.Add(titleLabel);
            View.Add(descLabel);
        }

        // ---- Construction ----

        public void AddSection(Func<string> title, Func<string> precision)
        {
            var s = new Section { Title = title, Precision = precision };
            if (title != null) { s.TitleLabel = new TextLabel { Size = new Size(Width, SectionTitleH), PointSize = 18, TextColor = Color.White }; View.Add(s.TitleLabel); }
            if (precision != null) { s.PrecisionLabel = new TextLabel { Size = new Size(Width, PrecisionH), PointSize = 15, TextColor = Theme.Grey, Ellipsis = true }; View.Add(s.PrecisionLabel); }
            s.Card = new View { Size = new Size(Width, 0), CornerRadius = 12, BackgroundColor = Theme.White(0.04f) };
            View.Add(s.Card);
            sections.Add(s);
        }

        public Row Toggle(Func<string> label, Func<string> help, Func<bool> on, Action toggle) =>
            Add(new Row { Kind = Kind.Toggle, Label = label, Help = help, On = on, Run = toggle });

        public Row Choice(Func<string> label, Func<string> help, Func<string> value, Action open) =>
            Add(new Row { Kind = Kind.Choice, Label = label, Help = help, Value = value, Run = open });

        public Row Button(Func<string> label, Func<string> help, Func<string> button, Action run) =>
            Add(new Row { Kind = Kind.Button, Label = label, Help = help, Value = button, Run = run });

        public Row Info(Func<string> label, Func<string> help, Func<string> value = null) =>
            Add(new Row { Kind = Kind.Info, Label = label, Help = help, Value = value });

        // Réglage pas encore branché (étape 3 du module 13c) : pâli, « Bientôt » à droite.
        public Row Soon(Func<string> label, Func<string> help = null) =>
            Add(new Row { Kind = Kind.Soon, Label = label, Help = help, Value = () => Lang.T("set.soon") });

        // Contenu propre, sur toute la largeur de la carte, non sélectionnable (aperçu).
        public Row Custom(View content, float height) =>
            Add(new Row { Kind = Kind.Info, Label = () => "", Extra = content, ExtraH = height });

        // Vue posée à gauche du libellé, centrée en hauteur (photo du compte) ; le texte se décale.
        public void SetLead(Row r, View v)
        {
            float w = v.SizeWidth + 20;
            r.Lead = v;
            v.PositionX = Pad;
            r.Box.Add(v);
            r.Name.PositionX += w;
            r.Name.SizeWidth -= w;
            if (r.HelpLabel != null) { r.HelpLabel.PositionX += w; r.HelpLabel.SizeWidth -= w; }
            Layout();
        }

        // Pastille de couleur au début de la valeur d'une ligne à choix ; le texte se décale.
        public void SetSwatch(Row r, Swatch sw)
        {
            r.Swatch = sw;
            sw.View.Position = new Position(10, (PillH - sw.View.SizeHeight) / 2);
            r.Pill.Add(sw.View);
            float x = 10 + sw.View.SizeWidth + 12;
            r.ValueLabel.SizeWidth -= x - r.ValueLabel.PositionX;
            r.ValueLabel.PositionX = x;
        }

        // Thème changé : en-tête et interrupteurs repeints.
        public void ApplyTheme()
        {
            iconBox.BackgroundColor = Theme.AccentA(0.15f);
            icon.Color = Theme.Accent;
            Paint();
        }

        public Row Add(Row r)
        {
            Section s = sections[sections.Count - 1];
            Make(r, s);
            s.Rows.Add(r);
            Rows.Add(r);
            return r;
        }

        // Ligne ajoutée juste avant une autre de la même section (liste qui grandit).
        public Row InsertBefore(Row before, Row r)
        {
            Section s = before.Section;
            Make(r, s);
            s.Rows.Insert(s.Rows.IndexOf(before), r);
            int at = Rows.IndexOf(before);
            Rows.Insert(at, r);
            if (Focus >= at) Focus++;
            if (painted >= at) painted++;
            return r;
        }

        // Vues d'une ligne ; un séparateur de plus dans la carte (placés dans l'ordre par Layout).
        void Make(Row r, Section s)
        {
            r.Section = s;
            {
                var sep = new View { Size = new Size(Width, 2), BackgroundColor = Theme.White(0.08f) };
                s.Card.Add(sep);
                s.Separators.Add(sep);
            }
            int textW = Width - 2 * Pad - (r.Value != null || r.Kind == Kind.Toggle ? ControlW + Pad : 0);
            r.Box = new View { Size = new Size(Width, MinRowH) };
            r.Name = new TextLabel { Position = new Position(Pad, RowPad), Size = new Size(textW, LabelH), PointSize = 18, Ellipsis = true };
            r.Box.Add(r.Name);
            if (r.Help != null)
            {
                r.HelpLabel = new TextLabel { Position = new Position(Pad, RowPad + LabelH + 2), Size = new Size(textW, HelpLine), PointSize = 15, MultiLine = true, Ellipsis = true };
                r.Box.Add(r.HelpLabel);
            }
            switch (r.Kind)
            {
                case Kind.Toggle:
                    r.Track = new View { Size = new Size(TrackW, TrackH), CornerRadius = TrackH / 2 };
                    r.Knob = new View { Position = new Position(4, 4), Size = new Size(Knob, Knob), CornerRadius = Knob / 2, BackgroundColor = Color.White };
                    r.Track.Add(r.Knob);
                    r.Box.Add(r.Track);
                    break;
                case Kind.Choice when r.Value == null:
                    // Ligne d'une liste : OK ouvre ses actions, seulement « › » à droite.
                    r.Chevron = new TextLabel { Text = "›", Size = new Size(24, LabelH), PointSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    r.Box.Add(r.Chevron);
                    break;
                case Kind.Choice:
                    r.Pill = new View { Size = new Size(ControlW, PillH), CornerRadius = PillH / 2 };
                    r.ValueLabel = new TextLabel { Position = new Position(20, 0), Size = new Size(ControlW - 64, PillH), PointSize = 18, VerticalAlignment = VerticalAlignment.Center, Ellipsis = true };
                    r.Chevron = new TextLabel { Text = "›", Position = new Position(ControlW - 40, 0), Size = new Size(24, PillH), PointSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    r.Pill.Add(r.ValueLabel);
                    r.Pill.Add(r.Chevron);
                    r.Box.Add(r.Pill);
                    break;
                case Kind.Button:
                    r.Pill = new View { Size = new Size(ControlW, PillH), CornerRadius = PillH / 2 };
                    r.ValueLabel = new TextLabel { Size = new Size(ControlW, PillH), PointSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Ellipsis = true };
                    r.Pill.Add(r.ValueLabel);
                    r.Box.Add(r.Pill);
                    break;
                default:
                    if (r.Value != null)
                    {
                        r.ValueLabel = new TextLabel { Size = new Size(ControlW, LabelH), PointSize = 18, HorizontalAlignment = HorizontalAlignment.End, Ellipsis = true };
                        r.Box.Add(r.ValueLabel);
                    }
                    break;
            }
            if (r.Extra != null) r.Box.Add(r.Extra);
            s.Card.Add(r.Box);
        }

        // ---- Textes et placement ----

        public void ApplyTexts()
        {
            titleLabel.Text = title();
            descLabel.Text = description();
            foreach (Section s in sections)
            {
                if (s.TitleLabel != null) s.TitleLabel.Text = s.Title();
                if (s.PrecisionLabel != null) s.PrecisionLabel.Text = s.Precision();
            }
            foreach (Row r in Rows) SetTexts(r);
            Layout();
            Paint(); // sinon une page jamais visitée garde le texte noir par défaut (vu par l'auteur)
        }

        // Textes qui changent sans changement de langue (utilisateur, serveur, message).
        public void Refresh(Row r)
        {
            float h = r.Height;
            SetTexts(r);
            if (RowHeight(r) != h) Layout();
        }

        void SetTexts(Row r)
        {
            r.Name.Text = r.Label();
            if (r.HelpLabel != null) r.HelpLabel.Text = r.Help() ?? "";
            if (r.ValueLabel != null) r.ValueLabel.Text = r.Value() ?? "";
            if (r.Kind == Kind.Toggle) PaintToggle(r, r == Painted && active);
        }

        static int HelpLines(Row r)
        {
            if (r.HelpLabel == null) return 0;
            string t = r.HelpLabel.Text;
            if (string.IsNullOrEmpty(t)) return 0;
            int perLine = Math.Max(1, (int)(r.HelpLabel.SizeWidth / HelpCharPx));
            return Math.Min(4, (t.Length + perLine - 1) / perLine);
        }

        static float RowHeight(Row r)
        {
            if (r.Extra != null) return r.ExtraH;
            int lines = HelpLines(r);
            if (r.Joined && lines == 0) return JoinedH;
            return Math.Max(MinRowH, RowPad + LabelH + (lines > 0 ? 2 + lines * HelpLine : 0) + RowPad);
        }

        void Layout()
        {
            float y = HeaderH + HeaderGap;
            foreach (Section s in sections)
            {
                if (s.TitleLabel != null) { s.TitleLabel.PositionY = y; y += SectionTitleH; }
                if (s.PrecisionLabel != null) { s.PrecisionLabel.PositionY = y; y += PrecisionH; }
                if (s.TitleLabel != null || s.PrecisionLabel != null) y += 12;
                float ry = 0;
                int seps = 0;
                bool first = true;
                foreach (Row r in s.Rows)
                {
                    r.Shown = r.Visible == null || r.Visible();
                    if (!r.Shown) { r.Box.Hide(); continue; }
                    r.Box.Show();
                    if (!first && !r.Joined) { View sep = s.Separators[seps++]; sep.Show(); sep.PositionY = ry; ry += 2; }
                    first = false;
                    r.Y = ry;
                    r.Height = RowHeight(r);
                    r.Box.PositionY = ry;
                    r.Box.SizeHeight = r.Height;
                    r.Name.PositionY = r.Joined && HelpLines(r) == 0 ? (JoinedH - LabelH) / 2 : RowPad;
                    if (r.Chevron != null && r.Pill == null) r.Chevron.Position = new Position(Width - Pad - 24, (r.Height - LabelH) / 2);
                    if (r.HelpLabel != null) r.HelpLabel.SizeHeight = Math.Max(1, HelpLines(r)) * HelpLine + 4;
                    float mid = (r.Height - PillH) / 2;
                    if (r.Lead != null) r.Lead.PositionY = (r.Height - r.Lead.SizeHeight) / 2;
                    if (r.Track != null) r.Track.Position = new Position(Width - Pad - TrackW, (r.Height - TrackH) / 2);
                    if (r.Pill != null) r.Pill.Position = new Position(Width - Pad - ControlW, mid);
                    else if (r.ValueLabel != null) r.ValueLabel.Position = new Position(Width - Pad - ControlW, RowPad);
                    ry += r.Height;
                }
                for (int k = seps; k < s.Separators.Count; k++) s.Separators[k].Hide();
                s.Card.PositionY = y;
                s.Card.SizeHeight = ry;
                y += ry + SectionGap;
            }
            Height = y - SectionGap;
            View.SizeHeight = Height;
            if (Focus >= 0 && active) light.Place(Top(Rows[Focus]), Rows[Focus].Height);
        }

        static float Top(Row r) => r.Section.Card.PositionY + r.Y;

        // ---- Focus ----

        Row Painted => painted >= 0 && painted < Rows.Count ? Rows[painted] : null;

        public bool HasFocusable => Rows.Exists(r => r.Focusable);

        // La page prend (ou perd) le focus.
        public void SetActive(bool on)
        {
            active = on;
            if (on)
            {
                if (Focus < 0) Focus = Rows.FindIndex(r => r.Focusable);
                painted = Focus;
                if (Focus >= 0) { light.Place(Top(Rows[Focus]), Rows[Focus].Height); light.View.Show(); }
            }
            else { light.Stop(); light.View.Hide(); }
            Paint();
        }

        // Haut/Bas : false si rien dans ce sens.
        public bool Move(int step, float viewTop, float viewBottom)
        {
            int i = Focus + step;
            while (i >= 0 && i < Rows.Count && !Rows[i].Focusable) i += step;
            if (i < 0 || i >= Rows.Count) return false;
            Focus = i;
            Paint();
            Row r = Rows[i];
            light.SlideTo(Top(r), r.Height, () => { painted = Focus; Paint(); });
            Reveal(viewTop, viewBottom);
            return true;
        }

        // Fait défiler la page pour montrer la ligne regardée (et l'en-tête pour la première).
        public void Reveal(float viewTop, float viewBottom)
        {
            if (Focus < 0) return;
            Row r = Rows[Focus];
            float top = viewTop + Top(r) - RevealMargin, bottom = viewTop + Top(r) + r.Height + RevealMargin;
            float now = View.PositionY - viewTop; // décalage actuel (≤ 0)
            float offset = now;
            if (Focus == Rows.FindIndex(x => x.Focusable)) offset = 0;
            else if (top + now < viewTop) offset = viewTop - top;
            else if (bottom + now > viewBottom) offset = viewBottom - bottom;
            offset = Math.Min(0, Math.Max(offset, Math.Min(0, viewBottom - viewTop - Height)));
            if (Math.Abs(offset - now) < 0.5f) return;
            SlidePage(viewTop + offset);
        }

        // Page sans ligne à choisir (À propos) : Haut/Bas la font défiler d'un pas ; false si
        // elle est déjà au bout dans ce sens (ou tient entière à l'écran).
        public bool Scroll(int step, float viewTop, float viewBottom)
        {
            float now = View.PositionY - viewTop;
            float min = Math.Min(0, viewBottom - viewTop - Height);
            float offset = Math.Min(0, Math.Max(min, now - step * ScrollStep));
            if (Math.Abs(offset - now) < 0.5f) return false;
            SlidePage(viewTop + offset);
            return true;
        }

        // La page tient-elle entière entre ces deux hauteurs de l'écran ?
        public bool Fits(float viewTop, float viewBottom) => Height <= viewBottom - viewTop;

        const float ScrollStep = 300;

        void SlidePage(float y)
        {
            float from = View.CurrentPosition.Y;
            scroll?.Stop();
            View.PositionY = from;
            scroll = new Animation((int)SlideMs);
            scroll.AnimateTo(View, "PositionY", y, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            scroll.Play();
        }

        // Haut de la ligne regardée sur l'écran, une fois le défilement fini (PositionY
        // donne la position visée par l'animation).
        public float FocusScreenTop()
        {
            return View.PositionY + Top(Rows[Focus]);
        }

        public Row Current => Focus >= 0 ? Rows[Focus] : null;

        // Après un changement qui montre, cache ou ajoute des lignes : textes, placement, et
        // focus sur la ligne la plus proche si la sienne a disparu (index voulu : keep).
        public void Update(int keep = -1)
        {
            foreach (Row r in Rows) SetTexts(r);
            Layout();
            if (keep >= 0) Focus = Math.Min(keep, Rows.Count - 1);
            if (Focus >= 0 && !Rows[Focus].Focusable)
            {
                int i = Focus;
                while (i < Rows.Count && !Rows[i].Focusable) i++;
                if (i >= Rows.Count) { i = Focus; while (i >= 0 && !Rows[i].Focusable) i--; }
                Focus = i;
            }
            painted = Focus;
            if (Focus >= 0 && active) light.Place(Top(Rows[Focus]), Rows[Focus].Height);
            Paint();
        }

        void Paint()
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                Row r = Rows[i];
                bool on = active && i == painted;
                bool soon = r.Kind == Kind.Soon;
                // Ligne « Bientôt » : libellé et aide lisibles, seul « Bientôt » est pâli.
                r.Name.TextColor = on ? Theme.Ink : Color.White;
                if (r.HelpLabel != null) r.HelpLabel.TextColor = on ? Theme.InkA(0.6f) : Theme.Grey;
                switch (r.Kind)
                {
                    case Kind.Toggle: PaintToggle(r, on); break;
                    case Kind.Choice when r.Pill != null:
                    case Kind.Button:
                        r.Pill.BackgroundColor = on ? Theme.InkA(0.08f) : Theme.White(0.08f);
                        r.ValueLabel.TextColor = on ? Theme.Ink : Theme.Text;
                        if (r.Chevron != null) r.Chevron.TextColor = on ? Theme.InkA(0.6f) : Theme.Grey;
                        r.Swatch?.OnWhite(on);
                        break;
                    case Kind.Choice:
                        r.Chevron.TextColor = on ? Theme.InkA(0.6f) : Theme.Grey;
                        break;
                    default:
                        if (r.ValueLabel != null) r.ValueLabel.TextColor = on ? Theme.InkA(soon ? 0.45f : 0.6f) : soon ? Theme.Faded : Theme.Grey;
                        break;
                }
            }
        }

        // Interrupteur : à la couleur d'accent quand il est mis, rond sombre sur un accent très clair (thème Blanc).
        static void PaintToggle(Row r, bool on)
        {
            bool v = r.On();
            // Accent très clair sur la ligne regardée (fond blanc) : piste sombre, sinon invisible.
            bool dark = v && on && Theme.LightAccent;
            r.Track.BackgroundColor = dark ? Theme.Ink : v ? Theme.Accent : on ? Theme.InkA(0.24f) : Theme.White(0.24f);
            r.Knob.BackgroundColor = v && !dark ? Theme.OnAccent : Color.White;
            r.Knob.PositionX = v ? TrackW - Knob - 4 : 4;
        }

        // Après un changement de valeur.
        public void Changed(Row r)
        {
            SetTexts(r);
            Paint();
        }
    }
}
