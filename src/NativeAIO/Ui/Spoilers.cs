// Flou des épisodes pas commencés (réglage « Blur episodes you haven't started » du web,
// Prefs.BlurSpoilers ; web : styles.css, data-blur-spoilers, version du 2026-11-08).
// Le web floute l'image (12 px) et les textes (5 px) d'un épisode ni vu ni commencé, tant
// que la sélection n'est pas dessus. NUI ne sait flouter ni une vue ni un texte : l'image
// floue est calculée ici, une fois par image, et posée sur l'image nette ; les textes sont
// remplacés par « Épisode N » (écrans).
// Calcul : l'image lue par la TV à 96 × 54 points (même lecture que l'arrière-plan d'après
// l'image, ArtWash), trois passes de moyenne de rayon 3 (proche d'un flou gaussien de
// 3,5 points, soit environ 12 px sur une vignette de 300 px comme le web), écrite en PNG
// dans le cache et étirée par la TV à la taille de la vignette.
// Si la lecture des points échoue : rien (done(null)), l'écran garde l'image nette cachée.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using Item = NativeAIO.Core.Item;
using Path = System.IO.Path;

namespace NativeAIO.Ui
{
    static class Spoilers
    {
        const int GridW = 96, GridH = 54, Radius = 3, Passes = 3;

        static string dir;
        static readonly Dictionary<string, string> made = new Dictionary<string, string>(); // image nette → PNG flou
        static int next;
        public static int Made, Failed;
        public static long LastMs;

        public static bool On => Prefs.BlurSpoilers;

        // Épisode ni vu ni commencé (web : episodeState, data-watched et data-in-progress).
        public static bool Hidden(Item e) =>
            On && e != null && e.Type == "Episode" && e.UserData?.Played != true && Library.Progress(e) <= 0;

        // done(fichier flou) ou done(null) en cas d'échec. Appelé sur le fil d'interface.
        public static async void Request(string path, Action<string> done)
        {
            try
            {
                if (made.TryGetValue(path, out string ready) && File.Exists(ready)) { done(ready); return; }
                var sw = Stopwatch.StartNew();
                byte[] px = null;
                int w = 0, h = 0, bpp = 0;
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
                if (px == null) throw new InvalidDataException("image illisible");
                if (dir == null)
                {
                    dir = Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.Cache, "spoilers");
                    // Ceux d'un lancement précédent : refaits à la demande.
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    Directory.CreateDirectory(dir);
                }
                string file = Path.Combine(dir, (next++) + ".png");
                await Task.Run(() => Png.Write(file, GridW, GridH, 3, Blur(px, w, h, bpp)));
                LastMs = sw.ElapsedMilliseconds;
                made[path] = file;
                Made++;
                Probe.Log("spoiler", $"flou fait en {LastMs} ms ({w}×{h}, {bpp * 8} bits)");
                done(file);
            }
            catch (Exception e)
            {
                Failed++;
                Probe.Log("spoiler", "échec : " + e.GetType().Name + " " + e.Message);
                done(null);
            }
        }

        // Image d'un épisode cachée hors sélection, dans le cadre d'une carte ou d'une ligne :
        // image nette et copie floue superposées, échangées en fondu (0,3 s, comme le web) ;
        // les textes sont changés par l'écran (Texts). Hors sélection, l'image nette reste
        // cachée tant que la copie floue n'est pas là (et pour de bon si le calcul échoue).
        public sealed class Veil
        {
            readonly View frame;
            readonly int w, h, radius;
            readonly Func<bool> dead;
            readonly Action<string, Action> guard;
            ImageView sharp, blur;
            string path;
            bool asked;
            Animation anim; // gardée, sinon elle s'arrête
            public Action<bool> Texts; // vrai : textes complets ; faux : textes cachés
            public bool Spoiler { get; private set; }
            public bool Shown { get; private set; } = true;

            public Veil(View frame, int w, int h, int radius, bool spoiler, Func<bool> dead, Action<string, Action> guard)
            {
                this.frame = frame;
                this.w = w;
                this.h = h;
                this.radius = radius;
                this.dead = dead;
                this.guard = guard;
                Spoiler = spoiler;
            }

            // Image nette propre à l'épisode, posée par l'écran dans le cadre. Vrai si le voile
            // en règle l'opacité (l'écran ne fait alors pas son fondu habituel).
            public bool Take(ImageView img, string file)
            {
                sharp = img;
                path = file;
                if (!Spoiler) return false;
                img.Opacity = Shown ? 1f : 0f;
                Request();
                return true;
            }

            // Épisode marqué vu ou non vu.
            public void SetSpoiler(bool on, bool focused)
            {
                Spoiler = on;
                Paint(focused);
                Request();
            }

            public void Paint(bool focused)
            {
                bool show = !Spoiler || focused;
                if (show == Shown) return;
                Shown = show;
                Texts?.Invoke(show);
                if (sharp == null) return;
                anim?.Stop();
                var a = new Animation(Theme.Fade);
                a.AnimateTo(sharp, "Opacity", show ? 1f : 0f);
                if (blur != null) a.AnimateTo(blur, "Opacity", show ? 0f : 1f);
                a.Play();
                anim = a;
            }

            void Request()
            {
                if (!Spoiler || sharp == null || blur != null || path == null || asked) return;
                asked = true;
                Spoilers.Request(path, file => guard("Veil", () => OnBlur(file)));
            }

            // Copie floue posée juste au-dessus de l'image nette (sous progression et pastilles).
            void OnBlur(string file)
            {
                if (file == null || blur != null || sharp == null || dead()) return;
                View v = Pictures.Cover(file, w, h, out ImageView img);
                if (v == null) return;
                img.Rounded(radius);
                img.Opacity = Shown ? 0f : 1f;
                frame.Add(img);
                img.LowerToBottom();
                sharp.LowerToBottom();
                blur = img;
            }
        }

        // Points ramenés à GridW × GridH (cadre rempli, centre gardé), floutés, en RGB 8 bits.
        static byte[] Blur(byte[] px, int w, int h, int bpp)
        {
            int n = GridW * GridH;
            var c = new float[3][];
            for (int ch = 0; ch < 3; ch++) c[ch] = new float[n];
            float scale = Math.Max((float)GridW / w, (float)GridH / h);
            float x0 = (w - GridW / scale) / 2, y0 = (h - GridH / scale) / 2;
            for (int y = 0; y < GridH; y++)
                for (int x = 0; x < GridW; x++)
                {
                    int sx = Math.Min(w - 1, (int)(x0 + (x + 0.5f) / scale));
                    int sy = Math.Min(h - 1, (int)(y0 + (y + 0.5f) / scale));
                    int o = (sy * w + sx) * bpp, i = y * GridW + x;
                    for (int ch = 0; ch < 3; ch++) c[ch][i] = px[o + ch];
                }
            var scratch = new float[n];
            var outp = new byte[n * 3];
            for (int ch = 0; ch < 3; ch++)
            {
                for (int pass = 0; pass < Passes; pass++)
                {
                    BoxPass(c[ch], scratch, GridH, GridW, GridW, 1);
                    BoxPass(scratch, c[ch], GridW, GridH, 1, GridW);
                }
                for (int i = 0; i < n; i++) outp[i * 3 + ch] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(c[ch][i])));
            }
            return outp;
        }

        // Une passe de moyenne le long des lignes (step 1) ou des colonnes (step GridW), bords tenus.
        static void BoxPass(float[] src, float[] dst, int lines, int length, int lineStep, int step)
        {
            const int R = Radius;
            float k = 1f / (2 * R + 1);
            for (int line = 0; line < lines; line++)
            {
                int bas = line * lineStep;
                int last = bas + (length - 1) * step;
                float sum = src[bas] * (R + 1);
                for (int i = 1; i <= R; i++) sum += src[bas + Math.Min(i, length - 1) * step];
                for (int i = 0; i < length; i++)
                {
                    dst[bas + i * step] = sum * k;
                    int add = i + R + 1, drop = i - R;
                    sum += src[add < length ? bas + add * step : last] - src[drop > 0 ? bas + drop * step : bas];
                }
            }
        }
    }
}
