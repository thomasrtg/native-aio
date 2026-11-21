// Images téléchargées par ImageCache et confiées à la TV (voir LECONS.md) :
// fichier local, décodé à la taille affichée, avec la division par zéro que NUI
// lève sur certains fichiers interceptée.
using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    static class Pictures
    {
        // Image décodée à la taille affichée, ou null si la TV ne sait pas la lire.
        // Le WebP est confié tel quel, sans taille de décodage (affiché par la TV, vu au module 3).
        public static ImageView Decoded(string path, int w, int h)
        {
            if (path == null) return null;
            string ext = System.IO.Path.GetExtension(path);
            if (ext == ".webp") return new ImageView(path) { Size = new Size(w, h) };
            if (ext != ".jpg" && ext != ".png") return null;
            var v = new ImageView(path) { Size = new Size(w, h) };
            try
            {
                v.DesiredWidth = w;
                v.DesiredHeight = h;
                return v;
            }
            catch (DivideByZeroException)
            {
                v.Dispose();
                if (System.IO.File.Exists(path)) Core.ImageCache.Forget(path);
                return null;
            }
        }

        // Image recadrée pour remplir w×h sans être déformée (comme « object-cover »).
        // Le recadrage est fait au décodage (FittingMode) : sur la TV, ClipChildren ne
        // coupe pas ce qui dépasse d'une vue (vu le 2026-11-08), alors que ce recadrage
        // fonctionne (photos rondes de la distribution).
        public static View Cover(string path, int w, int h, out ImageView image)
        {
            image = null;
            if (path == null) return null;
            string ext = System.IO.Path.GetExtension(path);
            if (ext != ".jpg" && ext != ".png" && ext != ".webp") return null;
            var v = new ImageView(path) { Size = new Size(w, h) };
            try
            {
                v.FittingMode = FittingModeType.ScaleToFill;
                v.DesiredWidth = w;
                v.DesiredHeight = h;
            }
            catch (DivideByZeroException)
            {
                v.Dispose();
                if (System.IO.File.Exists(path)) Core.ImageCache.Forget(path);
                return null;
            }
            image = v;
            return v;
        }

        // Photo ronde de diamètre d : décodée en remplissant le carré (le reste coupé
        // au décodage, FittingMode), coins arrondis en cercle. Non vérifié sur la TV.
        public static ImageView Circle(string path, int d)
        {
            if (path == null) return null;
            var v = new ImageView(path) { Size = new Size(d, d) }.Rounded(d / 2f);
            try
            {
                v.FittingMode = FittingModeType.ScaleToFill;
                v.DesiredWidth = d;
                v.DesiredHeight = d;
                return v;
            }
            catch (DivideByZeroException)
            {
                v.Dispose();
                if (System.IO.File.Exists(path)) Core.ImageCache.Forget(path);
                return null;
            }
        }

        // Dimensions d'une image PNG, JPEG ou WebP, lues dans les premiers octets du fichier.
        static readonly byte[] headBuffer = new byte[65536];

        public static void ImageSize(string path, out int w, out int h)
        {
            w = h = 0;
            try
            {
                byte[] d = headBuffer;
                int len = 0;
                using (var f = System.IO.File.OpenRead(path))
                    while (len < d.Length) { int r = f.Read(d, len, d.Length - len); if (r <= 0) break; len += r; }
                if (len > 30 && d[0] == 0x52 && d[1] == 0x49 && d[8] == 0x57 && d[9] == 0x45)
                {
                    // RIFF....WEBP puis « VP8X » (étendu), « VP8 » (avec perte) ou « VP8L » (sans perte).
                    if (d[15] == 0x58) { w = 1 + (d[24] | (d[25] << 8) | (d[26] << 16)); h = 1 + (d[27] | (d[28] << 8) | (d[29] << 16)); }
                    else if (d[15] == 0x20) { w = (d[26] | (d[27] << 8)) & 0x3FFF; h = (d[28] | (d[29] << 8)) & 0x3FFF; }
                    else if (d[15] == 0x4C) { int b = d[21] | (d[22] << 8) | (d[23] << 16) | (d[24] << 24); w = 1 + (b & 0x3FFF); h = 1 + ((b >> 14) & 0x3FFF); }
                    return;
                }
                if (len > 24 && d[0] == 0x89 && d[1] == 0x50)
                {
                    w = (d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19];
                    h = (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23];
                    return;
                }
                if (len > 4 && d[0] == 0xFF && d[1] == 0xD8)
                {
                    int i = 2;
                    while (i + 9 < len)
                    {
                        if (d[i] != 0xFF) { i++; continue; }
                        byte m = d[i + 1];
                        // Marqueurs SOF (début d'image) : hauteur puis largeur.
                        if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                        {
                            h = (d[i + 5] << 8) | d[i + 6];
                            w = (d[i + 7] << 8) | d[i + 8];
                            return;
                        }
                        i += 2 + ((d[i + 2] << 8) | d[i + 3]);
                    }
                }
            }
            catch (Exception) { }
        }

        // Texte coupé à la fin d'un mot avant « len » caractères, terminé par « … ».
        public static string Cut(string text, int len)
        {
            if (text == null || text.Length <= len) return text ?? "";
            int space = text.LastIndexOf(' ', len - 1);
            if (space < len / 2) space = len;
            return text.Substring(0, space).TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
        }

        // Titre posé au centre d'une affiche sans image, quand rien n'est écrit dessous
        // (réglage « Sous les affiches » : une affiche sans image montre tout de même son titre).
        public static TextLabel NoArt(string title, int w, int h) => new TextLabel
        {
            Position = new Position(12, 12),
            Size = new Size(w - 24, h - 24),
            PointSize = 18,
            MultiLine = true,
            Ellipsis = true,
            TextColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = title ?? "",
        };
    }
}
