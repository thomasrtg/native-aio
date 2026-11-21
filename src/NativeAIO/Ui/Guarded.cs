// Garde des traitements d'un écran (repris de HomeScreen.Guard) : une exception
// est affichée dans un cadre jaune (10 s) au lieu de fermer l'application, et un
// traitement de plus de 30 ms sur le fil d'interface est noté pour la sonde.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class Guarded
    {
        readonly View root;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Dictionary<string, long> slowest = new Dictionary<string, long>();
        TextLabel box;
        Timer timer; // gardé dans un champ, sinon il s'arrête
        public int SlowCount { get; private set; }
        public string LastError { get; private set; }

        public Guarded(View root) { this.root = root; }

        public void Run(string where, Action run)
        {
            long t0 = clock.ElapsedMilliseconds;
            try { run(); }
            catch (Exception e) { Show(where, e); }
            long ms = clock.ElapsedMilliseconds - t0;
            if (ms < 30) return;
            Probe.Log("slow", where + " " + ms + " ms");
            string name = where.StartsWith("Key ") ? "Key" : where;
            if (!slowest.TryGetValue(name, out long prev) || ms > prev) slowest[name] = ms;
            SlowCount++;
        }

        public string Slowest()
        {
            if (slowest.Count == 0) return Lang.T("home.none");
            var l = new List<KeyValuePair<string, long>>(slowest);
            l.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < l.Count && i < 4; i++) parts.Add(l[i].Key + " " + l[i].Value + " ms");
            return string.Join(", ", parts);
        }

        public void Show(string where, Exception e)
        {
            var lines = new List<string> { $"{where} : {e.GetType().Name} : {e.Message}" };
            for (Exception inner = e.InnerException; inner != null; inner = inner.InnerException)
                lines.Add($"→ {inner.GetType().Name} : {inner.Message}");
            string[] stack = (e.StackTrace ?? "").Split('\n');
            for (int i = 0; i < stack.Length && i < 6; i++) lines.Add(stack[i].Trim());
            Probe.Log("error", string.Join(" | ", lines));
            Crash.Note("caught " + e.GetType().Name); // type seulement : le message peut être personnel
            LastError = lines[0];
            if (Closed) return;
            if (box == null)
            {
                box = new TextLabel
                {
                    Position = new Position(40, 200),
                    Size = new Size(1840, 600),
                    PointSize = 14,
                    MultiLine = true,
                    TextColor = new Color(1f, 0.85f, 0.4f, 1f),
                    BackgroundColor = new Color(0f, 0f, 0f, 0.85f),
                };
                root.Add(box);
            }
            box.Text = Lang.T("home.error", string.Join("\n", lines));
            box.Show();
            box.RaiseToTop();
            timer?.Stop();
            timer = new Timer(10000);
            timer.Tick += (s, e2) => { box?.Hide(); return false; };
            timer.Start();
        }

        // L'écran est fermé : plus rien à afficher.
        public bool Closed { get; private set; }

        public void Stop() { Closed = true; timer?.Stop(); }
    }
}
