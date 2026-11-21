// Couleurs, tailles et styles de l'interface (charte, docs/CHARTE.md) : fichier commun à tous
// les écrans. Les écrans y sont rattachés groupe par groupe au module 14 (Harmonisation) :
// Paramètres, connexion, accueil, calendrier, barre latérale, page d'un titre, liste des liens et
// liste des bandes-annonces pour l'instant ; les autres
// gardent encore leurs valeurs.
// Accent et fond : ceux du thème choisi, gardés sur le compte comme le web (Prefs.AccentColor,
// Prefs.BackgroundColor ; absents = thème « Default »). Relus à chaque usage : un changement
// fait sur la page Thème ou sur le web se voit dès que l'écran repeint (Version change).
// Couleurs tirées de l'accent comme le web (packages/ui/src/utils/palette.ts) :
// - AccentText : accent éclairci de 35 % vers le blanc (--brand du web), texte sur fond sombre ;
// - OnAccent : texte ou rond posé sur l'accent, blanc, sombre si l'accent est très clair (--color-on-brand).
using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    static class Theme
    {
        // Thèmes tout faits du web (THEME_PRESETS), dans son ordre : clé du nom, fond, accent.
        public static readonly (string Key, string Background, string Accent)[] Presets =
        {
            ("default", Prefs.DefaultBackground, Prefs.DefaultAccent),
            ("midnight", "#000000", "#5649bb"),
            ("deep_purple", "#0a050d", "#8659c9"),
            ("desert", "#090e11", "#c08b57"),
            ("pink", "#0f060c", "#bb5889"),
            ("sun", "#0b0803", "#c17e21"),
            ("crimson", "#0c0d10", "#8c1d1d"),
            ("rainforest", "#050d0c", "#1d975a"),
            ("ocean", "#050a0d", "#1a72a8"),
            ("white", "#0b0b0b", "#ffffff"),
        };

        static string accentHex, backgroundHex;
        static Color accent, background, accentText, onAccent, soft;
        static bool lightAccent;

        // Augmente à chaque changement de thème : un écran repeint s'il a vu une autre valeur.
        public static int Version { get { Read(); return version; } }
        static int version;

        public static Color Accent { get { Read(); return accent; } }
        public static Color Background { get { Read(); return background; } }

        // Fond propre d'un écran qui montre l'arrière-plan d'après l'image (Screen.ArtBackdrop) :
        // transparent quand le réglage est mis, pour laisser voir la couche de l'application.
        public static Color ScreenBack(Color normal) => ArtWash.On ? Color.Transparent : normal;
        // Gris clair tiré du fond (--color-gray-300 du web) : descriptions des versions.
        public static Color Soft { get { Read(); return soft; } }
        public static Color AccentText { get { Read(); return accentText; } }
        public static Color OnAccent { get { Read(); return onAccent; } }
        public static bool LightAccent { get { Read(); return lightAccent; } } // OnAccent sombre

        // Fixes (charte).
        public static readonly Color Ink = Hex(0x0A0D12);         // texte et icône sur fond blanc
        public static readonly Color Grey = Hex(0x9CA3AF);        // texte secondaire
        public static readonly Color Placeholder = Hex(0x1F2129); // image pas chargée, liste déroulante
        public static readonly Color Text = White(0.85f);         // texte au repos
        public static readonly Color Faded = White(0.35f);        // indisponible
        public static readonly Color Error = Hex(0xFFD966);       // messages d'erreur seulement
        public static readonly Color Liner = Hex(0x0A0A0A);       // liseré sombre du contour de focus
        public static readonly Color Panel = new Color(0f, 0f, 0f, 0.9f); // panneau posé sur un écran
        public static readonly Color Veil = new Color(0f, 0f, 0f, 0.8f);  // écran sous un panneau (bg-black/80 du web)
        public static readonly Color Capsule = new Color(0f, 0f, 0f, 0.4f); // capsule à deux choix
        // Fond des boîtes de choix et des menus (Ui/Picker, menus du lecteur) : noir neutre à 95 %
        // (le #030712 du web paraissait bleu sur la TV, LECONS.md).
        public static readonly Color Menu = new Color(0.04f, 0.04f, 0.04f, 0.95f);

        // Blanc transparent : quatre dosages seulement.
        public static readonly Color Faint = White(0.04f);   // surface très discrète
        public static readonly Color Surface = White(0.08f); // surface au repos : boutons, champs, pastilles
        public static readonly Color Marked = White(0.16f);  // séparation, pastille sur une image
        public static readonly Color Chosen = White(0.24f);  // choix en cours sans le focus

        // ---- Texte : six tailles (pt) ----
        public const float SizeSmall = 12;   // petites mentions, étiquettes de panneau
        public const float SizeInfo = 15;    // année, durée, compteurs
        public const float SizeBody = 18;    // texte courant : boutons, champs, pastilles
        public const float SizeHeading = 22; // titres de rangées et de sections
        public const float SizeTitle = 30;   // titre d'écran
        public const float SizeHuge = 48;    // grand titre à la place d'un logo

        // ---- Formes (px) ----
        public const int RadiusSmall = 8;    // petites images
        public const int RadiusLarge = 12;   // grandes images, champs, panneaux, listes déroulantes
        public const int ButtonHeight = 56;  // capsule : arrondi = moitié de la hauteur
        public const int PillHeight = 44;

        // ---- Marges et espacements (px) ----
        public const int Margin = 96;        // marge gauche des écrans
        public const int PlayerMargin = 60;  // lecteur et lecture des bandes-annonces
        public const int Top = 60;           // premier élément depuis le haut
        public const int Gap = 16;           // entre deux boutons
        public const int TileGap = 24;       // entre deux cartes ou affiches d'une rangée

        // ---- Focus ----
        public const int Ring = 3;           // contour blanc, puis liseré sombre de même épaisseur
        public const int RingOut = 2 * Ring; // débord du contour autour de l'élément
        public const float Grow = 1.06f;     // grossissement de l'élément regardé

        // ---- Animations (ms) ----
        public const int Fade = 300;         // fondus
        public const int Slide = 200;        // déplacements, glissements, grossissements
        // Dynamique commune du focus dans une liste et du défilement d'une liste (demande de
        // l'auteur, 2026-11-19 : celle de la boîte de choix des Paramètres, partout) : Slide ms,
        // ralentie à la fin. La changer ici la change partout (Ui/Highlight, Ui/Picker…).
        public static AlphaFunction Ease() => new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);

        // Contour de focus d'un élément de w × h arrondi à radius (charte) : blanc de 3 px, liseré
        // sombre de 3 px, coins concentriques. À poser en (-RingOut, -RingOut) de l'élément, dessous.
        public static View FocusRing(int w, int h, int radius)
        {
            var ring = new View { Size = new Size(w + 2 * RingOut, h + 2 * RingOut), BackgroundColor = Color.White }.Rounded(radius + RingOut);
            ring.Add(new View { Position = new Position(Ring, Ring), Size = new Size(w + 2 * Ring, h + 2 * Ring), BackgroundColor = Liner }.Rounded(radius + Ring));
            return ring;
        }

        // Même contour, au centre transparent (image « 9-patch » étirée, tools/ring.mjs) : le fond de
        // l'élément reste visible (cartes de la liste des liens, l'auteur, 2026-11-18). Arrondis
        // dessinés : 12 (RadiusLarge) et 26 (capsule de 52 px). À poser en (-RingOut, -RingOut),
        // par-dessus ou dessous l'élément.
        public static ImageView HollowRing(int w, int h, int radius) =>
            new ImageView(Tizen.Applications.Application.Current.DirectoryInfo.Resource + "images/ring" + radius + ".9.png")
            {
                Size = new Size(w + 2 * RingOut, h + 2 * RingOut),
            };

        // Contour d'un rond de d px (56 ou 168 : res/images/round<d>.png, tools/ring.mjs). La TV
        // dessine mal un contour rond fait de vues arrondies (aplati en haut et en bas, capture de
        // « Qui regarde ? », module 15). À poser en (-RingOut, -RingOut) du rond, dessous.
        public static ImageView RoundRing(int d) =>
            new ImageView(Tizen.Applications.Application.Current.DirectoryInfo.Resource + "images/round" + d + ".png")
            {
                Size = new Size(d + 2 * RingOut, d + 2 * RingOut),
            };

        public static readonly Color Shade = new Color(0f, 0f, 0f, 0.2f); // carte regardée : assombrie de 20 %

        // Rotation sans fin d'une icône pendant un chargement (icône « Rechercher à nouveau », comme
        // animate-spin du web : un tour par seconde, régulier ; demande de l'auteur, 2026-11-19).
        // Arrêt : Looping = false, l'icône finit son tour au lieu de s'arrêter de travers.
        // Quatre étapes de 120° : une rotation de 360° d'un coup vaut zéro pour la TV.
        public static Animation Spin(View icon)
        {
            icon.PivotPoint = Tizen.NUI.PivotPoint.Center;
            icon.PositionUsesPivotPoint = false;
            var turn = new KeyFrames();
            for (int i = 0; i <= 3; i++)
                turn.Add(i / 3f, new PropertyValue(new Rotation(new Radian(new Degree(120f * i)), Vector3.ZAxis)));
            var a = new Animation(1000) { Looping = true };
            a.AnimateBetween(icon, "Orientation", turn, Animation.Interpolation.Linear, new AlphaFunction(AlphaFunction.BuiltinFunctions.Linear));
            return a;
        }

        // Demi-gras (graisse 600 de SamsungOneUI) : titres de rangées, comme le web (module 14,
        // essai à juger sur photo ; la charte garde « pas de gras » ailleurs).
        public static PropertyMap SemiBold() => new PropertyMap().Add("weight", new PropertyValue("semiBold"));

        public static Color White(float a) => new Color(1f, 1f, 1f, a);
        public static Color InkA(float a) => new Color(Ink.R, Ink.G, Ink.B, a);
        public static Color AccentA(float a) { Read(); return new Color(accent.R, accent.G, accent.B, a); }

        public static Color Hex(int rgb) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        // « #rrggbb » (déjà vérifié par Prefs).
        public static Color Hex(string s) => Hex(Convert.ToInt32(s.Substring(1), 16));

        public static string IconPath(string name) =>
            Tizen.Applications.Application.Current.DirectoryInfo.Resource + "icons/" + name + ".png";

        static void Read()
        {
            string a = Prefs.AccentColor, b = Prefs.BackgroundColor;
            if (a == accentHex && b == backgroundHex) return;
            accentHex = a;
            backgroundHex = b;
            int rgb = Convert.ToInt32(a.Substring(1), 16);
            accent = Hex(rgb);
            background = Hex(b);
            accentText = Hex(Mix(rgb, 0xFFFFFF, 0.35));
            lightAccent = Luminance(rgb) > 0.6;
            onAccent = lightAccent ? Hex(0x0A0A0A) : Color.White;
            // Comme themeVariables du web (packages/ui/src/utils/palette.ts).
            int bg = Convert.ToInt32(b.Substring(1), 16);
            int light = Mix(0xF8FAFC, bg, 0.15);
            soft = Hex(Mix(bg, light, 0.78));
            version++;
        }

        // ---- Calculs de couleur, comme colord (bibliothèque du web) ----

        // Luminance relative (WCAG), entre 0 et 1.
        static double Luminance(int rgb) =>
            0.2126 * Linear((rgb >> 16) & 0xFF) + 0.7152 * Linear((rgb >> 8) & 0xFF) + 0.0722 * Linear(rgb & 0xFF);

        static double Linear(int c)
        {
            double v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        static int Encode(double v)
        {
            v = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            return Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));
        }

        // Mélange de deux couleurs dans l'espace LAB, comme colord().mix (ratio : part de la
        // seconde). colord passe par l'illuminant D50, ici D65 : écart de l'ordre d'une unité
        // par composante, non vérifié au détail près.
        static int Mix(int c1, int c2, double ratio)
        {
            double[] a = Lab(c1), b = Lab(c2);
            var m = new double[3];
            for (int i = 0; i < 3; i++) m[i] = a[i] * (1 - ratio) + b[i] * ratio;
            return FromLab(m);
        }

        const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;

        static double[] Lab(int rgb)
        {
            double r = Linear((rgb >> 16) & 0xFF), g = Linear((rgb >> 8) & 0xFF), bl = Linear(rgb & 0xFF);
            double x = (0.4124 * r + 0.3576 * g + 0.1805 * bl) / Xn;
            double y = (0.2126 * r + 0.7152 * g + 0.0722 * bl) / Yn;
            double z = (0.0193 * r + 0.1192 * g + 0.9505 * bl) / Zn;
            double fx = F(x), fy = F(y), fz = F(z);
            return new[] { 116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz) };
        }

        static int FromLab(double[] lab)
        {
            double fy = (lab[0] + 16) / 116, fx = fy + lab[1] / 500, fz = fy - lab[2] / 200;
            double x = FInv(fx) * Xn, y = FInv(fy) * Yn, z = FInv(fz) * Zn;
            double r = 3.2406 * x - 1.5372 * y - 0.4986 * z;
            double g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
            double bl = 0.0557 * x - 0.2040 * y + 1.0570 * z;
            return (Encode(r) << 16) | (Encode(g) << 8) | Encode(bl);
        }

        static double F(double t) => t > 216.0 / 24389 ? Math.Pow(t, 1.0 / 3) : (24389.0 / 27 * t + 16) / 116;
        static double FInv(double t) => t * t * t > 216.0 / 24389 ? t * t * t : (116 * t - 16) / (24389.0 / 27);
    }
}
