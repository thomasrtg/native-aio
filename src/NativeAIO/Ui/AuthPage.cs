// Écrans de connexion repris de la version web (pages/sign-in.tsx, pages/servers.tsx ; module 15,
// demande de l'auteur : « les écrans de l'app web tout simplement »), agrandis d'environ 1,5 et
// arrondis aux tailles de la charte (carte de 384 px du web → 576 px).
// - Fond du thème et halo indigo du web en haut (Backdrop : radial-gradient(ellipse at top,
//   rgba(99,102,241,.18), transparent 60%)).
// - Logo d'AIOStreams, puis une carte (AUTH_CARD : arrondi 16 → 24 px, bordure blanche à 10 %,
//   fond presque noir) ou le contenu sans carte (« Qui regarde ? »), le tout centré.
// - Arrêts parcourus avec Haut/Bas : champ, bouton blanc, rangée de liens (Gauche/Droite), élément
//   propre à l'écran (Custom). Saisie au clavier de la TV comme l'ancien Ui/Form (LECONS.md) : OK
//   ouvre le clavier ; Retour, OK, Haut ou Bas le referment ; « Terminé » passe à l'arrêt suivant.
using System;
using System.Collections.Generic;
using System.Threading;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class AuthPage
    {
        public enum Kind { Url, Text, Password }

        public const int LogoD = 96, CardW = 576, Pad = 36, Inner = CardW - 2 * Pad, Wide = 1152;
        const int LogoGap = 48, CardGap = 24, PageGap = 48, HelpGap = 6, CardR = 24, Border = 2;
        const int FieldH = 60, FieldR = 12, LabelH = 28, LabelGap = 10, LineH = 24;
        const int ButtonH = Theme.ButtonHeight, PillH = Theme.PillHeight, PillPad = 18, PillGap = 12;

        // Couleurs du web, en neutre (un gris teinté de bleu paraît bleu sur la TV, LECONS.md).
        public static readonly Color Error = Theme.Hex(0xFCA5A5);        // red-300 du web
        static readonly Color Required = Theme.Hex(0xEF4444);            // astérisque d'un champ
        static readonly Color CardFill = Theme.Hex(0x0B0B0B), CardBorder = Theme.Hex(0x262626);
        static readonly Color FieldFill = Theme.Hex(0x141414), FieldBorder = Theme.Hex(0x303030);
        static readonly Color PillFill = Theme.White(0.12f);
        static readonly Color Glow = new Color(99 / 255f, 102 / 255f, 241 / 255f, 1f);

        internal enum Type { Title, Help, Field, Status, Button, Links, Custom }

        public class Item
        {
            internal Type Type;
            internal View View;
            internal int Height, Width, GapBefore = -1;
            internal bool Shown = true;
            internal TextLabel Text;          // titre, aide, message, bouton, nom du champ
            internal Func<string> Make;       // texte relu à chaque changement de langue
            internal View FieldBorder, Fill;  // champ
            internal TextField Field;
            internal Func<string> Placeholder;
            internal View Box;                // bouton
            internal ImageView Ring;
            internal Action Run;
            internal List<Link> Links;        // rangée de liens
            internal Func<string, bool> Key;  // élément propre à l'écran
            internal Action<bool> Focus;
            internal bool StatusError;
        }

        internal class Link
        {
            public View Box;
            public TextLabel Label;
            public Func<string> Text;
            public Action Run;
            public bool Shown = true;
        }

        readonly bool card;
        readonly List<Item> items = new List<Item>();
        readonly View cardBorder, cardFill, body;
        readonly ImageView logo;
        Animation appear; // gardée dans un champ
        int focus = -1, linkFocus;
        bool editing;

        public View View { get; } = new View { Size = new Size(1920, 1080) };
        public bool Editing => editing;

        // Fond commun : couleur du thème et halo indigo du web en haut, en ellipse (transparent à
        // 60 % de la hauteur et à ±35 % de la largeur, d'après les captures du web).
        public static View Backdrop()
        {
            var root = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.Background };
            root.Add(Gradient.Radial(new Position(288, 0), new Size(1344, 1296), new Vector2(0f, -0.5f), 0.5f, Glow, t => 0.18f * (1f - t)));
            return root;
        }

        public AuthPage(bool card = true)
        {
            this.card = card;
            logo = new ImageView
            {
                ResourceUrl = Tizen.Applications.Application.Current.DirectoryInfo.Resource + "images/aiostreams.png",
                Size = new Size(LogoD, LogoD),
                DesiredWidth = LogoD,
                DesiredHeight = LogoD,
            };
            View.Add(logo);
            if (card)
            {
                cardBorder = new View { CornerRadius = CardR, BackgroundColor = CardBorder };
                cardFill = new View { Position = new Position(Border, Border), CornerRadius = CardR - Border, BackgroundColor = CardFill };
                cardBorder.Add(cardFill);
                View.Add(cardBorder);
            }
            body = new View();
            View.Add(body);
        }

        int W => card ? Inner : Wide;
        int StdX => card ? 0 : (1920 - Wide) / 2;

        Item Append(Item it)
        {
            items.Add(it);
            body.Add(it.View);
            return it;
        }

        static TextLabel Label(float size, Color color, bool center)
        {
            return new TextLabel
            {
                PointSize = size,
                TextColor = color,
                HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Begin,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        // Titre de la carte (30 pt) ou de l'écran sans carte (36 pt, text-2xl du web).
        public Item AddTitle(Func<string> text)
        {
            float size = card ? Theme.SizeTitle : 36;
            TextLabel l = Label(size, Color.White, true);
            l.FontStyle = Theme.SemiBold();
            l.Size = new Size(W, card ? 44 : 52);
            return Append(new Item { Type = Type.Title, View = l, Text = l, Make = text, Height = card ? 44 : 52 });
        }

        // Explication grise sous le titre, sur plusieurs lignes, collée au titre (gap-1 du web).
        public Item AddHelp(Func<string> text)
        {
            TextLabel l = Label(Theme.SizeBody, Theme.Grey, true);
            l.MultiLine = true;
            l.VerticalAlignment = VerticalAlignment.Top;
            l.Size = new Size(W, LineH);
            return Append(new Item { Type = Type.Help, View = l, Text = l, Make = text, Height = LineH, GapBefore = HelpGap });
        }

        // Champ du web : nom au-dessus avec l'astérisque rouge, cadre arrondi à bordure fine ;
        // regardé = bordure à l'accent du thème (focus:border-brand du web), plus épaisse.
        public TextField AddField(Func<string> label, Func<string> placeholder, string text, Kind kind)
        {
            var it = new Item { Type = Type.Field, Make = label, Placeholder = placeholder, Height = LabelH + LabelGap + FieldH };
            it.View = new View { Size = new Size(W, it.Height) };
            it.Text = Label(Theme.SizeBody, Theme.White(0.9f), false);
            it.Text.FontStyle = Theme.SemiBold();
            it.Text.EnableMarkup = true;
            it.Text.Size = new Size(W, LabelH);
            it.FieldBorder = new View { Position = new Position(0, LabelH + LabelGap), Size = new Size(W, FieldH), CornerRadius = FieldR, BackgroundColor = FieldBorder };
            it.Fill = new View { CornerRadius = FieldR - Border, BackgroundColor = FieldFill };
            var f = new TextField
            {
                Position = new Position(18 - Border, 0),
                Size = new Size(W - 36, FieldH - 2 * Border),
                PointSize = Theme.SizeBody,
                TextColor = Color.White,
                PlaceholderTextColor = new Vector4(1f, 1f, 1f, 0.35f),
                VerticalAlignment = VerticalAlignment.Center,
                PrimaryCursorColor = new Vector4(1f, 1f, 1f, 1f),
                Focusable = true,
                Text = text ?? "",
            };
            var im = new InputMethod
            {
                PanelLayout = kind == Kind.Password ? InputMethod.PanelLayoutType.Password
                    : kind == Kind.Url ? InputMethod.PanelLayoutType.URL
                    : InputMethod.PanelLayoutType.Normal,
            };
            f.InputMethodSettings = im.OutputMap;
            if (kind == Kind.Password)
            {
                var hidden = new PropertyMap();
                hidden.Add(HiddenInputProperty.Mode, new PropertyValue((int)HiddenInputModeType.HideAll));
                f.HiddenInputSettings = hidden;
            }
            f.FocusGained += (s, e) => { editing = true; Paint(); };
            f.FocusLost += (s, e) => { editing = false; Paint(); };
            f.KeyEvent += OnFieldKey;
            it.Field = f;
            it.Fill.Add(f);
            it.FieldBorder.Add(it.Fill);
            it.View.Add(it.Text);
            it.View.Add(it.FieldBorder);
            Append(it);
            return f;
        }

        // Ligne de message (ErrorLine du web) : rouge clair pour une erreur, gris sinon ; cachée vide.
        public Item AddStatus()
        {
            TextLabel l = Label(Theme.SizeBody, Error, true);
            l.MultiLine = true;
            l.VerticalAlignment = VerticalAlignment.Top;
            l.Size = new Size(W, LineH);
            return Append(new Item { Type = Type.Status, View = l, Text = l, Height = LineH, Shown = false });
        }

        public void SetStatus(Item it, Func<string> make, bool error)
        {
            it.Make = make;
            it.StatusError = error;
            it.Text.Text = make?.Invoke() ?? "";
            it.Text.TextColor = error ? Error : Theme.Grey;
            it.Shown = it.Text.Text.Length > 0;
            Layout();
        }

        // Bouton blanc sur toute la largeur (intent="white", rounded-full) ; regardé = contour creux.
        public Item AddButton(Func<string> text, Action run)
        {
            var it = new Item { Type = Type.Button, Make = text, Run = run, Height = ButtonH };
            int w = card ? Inner : 560;
            it.Width = w;
            it.View = new View { Size = new Size(w, ButtonH) };
            it.Ring = Theme.HollowRing(w, ButtonH, ButtonH / 2);
            it.Ring.Position = new Position(-Theme.RingOut, -Theme.RingOut);
            it.Ring.Hide();
            it.Box = new View { Size = new Size(w, ButtonH), CornerRadius = ButtonH / 2, BackgroundColor = Color.White };
            it.Text = Label(Theme.SizeBody, Theme.Ink, true);
            it.Text.Size = new Size(w, ButtonH);
            it.Box.Add(it.Text);
            it.View.Add(it.Ring);
            it.View.Add(it.Box);
            return Append(it);
        }

        // Rangée de liens en capsules grises (LinkRow du web) ; regardé = fond blanc.
        public Item AddLinks(params (Func<string> Text, Action Run)[] links)
        {
            var it = new Item { Type = Type.Links, Height = PillH, Links = new List<Link>() };
            it.View = new View { Size = new Size(W, PillH) };
            foreach (var (text, run) in links)
            {
                var l = new Link { Text = text, Run = run, Box = new View { CornerRadius = PillH / 2 } };
                l.Label = Label(Theme.SizeBody, Color.White, true);
                l.Label.FontStyle = Theme.SemiBold();
                l.Box.Add(l.Label);
                it.View.Add(l.Box);
                it.Links.Add(l);
            }
            return Append(it);
        }

        public void ShowLink(Item it, int index, bool shown)
        {
            it.Links[index].Shown = shown;
            it.Shown = it.Links.Exists(l => l.Shown);
            if (focus >= 0 && items[focus] == it && !it.Links[linkFocus].Shown) linkFocus = FirstLink(it);
            Layout();
        }

        // Élément propre à l'écran (grille des utilisateurs, code de Quick Connect), centré.
        // key : touches reçues quand il a le focus (faux : la page s'en occupe) ; null = pas un arrêt.
        public Item AddCustom(View view, int width, int height, Func<string, bool> key = null, Action<bool> focused = null)
        {
            return Append(new Item { Type = Type.Custom, View = view, Width = width, Height = height, Key = key, Focus = focused });
        }

        public void Show(Item it, bool shown)
        {
            it.Shown = shown;
            if (!shown && focus >= 0 && items[focus] == it) { focus = -1; Move(1); }
            Layout();
        }

        public void Focus(Item it)
        {
            int i = items.IndexOf(it);
            if (i < 0 || !IsStop(it)) return;
            focus = i;
            if (it.Type == Type.Links) linkFocus = FirstLink(it);
            Paint();
        }

        public void Focus(TextField field) => Focus(items.Find(x => x.Field == field));

        // Apparition en fondu de 0,3 s (le web : fondu et légère montée).
        public void Appear()
        {
            View.Opacity = 0f;
            appear = new Animation(Theme.Fade);
            appear.AnimateTo(View, "Opacity", 1f);
            appear.Play();
        }

        public void ApplyTexts()
        {
            foreach (Item it in items)
            {
                switch (it.Type)
                {
                    case Type.Field:
                        it.Text.Text = Escape(it.Make()) + " <color value='#EF4444'>*</color>";
                        it.Field.PlaceholderText = it.Placeholder?.Invoke() ?? "";
                        break;
                    case Type.Links:
                        foreach (Link l in it.Links) l.Label.Text = l.Text();
                        break;
                    case Type.Custom:
                        break;
                    case Type.Status:
                        it.Text.Text = it.Make?.Invoke() ?? "";
                        it.Shown = it.Text.Text.Length > 0;
                        break;
                    default:
                        it.Text.Text = it.Make?.Invoke() ?? "";
                        break;
                }
            }
            Layout();
        }

        static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // Place tout : éléments empilés, le bloc logo + carte centré dans l'écran.
        public void Layout()
        {
            int y = 0;
            bool first = true;
            foreach (Item it in items)
            {
                if (!it.Shown) { it.View.Hide(); continue; }
                it.View.Show();
                if (it.Type == Type.Help || it.Type == Type.Status)
                {
                    // Nombre de lignes lu sur la TV ; à défaut, estimé (18 pt : ~9,2 px par caractère, LECONS.md).
                    int lines = it.Text.Text.Length == 0 ? 1 : it.Text.LineCount;
                    if (lines <= 0) lines = Math.Max(1, (int)Math.Ceiling(it.Text.Text.Length * 9.2 / W));
                    it.Height = lines * LineH;
                    it.Text.Size = new Size(W, it.Height);
                }
                if (!first) y += it.GapBefore >= 0 ? it.GapBefore : card ? CardGap : PageGap;
                first = false;
                int x = it.Type == Type.Custom || it.Type == Type.Button ? (card ? (Inner - it.Width) / 2 : (1920 - it.Width) / 2) : StdX;
                it.View.Position = new Position(x, y);
                if (it.Type == Type.Links) PlaceLinks(it);
                y += it.Height;
            }
            int content = y;
            int total = LogoD + LogoGap + content + (card ? 2 * Pad : 0);
            int top = Math.Max(40, (1080 - total) / 2);
            logo.Position = new Position((1920 - LogoD) / 2, top);
            int below = top + LogoD + LogoGap;
            if (card)
            {
                int h = content + 2 * Pad;
                cardBorder.Position = new Position((1920 - CardW) / 2, below);
                cardBorder.Size = new Size(CardW, h);
                cardFill.Size = new Size(CardW - 2 * Border, h - 2 * Border);
                body.Position = new Position((1920 - CardW) / 2 + Pad, below + Pad);
            }
            else body.Position = new Position(0, below);
            Paint();
        }

        void PlaceLinks(Item it)
        {
            var widths = new List<int>();
            int total = 0;
            foreach (Link l in it.Links)
            {
                if (!l.Shown) { l.Box.Hide(); widths.Add(0); continue; }
                int w = l.Label.NaturalSize2D.Width + 2 * PillPad;
                widths.Add(w);
                total += w + (total > 0 ? PillGap : 0);
            }
            int x = (W - total) / 2;
            for (int i = 0; i < it.Links.Count; i++)
            {
                Link l = it.Links[i];
                if (!l.Shown) continue;
                l.Box.Show();
                l.Box.Position = new Position(x, 0);
                l.Box.Size = new Size(widths[i], PillH);
                l.Label.Size = new Size(widths[i], PillH);
                x += widths[i] + PillGap;
            }
        }

        static bool IsStop(Item it) => it.Shown && (it.Type == Type.Field || it.Type == Type.Button || it.Type == Type.Links
            || (it.Type == Type.Custom && it.Key != null));

        static int FirstLink(Item it)
        {
            int i = it.Links.FindIndex(l => l.Shown);
            return i < 0 ? 0 : i;
        }

        public bool OnKey(string key)
        {
            bool ok = IsOk(key);
            if (editing)
            {
                // Touches que le clavier de la TV n'a pas gardées : on sort de la saisie.
                if (key == "Up" || key == "Down" || ok)
                {
                    StopEditing();
                    if (key != "Up") Move(1);
                    return true;
                }
                return false;
            }
            if (focus < 0 || !IsStop(items[focus])) Move(1);
            if (focus < 0) return false;
            Item it = items[focus];
            if (it.Type == Type.Custom && it.Key(key)) return true;
            switch (key)
            {
                case "Up": Move(-1); return true;
                case "Down": Move(1); return true;
                case "Left":
                case "Right":
                    if (it.Type != Type.Links) return false;
                    int step = key == "Left" ? -1 : 1;
                    for (int i = linkFocus + step; i >= 0 && i < it.Links.Count; i += step)
                        if (it.Links[i].Shown) { linkFocus = i; Paint(); break; }
                    return true;
            }
            if (!ok) return false;
            if (it.Type == Type.Field) StartEditing(it.Field);
            else if (it.Type == Type.Button) it.Run();
            else if (it.Type == Type.Links) it.Links[linkFocus].Run();
            return true;
        }

        public bool OnBack()
        {
            if (!editing) return false;
            StopEditing();
            return true;
        }

        // À appeler avant de quitter l'écran : referme le clavier s'il est ouvert.
        public void Stop()
        {
            if (editing) StopEditing();
        }

        static bool IsOk(string key) => key == "Return" || key == "KP_Enter" || key == "Select";

        void Move(int step)
        {
            int i = focus + step;
            if (focus < 0) i = step > 0 ? 0 : items.Count - 1;
            while (i >= 0 && i < items.Count && !IsStop(items[i])) i += step;
            if (i < 0 || i >= items.Count) return;
            focus = i;
            if (items[i].Type == Type.Links) linkFocus = FirstLink(items[i]);
            Paint();
        }

        void Paint()
        {
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                bool on = i == focus && it.Shown;
                switch (it.Type)
                {
                    case Type.Field:
                        int b = on ? 3 : Border;
                        it.FieldBorder.BackgroundColor = on ? Theme.Accent : FieldBorder;
                        it.Fill.Position = new Position(b, b);
                        it.Fill.Size = new Size(W - 2 * b, FieldH - 2 * b);
                        it.Fill.CornerRadius = FieldR - b;
                        it.Field.PositionX = 18 - b;
                        break;
                    case Type.Button:
                        if (on && !editing) it.Ring.Show(); else it.Ring.Hide();
                        break;
                    case Type.Links:
                        for (int k = 0; k < it.Links.Count; k++)
                        {
                            bool lit = on && !editing && k == linkFocus;
                            it.Links[k].Box.BackgroundColor = lit ? Color.White : PillFill;
                            it.Links[k].Label.TextColor = lit ? Theme.Ink : Color.White;
                        }
                        break;
                    case Type.Custom:
                        it.Focus?.Invoke(on);
                        break;
                }
            }
        }

        void StartEditing(TextField f)
        {
            FocusManager.Instance.SetCurrentFocusView(f);
            editing = true;
            Paint();
            // Le clavier se ferme (« Terminé ») : on quitte la saisie et on passe à l'arrêt suivant.
            try
            {
                InputMethodContext imc = f.GetInputMethodContext();
                if (imc != null && !hooked.Contains(imc))
                {
                    hooked.Add(imc);
                    imc.StatusChanged += (s, e) =>
                    {
                        App.Current.NoteKey(e.StatusChanged ? "clavier ouvert" : "clavier fermé");
                        if (!e.StatusChanged) Later(() => { if (editing) { StopEditing(); Move(1); } });
                    };
                }
            }
            catch (Exception) { }
        }

        readonly List<InputMethodContext> hooked = new List<InputMethodContext>();

        void StopEditing()
        {
            FocusManager.Instance.ClearFocus();
            editing = false;
            Paint();
        }

        // Touche reçue par le champ lui-même pendant la saisie (touche « Terminé », LECONS.md).
        bool OnFieldKey(object sender, View.KeyEventArgs e)
        {
            if (e.Key.State != Key.StateType.Down) return false;
            string key = e.Key.KeyPressedName;
            App.Current.NoteKey("champ " + key);
            if (!IsOk(key)) return false;
            Later(() => { if (editing) { StopEditing(); Move(1); } });
            return true;
        }

        // Après le traitement en cours : ne pas retirer le focus pendant un évènement du champ.
        static void Later(Action a)
        {
            SynchronizationContext ctx = SynchronizationContext.Current;
            if (ctx != null) ctx.Post(_ => a(), null);
            else a();
        }
    }
}
