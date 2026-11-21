// Écritures des préférences de la TV (Preference) hors du fil d'interface (module 16) :
// une écriture a pris jusqu'à 75 ms sur la TV, peut-être 1,4 s une fois (LECONS.md), pendant
// lesquelles l'écran ne répondait plus. Toutes les écritures passent par une seule file, l'une
// après l'autre et dans l'ordre ; la durée de chacune est notée dans le journal de la sonde
// (catégorie « store »). Les valeurs écrites sont gardées en mémoire par ceux qui les écrivent :
// rien ne relit une préférence juste après l'avoir écrite.
// À la fermeture, App attend la fin des écritures en cours (Flush, une seconde au plus).
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Tizen.Applications;

namespace NativeAIO.Core
{
    static class Store
    {
        static readonly object gate = new object();
        static Task queue = Task.CompletedTask;

        public static void Set(string key, string value) => Run(key, () => Preference.Set(key, value));

        public static void Set(string key, int value) => Run(key, () => Preference.Set(key, value));

        public static void Remove(string key) => Run(key + " (retrait)", () => { if (Preference.Contains(key)) Preference.Remove(key); });

        // Écriture quelconque (plusieurs préférences d'un coup, types variés), nommée pour le journal.
        public static void Run(string what, Action write)
        {
            lock (gate)
            {
                queue = queue.ContinueWith(_ =>
                {
                    var sw = Stopwatch.StartNew();
                    try { write(); }
                    catch (Exception e) { Probe.Log("store", $"{what} : échec ({e.Message})"); return; }
                    Probe.Log("store", $"{what} : {sw.ElapsedMilliseconds} ms");
                }, TaskScheduler.Default);
            }
        }

        // Attend la fin des écritures en cours, au plus ms millisecondes.
        public static void Flush(int ms)
        {
            Task t;
            lock (gate) t = queue;
            try { t.Wait(ms); } catch (Exception) { }
        }
    }
}
