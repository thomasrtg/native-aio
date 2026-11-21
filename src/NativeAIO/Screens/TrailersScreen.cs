// Choix d'une bande-annonce depuis la page d'un titre, quand il y en a plusieurs (comme
// TrailersSheet de la version web, components/trailers.tsx) : panneau à droite, la page du titre
// voilée dessous ; « BANDES-ANNONCES », le nom du titre, puis une ligne par bande-annonce avec sa
// vignette YouTube (mqdefault) et son nom.
// Fond comme les écrans : couleur de fond du thème, ou la partie de l'arrière-plan d'après
// l'image qui se trouve derrière. La TV ne coupe pas ce qui dépasse d'une zone : l'en-tête est
// posé par-dessus la liste (les lignes glissent dessous) et le panneau va jusqu'au bas de
// l'écran, qui coupe les lignes de lui-même ; plus rien n'apparaît ni ne disparaît d'un coup.
// OK ouvre la lecture à la place du panneau : Retour ramène à la page du titre.
using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Screens
{
    class TrailersScreen : Screen
    {
        const int PW = 620, PX = 1920 - PW, Pad = 32, In = 8;
        const int ThumbW = 176, ThumbH = 99, BoxW = PW - 2 * In, BoxH = ThumbH + 2 * In, RowH = BoxH + 8;
        const int ListTop = 148, ListH = 1080 - ListTop, IconSize = 26;

        readonly Item item;
        readonly List<MediaUrl> trailers;
        View root, list;
        TextLabel heading, title;
        readonly List<View> rows = new List<View>();
        readonly List<View> shades = new List<View>();
        CardFocus lit; // contour et assombrissement de la ligne regardée, qui glissent
        Animation thumbFade; // fondu des vignettes, gardé dans un champ
        int focus, scroll;
        Animation scrollAnim; // gardée dans un champ
        Guarded guard;

        public override bool Overlay => true;
        public override bool ArtBackdrop => true;
        public override bool FadeOut => true;

        public TrailersScreen(Item item, List<MediaUrl> trailers)
        {
            this.item = item;
            this.trailers = trailers;
        }

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.Veil };
            guard = new Guarded(root);
            ArtLayer.Show(item);
            root.Add(ArtLayer.Piece(PX, 0, PW, 1080, 0));
            // Liste sous l'en-tête. ClipChildren ne coupe rien sur la TV (voir plus haut).
            list = new View { Position = new Position(0, ListTop) };
            lit = new CardFocus(list, PX + In, BoxW, Theme.RadiusLarge);
            root.Add(list);
            root.Add(ArtLayer.Piece(PX, 0, PW, ListTop - In, 0));
            root.Add(new View { Position = new Position(PX, 0), Size = new Size(1, 1080), BackgroundColor = Theme.Faint });
            heading = new TextLabel { Position = new Position(PX + Pad, Theme.Top), Size = new Size(PW - 2 * Pad, 20), PointSize = Theme.SizeSmall, TextColor = Theme.Grey, FontStyle = Theme.SemiBold() };
            title = new TextLabel { Position = new Position(PX + Pad, Theme.Top + 26), Size = new Size(PW - 2 * Pad, 30), PointSize = Theme.SizeBody, TextColor = Color.White, FontStyle = Theme.SemiBold(), Ellipsis = true };
            root.Add(heading);
            root.Add(title);
            root.Add(new View { Position = new Position(PX, ListTop - 16), Size = new Size(PW, 1), BackgroundColor = Theme.Faint });
            string backdrop = Library.Backdrop(item, 320);
            for (int i = 0; i < trailers.Count; i++) AddRow(i, backdrop);
            lit.Raise();
            SetFocus(0);
            return root;
        }

        void AddRow(int i, string backdrop)
        {
            MediaUrl t = trailers[i];
            var box = new View { Position = new Position(PX + In, i * RowH), Size = new Size(BoxW, BoxH) }.Rounded(Theme.RadiusLarge);
            var thumbBack = new View { Position = new Position(In, In), Size = new Size(ThumbW, ThumbH), BackgroundColor = Theme.Placeholder }.Rounded(Theme.RadiusSmall);
            box.Add(thumbBack);
            // Ligne regardée : vignette assombrie à 30 % et triangle de lecture, comme le web.
            var shade = new View { Position = new Position(In, In), Size = new Size(ThumbW, ThumbH), BackgroundColor = new Color(0f, 0f, 0f, 0.3f) }.Rounded(Theme.RadiusSmall);
            shade.Add(new ImageView { ResourceUrl = Theme.IconPath("play_small"), Size = new Size(IconSize, IconSize), Position = new Position((ThumbW - IconSize) / 2, (ThumbH - IconSize) / 2) });
            shade.Opacity = 0f;
            box.Add(shade);
            shades.Add(shade);
            box.Add(new TextLabel
            {
                Position = new Position(In + ThumbW + 16, 0),
                Size = new Size(BoxW - ThumbW - 2 * In - 32, BoxH),
                PointSize = Theme.SizeBody,
                MultiLine = true,
                VerticalAlignment = VerticalAlignment.Center,
                TextColor = Color.White,
                FontStyle = Theme.SemiBold(),
                Text = Pictures.Cut(string.IsNullOrEmpty(t.Name) ? Lang.T("trailer.default_name") : t.Name, 70),
            });
            list.Add(box);
            rows.Add(box);
            string id = Trailers.YouTubeId(t.Url);
            string thumb = id != null ? $"https://i.ytimg.com/vi/{id}/mqdefault.jpg" : backdrop;
            if (thumb == null) return;
            ImageCache.Request(thumb, () => Closed ? int.MaxValue : Math.Abs(i - focus), path => guard.Run("Thumb", () =>
            {
                if (Closed || path == null) return;
                View v = Pictures.Cover(path, ThumbW, ThumbH, out ImageView img);
                if (v == null) return;
                img.Rounded(Theme.RadiusSmall);
                thumbBack.Add(v);
            }));
        }

        public override void ApplyTexts()
        {
            heading.Text = Lang.T("trailers.heading");
            title.Text = Library.Title(item);
        }

        public override void OnClose() => guard.Stop();

        // Ouverture en fondu de 0,2 s (l'auteur, 2026-11-18).
        Animation appear; // gardée dans un champ
        bool appeared;

        public override void OnShow()
        {
            if (appeared) return;
            appeared = true;
            root.Opacity = 0f;
            appear = new Animation(Theme.Slide);
            appear.AnimateTo(root, "Opacity", 1f);
            appear.Play();
        }

        // Ligne regardée, comme la liste des liens : assombrie de 20 %, contour blanc et liseré
        // sombre au centre transparent, qui glissent d'une ligne à l'autre (CardFocus, dynamique
        // commune, module 14) ; vignette assombrie avec ▶ en fondu de 0,2 s, l'ancienne s'éteint de même.
        void SetFocus(int i)
        {
            if (rows.Count == 0) return;
            int old = focus;
            focus = i;
            lit.To(focus * RowH, BoxH);
            thumbFade?.Stop();
            thumbFade = new Animation(Theme.Slide);
            foreach (int k in new[] { old, focus }) thumbFade.AnimateTo(shades[k], "Opacity", k == focus ? 1f : 0f);
            thumbFade.Play();
            int target = scroll;
            if ((focus + 1) * RowH - target > ListH) target = (focus + 1) * RowH - ListH;
            if (focus * RowH < target) target = focus * RowH;
            if (target == scroll) return;
            scroll = target;
            scrollAnim?.Stop();
            scrollAnim = new Animation(Theme.Slide);
            scrollAnim.AnimateTo(list, "PositionY", (float)(ListTop - scroll), Theme.Ease());
            scrollAnim.Play();
        }

        public override bool OnKey(string key)
        {
            bool done = true;
            guard.Run("Key " + key, () => done = Keys(key));
            return done;
        }

        bool Keys(string key)
        {
            switch (key)
            {
                case "Up": if (focus > 0) SetFocus(focus - 1); else App.Current.KeyIgnored(); return true;
                case "Down": if (focus < rows.Count - 1) SetFocus(focus + 1); else App.Current.KeyIgnored(); return true;
                case "Return":
                case "KP_Enter":
                    Nav.Replace(new TrailerScreen(item, trailers[focus]));
                    return true;
            }
            return false;
        }
    }
}
