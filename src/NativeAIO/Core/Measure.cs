// Panneau de mesures, en transparence à droite de l'écran (touche rouge pour le
// masquer), lisible sur une seule photo :
// - images dessinées par seconde (NUI ne redessine que ce qui change : à
//   l'arrêt, ce nombre tombe à 0 ou 1, c'est normal) ;
// - délai entre l'appui sur une touche et l'image suivante : dernier, médiane
//   et maximum des 20 derniers appuis (touches sans effet non comptées) ;
// - dernière touche reçue, tailles de la fenêtre et de l'écran, mémoire utilisée ;
// - puis les lignes de l'écran en cours (Measure.Note), relues chaque seconde.
// Le compteur d'images tourne sur le fil principal : quand ce fil est occupé,
// il sous-estime la fluidité réelle de l'animation (voir LECONS.md).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Core
{
    class Measure
    {
        public const int PanelX = 1100, PanelW = 800;

        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly List<long> lats = new List<long>();
        readonly List<long> all = new List<long>(); // tous les délais depuis la remise à zéro (sonde)
        int lastFps;
        readonly TextLabel label;
        readonly Timer refresh; // gardé dans un champ, sinon il s'arrête

        long keyAt = -1, lastLat;
        int frames;
        string lastKey = "-";
        string shown = "";
        readonly string sizes;

        // Lignes de l'écran en cours (temps de chargement…), relues chaque seconde.
        public static Func<string> Note { get; set; }

        public View View => label;

        public Measure()
        {
            label = new TextLabel
            {
                Position = new Position(PanelX, 20),
                Size = new Size(PanelW, 1040),
                MultiLine = true,
                PointSize = 14,
                TextColor = new Color(1f, 1f, 0.6f, 1f),
                BackgroundColor = new Color(0f, 0f, 0f, 0.72f),
                VerticalAlignment = VerticalAlignment.Top,
            };
            // Taille de la fenêtre de l'application et de l'écran annoncé par la TV.
            var ws = Window.Instance.WindowSize;
            int sw = 0, sh = 0;
            try
            {
                Tizen.System.Information.TryGetValue("http://tizen.org/feature/screen.width", out sw);
                Tizen.System.Information.TryGetValue("http://tizen.org/feature/screen.height", out sh);
            }
            catch (Exception) { }
#pragma warning disable CS0618 // seule l'écriture est dépréciée, la lecture est sans risque
            sizes = $"{ws.Width}×{ws.Height} / {sw}×{sh}";
#pragma warning restore CS0618
            Watch();
            refresh = new Timer(1000);
            refresh.Tick += (s, e) => { Update(); return true; };
            refresh.Start();
            // Paquet publié (sans la sonde) : panneau caché au départ, touche rouge pour l'afficher.
#if !PROBE
            label.Hide();
#endif
        }

        public bool Visible => label.Visibility;

        public void Toggle()
        {
            if (label.Visibility) label.Hide(); else { label.Show(); Watch(); Update(); }
        }

        // Touche sans effet (ex. Droite en bout de rangée) : rien ne change à l'écran,
        // l'image suivante ne dit rien de la fluidité. Elle n'est pas comptée.
        public void KeyIgnored() => keyAt = -1;

        // Appelé à chaque touche enfoncée, avant son traitement.
        public void KeyDown(string key)
        {
            lastKey = key;
            keyAt = clock.ElapsedMilliseconds;
        }

        // Après un changement de langue.
        public void ApplyTexts() { shown = ""; Update(); }

        void OnFrame(int id)
        {
            frames++;
            if (keyAt >= 0)
            {
                lastLat = clock.ElapsedMilliseconds - keyAt;
                keyAt = -1;
                lats.Add(lastLat);
                if (lats.Count > 20) lats.RemoveAt(0);
                if (all.Count < 10000) all.Add(lastLat);
                Probe.Log("lat", lastKey + " " + lastLat);
            }
            // À réenregistrer à chaque image. Panneau caché sans la sonde (version publiée) : plus
            // de rappel à chaque image, repris quand le panneau réapparaît (module 16).
            watching = false;
            if (label.Visibility || Probe.On) Watch();
        }

        bool watching;

        void Watch()
        {
            if (watching) return;
            watching = true;
            Window.Instance.AddFramePresentedCallback(OnFrame, 0);
        }

        // Mémoire réellement occupée par l'application (VmRSS), en Mo.
        static string Memory()
        {
            try
            {
                foreach (string line in File.ReadLines("/proc/self/status"))
                {
                    if (!line.StartsWith("VmRSS:")) continue;
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    // Entre parenthèses : la part gérée par .NET (sans les images ni les vues de la TV).
                    return (long.Parse(parts[1]) / 1024) + " Mo (.NET " + (GC.GetTotalMemory(false) / 1048576) + " Mo)";
                }
            }
            catch (Exception) { }
            return "?";
        }

        void Update()
        {
            int fps = frames;
            frames = 0;
            lastFps = fps;
            if (!label.Visibility) return;
            long med = 0, max = 0;
            if (lats.Count > 0)
            {
                var sorted = new List<long>(lats);
                sorted.Sort();
                med = sorted[sorted.Count / 2];
                max = sorted[sorted.Count - 1];
            }
            string text = Lang.T("measure.panel", fps, lastLat, med, max, lastKey, sizes, Memory());
            string note = Note?.Invoke();
            if (!string.IsNullOrEmpty(note)) text += "\n\n" + note;
            if (text == shown) return; // ne pas provoquer d'image pour rien
            shown = text;
            label.Text = text;
        }

        // ---- Sonde (Core/Probe.cs) ----

        public void ResetStats()
        {
            lats.Clear();
            all.Clear();
            keyAt = -1;
        }

        // Mesures en JSON. Délais appui → image en ms, sur tous les appuis
        // depuis la dernière remise à zéro ; « note » = lignes de l'écran en cours.
        public string Json(string screen, int depth, bool sidebar)
        {
            var sorted = new List<long>(all);
            sorted.Sort();
            long At(double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
            string note = null;
            try { note = Note?.Invoke(); } catch (Exception e) { note = "erreur : " + e.Message; }
            return "{"
                + $"\"t\":{Probe.Now},"
                + $"\"screen\":{Probe.Js(screen)},\"depth\":{depth},\"sidebar\":{(sidebar ? "true" : "false")},"
                + $"\"fps\":{lastFps},"
                + $"\"memory\":{Probe.Js(Memory())},"
                + $"\"panel\":{(label.Visibility ? "true" : "false")},"
                + $"\"lastKey\":{Probe.Js(lastKey)},"
                + $"\"latency\":{{\"n\":{sorted.Count},\"last\":{lastLat},\"median\":{At(0.5)},\"p90\":{At(0.9)},\"max\":{At(1)}}},"
                + $"\"note\":{Probe.Js(note)}"
                + "}";
        }
    }
}
