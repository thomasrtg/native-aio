// Arrière-plan d'après l'image (réglage « Background from artwork » du web, Prefs.ArtBackground) :
// l'image du titre, floutée et assombrie, sous tous les écrans (Ui/ArtLayer.cs). Calcul traduit du web (packages/web/src/lib/artwork/wash.ts et background.ts,
// version du 2026-11-14) :
// - image de fond du titre demandée à 300 px de large (sinon l'affiche à 160 px) ;
// - lue par la TV à 96 × 54 points en remplissant le cadre (le reste coupé) ;
// - floutée (trois passes de moyenne, rayon 8, proches d'un flou gaussien), en lumière linéaire ;
// - luminosité moyenne ramenée à 0,045, couleurs renforcées (×1,5), hautes lumières adoucies,
//   plus sombre vers le bas (-30 %) et un peu sur les côtés ;
// - redessinée à 320 × 180 avec un léger grain (évite les marches d'un dégradé sombre
//   étiré sur l'écran), écrite en PNG dans le cache, étirée par la TV sur tout l'écran.
// Lecture des points (ImageLoading.LoadImageFromFile, PixelBuffer.GetBuffer) sur le fil
// d'interface (petite image), calcul et écriture à part. Si la lecture échoue : la petite
// image elle-même, étirée (floue par l'agrandissement) et assombrie (repli, Fallback).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;
using Path = System.IO.Path;

namespace NativeAIO.Ui
{
    static class ArtWash
    {
        const int GridW = 96, GridH = 54, WashW = 320, WashH = 180;
        const int BoxRadius = 8;
        const float Mean = 0.045f, MinGain = 0.08f, MaxGain = 1.2f, WhitePoint = 0.2f, Saturation = 1.5f;
        public static bool On => Prefs.ArtBackground;

        static string dir;
        static readonly Dictionary<string, string> made = new Dictionary<string, string>(); // adresse → PNG
        public static bool Fallback; // lecture des points impossible sur cette TV
        static int next;
        public static int Made, Failed;
        public static long LastReadMs, LastMakeMs;

        // Adresse de l'image d'où l'arrière-plan est tiré ; rien pour une personne (sa photo).
        public static string UrlOf(Item it) => it == null || it.Type == "Person" ? null : Library.Backdrop(it, 300) ?? Library.Poster(it, 160);

        // done(fichier à étirer sur l'écran, vrai s'il faut l'assombrir : repli), ou rien si le
        // titre n'a pas d'image. Appelé sur le fil d'interface.
        public static void Request(Item it, Func<int> priority, Action<string, bool> done)
        {
            string url = UrlOf(it);
            if (url == null) return;
            if (made.TryGetValue(url, out string ready) && File.Exists(ready)) { done(ready, false); return; }
            ImageCache.Request(url, priority, path => { if (path != null) Make(url, path, done); });
        }

        static async void Make(string url, string path, Action<string, bool> done)
        {
            try
            {
                if (Fallback) { done(path, true); return; }
                var sw = Stopwatch.StartNew();
                byte[] px = null;
                int w = 0, h = 0, bpp = 0;
                try
                {
                    using (PixelBuffer pb = ImageLoading.LoadImageFromFile(path, new Size2D(GridW, GridH), FittingModeType.ScaleToFill, SamplingModeType.BoxThenLinear, true))
                    {
                        if (pb != null)
                        {
                            w = (int)pb.GetWidth();
                            h = (int)pb.GetHeight();
                            PixelFormat f = pb.GetPixelFormat();
                            bpp = f == PixelFormat.RGB888 ? 3 : f == PixelFormat.RGBA8888 ? 4 : 0;
                            if (bpp == 0) throw new NotSupportedException("format " + f);
                            if (w > 0 && h > 0)
                            {
                                px = new byte[w * h * bpp];
                                Marshal.Copy(pb.GetBuffer(), px, 0, px.Length);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    // La TV ne laisse pas lire les points : repli pour toute la suite.
                    Fallback = true;
                    Failed++;
                    Probe.Log("wash", "lecture des points impossible, repli sur l'image étirée : " + e.GetType().Name + " " + e.Message);
                    done(path, true);
                    return;
                }
                if (px == null) { Failed++; Probe.Log("wash", "image illisible : " + Path.GetFileName(path)); return; }
                LastReadMs = sw.ElapsedMilliseconds;
                if (dir == null)
                {
                    dir = Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.Cache, "wash");
                    // Ceux d'un lancement précédent : refaits à la demande.
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    Directory.CreateDirectory(dir);
                }
                string file = Path.Combine(dir, (next++) + ".png");
                await Task.Run(() => Png.Write(file, WashW, WashH, 3, Draw(Wash(Grid(px, w, h, bpp)))));
                LastMakeMs = sw.ElapsedMilliseconds;
                made[url] = file;
                Made++;
                Probe.Log("wash", $"fait en {LastMakeMs} ms (lecture {LastReadMs} ms, {w}×{h}, {bpp * 8} bits)");
                done(file, false);
            }
            catch (Exception e)
            {
                Failed++;
                Probe.Log("wash", "échec : " + e.GetType().Name + " " + e.Message);
            }
        }

        // ---- Calcul (web : wash.ts) ----

        static readonly float[] toLinear = MakeLinear();
        const int Lut = 4096;
        static readonly float[] toSrgb = MakeSrgb();

        static float[] MakeLinear()
        {
            var t = new float[256];
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
            }
            return t;
        }

        static float[] MakeSrgb()
        {
            var t = new float[Lut + 1];
            for (int i = 0; i <= Lut; i++)
            {
                double c = (double)i / Lut;
                t[i] = (float)(255 * (c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055));
            }
            return t;
        }

        // Les points lus ramenés à GridW × GridH en lumière linéaire (r, g, b à la suite).
        // La TV rend en principe déjà cette taille ; sinon cadre rempli, centre gardé.
        static float[][] Grid(byte[] px, int w, int h, int bpp)
        {
            int n = GridW * GridH;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            float scale = Math.Max((float)GridW / w, (float)GridH / h);
            float sw = GridW / scale, sh = GridH / scale;
            float x0 = (w - sw) / 2, y0 = (h - sh) / 2;
            for (int y = 0; y < GridH; y++)
                for (int x = 0; x < GridW; x++)
                {
                    int sx = Math.Min(w - 1, (int)(x0 + (x + 0.5f) / scale));
                    int sy = Math.Min(h - 1, (int)(y0 + (y + 0.5f) / scale));
                    int o = (sy * w + sx) * bpp, i = y * GridW + x;
                    r[i] = toLinear[px[o]];
                    g[i] = toLinear[px[o + 1]];
                    b[i] = toLinear[px[o + 2]];
                }
            return new[] { r, g, b };
        }

        // Une passe de moyenne le long des lignes (step 1) ou des colonnes (step GridW), bords tenus.
        static void BoxPass(float[] src, float[] dst, int lines, int length, int lineStep, int step)
        {
            const int R = BoxRadius;
            float scale = 1f / (2 * R + 1);
            for (int line = 0; line < lines; line++)
            {
                int bas = line * lineStep;
                int last = bas + (length - 1) * step;
                float sum = src[bas] * (R + 1);
                for (int i = 1; i <= R; i++) sum += src[bas + Math.Min(i, length - 1) * step];
                for (int i = 0; i < length; i++)
                {
                    dst[bas + i * step] = sum * scale;
                    int add = i + R + 1, drop = i - R;
                    sum += src[add < length ? bas + add * step : last] - src[drop > 0 ? bas + drop * step : bas];
                }
            }
        }

        static void Blur(float[] channel, float[] scratch)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                BoxPass(channel, scratch, GridH, GridW, GridW, 1);
                BoxPass(scratch, channel, GridW, GridH, 1, GridW);
            }
        }

        static float Smoothstep(float a, float b, float x)
        {
            float t = Math.Min(1, Math.Max(0, (x - a) / (b - a)));
            return t * t * (3 - 2 * t);
        }

        static float[] Wash(float[][] c)
        {
            float[] r = c[0], g = c[1], b = c[2];
            int n = GridW * GridH;
            var scratch = new float[n];
            Blur(r, scratch);
            Blur(g, scratch);
            Blur(b, scratch);
            float mean = 0;
            for (int i = 0; i < n; i++) mean += 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
            mean /= n;
            float gain = Math.Min(MaxGain, Math.Max(MinGain, Mean / (mean > 0 ? mean : 1e-4f)));
            var rgb = new float[n * 3];
            for (int y = 0; y < GridH; y++)
            {
                float v = (float)y / (GridH - 1);
                // Plus sombre vers le bas, où sont les rangées et le texte, et un peu sur les côtés.
                float fall = 1 - 0.3f * Smoothstep(0.3f, 1, v);
                for (int x = 0; x < GridW; x++)
                {
                    int i = y * GridW + x;
                    float u = 2f * x / (GridW - 1) - 1;
                    float shade = fall * (1 - 0.12f * u * u);
                    float cr = r[i] * gain, cg = g[i] * gain, cb = b[i] * gain;
                    float lum = 0.2126f * cr + 0.7152f * cg + 0.0722f * cb;
                    cr = Math.Max(0, lum + (cr - lum) * Saturation);
                    cg = Math.Max(0, lum + (cg - lum) * Saturation);
                    cb = Math.Max(0, lum + (cb - lum) * Saturation);
                    float toned = 1 / (1 + lum / WhitePoint);
                    rgb[i * 3] = cr * toned * shade;
                    rgb[i * 3 + 1] = cg * toned * shade;
                    rgb[i * 3 + 2] = cb * toned * shade;
                }
            }
            return rgb;
        }

        // Redessinée à WashW × WashH (interpolation entre les cases), avec un grain de ±1 cran.
        static byte[] Draw(float[] rgb)
        {
            const int Noise = 64;
            var noise = new float[Noise * Noise * 3];
            uint seed = 0x9e3779b9;
            Func<float> rand = () => { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return seed / 4294967296f; };
            for (int i = 0; i < noise.Length; i++) noise[i] = rand() - rand();

            var col0 = new int[WashW];
            var col1 = new int[WashW];
            var colW = new float[WashW];
            for (int x = 0; x < WashW; x++)
            {
                float gx = Math.Min(GridW - 1, Math.Max(0, (x + 0.5f) * GridW / WashW - 0.5f));
                col0[x] = (int)gx * 3;
                col1[x] = Math.Min(GridW - 1, (int)gx + 1) * 3;
                colW[x] = gx - (int)gx;
            }
            var outp = new byte[WashW * WashH * 3];
            for (int y = 0; y < WashH; y++)
            {
                float gy = Math.Min(GridH - 1, Math.Max(0, (y + 0.5f) * GridH / WashH - 0.5f));
                int y0 = (int)gy;
                float fy = gy - y0;
                int row0 = y0 * GridW * 3, row1 = Math.Min(GridH - 1, y0 + 1) * GridW * 3;
                int noiseRow = (y & (Noise - 1)) * Noise;
                int o = y * WashW * 3;
                for (int x = 0; x < WashW; x++, o += 3)
                {
                    int a0 = row0 + col0[x], a1 = row0 + col1[x], b0 = row1 + col0[x], b1 = row1 + col1[x];
                    float fx = colW[x];
                    int nz = (noiseRow + (x & (Noise - 1))) * 3;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        float top = rgb[a0 + ch] + (rgb[a1 + ch] - rgb[a0 + ch]) * fx;
                        float bottom = rgb[b0 + ch] + (rgb[b1 + ch] - rgb[b0 + ch]) * fx;
                        float lin = Math.Min(1, Math.Max(0, top + (bottom - top) * fy));
                        float s = toSrgb[(int)(lin * Lut)] + noise[nz + ch];
                        outp[o + ch] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(s)));
                    }
                }
            }
            return outp;
        }

        // ---- Masque de la page d'un titre ----
        // Avec l'arrière-plan d'après l'image, l'image de fond du haut de la page devient
        // transparente vers le bas au lieu de finir en noir, pour se fondre dans l'arrière-plan
        // qui reste en place quand la page défile. Ses deux voiles noirs (à gauche, en bas) sont
        // remplacés par de la transparence : l'arrière-plan, très sombre, assombrit de même.
        // (Une image noire à transparence posée par-dessus restait invisible sur la TV, cause
        // non trouvée.) Mêmes courbes que les voiles d'ItemScreen. Calculé une fois, gardé.

        static string maskPath;

        public static string BackdropMask()
        {
            if (maskPath != null && File.Exists(maskPath)) return maskPath;
            const int W = 240, H = 135;
            var px = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
            {
                float fy = (y + 0.5f) / H, py = fy * 1080;
                float keep = 1 - Smoothstep(0.55f, 1, fy);
                float bottom = py < 380 ? 0 : (float)Math.Pow((py - 380) / 700, 1.6);
                for (int x = 0; x < W; x++)
                {
                    float t = (x + 0.5f) / W * 1920 / 1300;
                    float left = t >= 1 ? 0 : 0.92f * (1 - t) * (1 - t);
                    float a = keep * (1 - left) * (1 - Math.Min(1, bottom));
                    int o = (y * W + x) * 4;
                    px[o] = px[o + 1] = px[o + 2] = 255;
                    px[o + 3] = (byte)Math.Round(255 * Math.Max(0, Math.Min(1, a)));
                }
            }
            maskPath = WriteShared("mask2.png", W, H, px);
            return maskPath;
        }

        static string WriteShared(string name, int w, int h, byte[] px)
        {
            string d = Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.Cache, "wash-masks");
            Directory.CreateDirectory(d);
            string p = Path.Combine(d, name);
            Png.Write(p, w, h, 4, px);
            return p;
        }
    }
}
