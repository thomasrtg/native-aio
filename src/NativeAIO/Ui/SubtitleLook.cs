// Sous-titre dessiné avec le style choisi (module 13c, partie B), comme la version web
// (lib/subtitles/style.ts subtitleCss) : taille, police, couleur, gras, contour, ombre, fond.
// Sert au lecteur et à l'aperçu de la page Sous-titres (plus petit, factor < 1).
// Texte centré, son bas au bas de la boîte ; le fond entoure le texte (un seul bloc, comme le
// <span> du web), calculé seulement quand son opacité n'est pas nulle.
// Tailles : « Normale » est l'ancienne « Très grande » de Native AIO (26 × 1,5 pt), la taille
// propre à la TV étant trop petite (l'auteur, 2026-11-15) ; les autres en gardent les
// proportions du web (0,8 ; 1 ; 1,25 ; 1,5).
// Polices : la TV n'a que SamsungOneUI (LECONS.md) ; les autres sont fournies par Native AIO
// (res/fonts, licences SIL OFL 1.1 et Apache 2.0) et ajoutées une fois au premier sous-titre :
// avec empattement PT Serif, chasse fixe Cousine, machine à écrire Courier Prime, décontractée
// Comic Neue, cursive Satisfy (une seule graisse).
using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;

namespace NativeAIO.Ui
{
    class SubtitleLook
    {
        public const float BasePoint = 39f;
        static readonly float[] Scales = { 0.8f, 1f, 1.25f, 1.5f };
        static readonly float[] OutlinePx = { 0f, 1f, 2f, 3f }; // web, pour un texte de 26 pt
        static readonly string[] Families = { null, "SamsungOneUI", "PT Serif", "Cousine", "Courier Prime", "Comic Neue", "Satisfy" };
        static bool fontsAdded;

        // Polices de Native AIO ajoutées à celles que connaît la TV (sur le fil d'interface).
        public static void AddFonts()
        {
            if (fontsAdded) return;
            fontsAdded = true;
            try
            {
                string dir = Tizen.Applications.Application.Current.DirectoryInfo.Resource + "fonts";
                var t = System.Diagnostics.Stopwatch.StartNew();
                bool ok = FontClient.Instance.AddCustomFontDirectory(dir);
                Probe.Log("fonts", $"polices de Native AIO ajoutées ({dir}) : {(ok ? "oui" : "non")} en {t.ElapsedMilliseconds} ms");
            }
            catch (Exception e) { Probe.Log("fonts", "polices de Native AIO non ajoutées : " + e.GetType().Name + " " + e.Message); }
        }

        public readonly View View;
        readonly TextLabel text;
        readonly View back;
        readonly float width, height, factor;
        readonly string defaultFont;
        string shown;
        float point;
        bool hasBack;
        static bool defaultLogged;

        public SubtitleLook(float width, float height, float factor)
        {
            this.width = width;
            this.height = height;
            this.factor = factor;
            AddFonts();
            View = new View { Size = new Size(width, height) };
            back = new View().Rounded(4);
            back.Hide();
            text = new TextLabel
            {
                Size = new Size(width, height),
                MultiLine = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            defaultFont = text.FontFamily;
            if (!defaultLogged) { defaultLogged = true; Probe.Log("fonts", "police par défaut des textes : « " + defaultFont + " »"); }
            View.Add(back);
            View.Add(text);
        }

        public float Point => point;

        public static float SizeScale(string size) => Scales[Math.Max(0, Array.IndexOf(Prefs.SubtitleSizes, size))];

        public static Color ColorOf(string hex, float alpha = 1f)
        {
            int n = Convert.ToInt32(hex.Substring(1), 16);
            return new Color(((n >> 16) & 255) / 255f, ((n >> 8) & 255) / 255f, (n & 255) / 255f, alpha);
        }

#pragma warning disable CS0618 // ShadowOffset/ShadowColor : dépréciés en API 8, toujours présents
        // Réglages relus (sur le fil d'interface).
        public void Apply()
        {
            point = BasePoint * SizeScale(Prefs.SubtitleSize) * factor;
            text.PointSize = point;
            int font = Math.Max(0, Array.IndexOf(Prefs.SubtitleFonts, Prefs.SubtitleFont));
            text.FontFamily = Families[font] ?? defaultFont;
            text.FontStyle = new PropertyMap().Add("weight", new PropertyValue(Prefs.SubtitleBold ? "bold" : "normal"));
            text.TextColor = ColorOf(Prefs.SubtitleColor);
            Color edge = ColorOf(Prefs.SubtitleOutlineColor);
            float px = OutlinePx[Math.Max(0, Array.IndexOf(Prefs.SubtitleOutlines, Prefs.SubtitleOutline))] * point / 26f;
            try
            {
                var map = new PropertyMap();
                map.Add("color", new PropertyValue(edge));
                map.Add("width", new PropertyValue(px));
                text.Outline = map;
            }
            catch (Exception) { }
            // Le web décale l'ombre de l'épaisseur du contour plus 0,08 em.
            if (Prefs.SubtitleShadow)
            {
                float d = px + 0.08f * point * 1.33f;
                text.ShadowOffset = new Vector2(d, d);
                text.ShadowColor = edge;
            }
            else
            {
                text.ShadowOffset = new Vector2(0, 0);
                text.ShadowColor = new Color(0, 0, 0, 0);
            }
            int opacity = Prefs.SubtitleBackgroundOpacity;
            hasBack = opacity > 0;
            back.BackgroundColor = ColorOf(Prefs.SubtitleBackgroundColor, opacity / 100f);
            PlaceBack();
        }
#pragma warning restore CS0618

        // Texte affiché (null : rien). Posé seulement s'il change (une mise en page à chaque fois).
        public void SetText(string t)
        {
            if (t == shown) return;
            shown = t;
            if (t == null) { View.Hide(); return; }
            text.Text = t;
            View.Show();
            PlaceBack();
        }

        void PlaceBack()
        {
            if (!hasBack || shown == null) { back.Hide(); return; }
#pragma warning disable CS0618 // X/Y : le getter est marqué déprécié avec le setter en API 8
            Vector3 n = text.NaturalSize;
            float w = Math.Min(n.X, width);
            float h = n.X > width ? text.GetHeightForWidth(width) : n.Y;
#pragma warning restore CS0618
            float padX = point * 0.35f, padY = point * 0.08f;
            back.Size = new Size(w + 2 * padX, h + 2 * padY);
            back.Position = new Position((width - w) / 2 - padX, height - h - padY);
            back.Show();
        }
    }
}
