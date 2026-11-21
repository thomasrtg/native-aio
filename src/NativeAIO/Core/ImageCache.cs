// Chargeur d'images de Native AIO : télécharge les images du serveur dans des
// fichiers (cache sur le disque), dans l'ordre que l'écran juge le plus utile.
// La TV les affiche ensuite depuis ces fichiers, décodées à la taille d'affichage
// (DesiredWidth/Height fonctionne sur un fichier local, pas sur une adresse http).
//
// File d'attente dynamique : chaque demande porte une fonction de priorité,
// relue à chaque fois qu'un téléchargement se libère. L'écran calcule cette
// priorité d'après la position en cours (carte regardée, voisines, rangées
// proches) : un déplacement change l'ordre sans rien avoir à prévenir.
// Plus petit = plus tôt ; int.MaxValue = plus voulu (la demande est retirée).
//
// Tout se passe sur le fil d'interface, sauf le téléchargement et l'écriture du fichier.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NativeAIO.Core
{
    static class ImageCache
    {
        public const int Parallel = 4;
        // Place à utiliser (Paramètres, Cache) ; au-delà, on redescend aux 5/6.
        static long MaxBytes => Prefs.CacheLimitMb * 1024L * 1024;
        static long KeepBytes => MaxBytes / 6 * 5;

        class Waiter
        {
            public Func<int> Priority;
            public Action<string> Done;
        }

        class Job
        {
            public string Url;
            public bool Running;
            public readonly List<Waiter> Waiters = new List<Waiter>();
        }

        static string dir;
        static readonly Dictionary<string, string> files = new Dictionary<string, string>(); // nom → chemin
        static readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();
        static int running;
        static readonly Stopwatch clock = Stopwatch.StartNew();

        // Mesures : durées de téléchargement (ms), images trouvées sur le disque.
        public static readonly List<long> Downloads = new List<long>();
        const int MaxDownloads = 500;
        public static int Hits, Failures;
        // Images reçues en WebP (illisible par la TV), et redemandées en JPEG avec succès.
        public static int WebpReceived, WebpConverted;
        public static int Queued => jobs.Count - running;
        public static int Running => running;

        // Taille du cache sur le disque, fichiers supprimés au démarrage (trop plein),
        // fichiers de l'index introuvables (ne devrait plus arriver).
        public static long TotalBytes;
        public static int FileCount => files.Count;
        public static int Trimmed, Missing;

        // Dossier du cache et fichiers déjà présents, puis Trim (place à utiliser).
        public static void Init()
        {
            if (dir != null) return;
            try
            {
                dir = Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.Cache, "img");
                Directory.CreateDirectory(dir);
                // Dossiers d'un « Tout effacer » interrompu (application fermée avant la fin).
                string parent = Path.GetDirectoryName(dir);
                foreach (string d in Directory.GetDirectories(parent, "img.old*"))
                    Task.Run(() => { try { Directory.Delete(d, true); } catch (Exception) { } });
                // Téléchargements interrompus (.part) : effacés, ils ne sont pas des images.
                long total = 0;
                foreach (FileInfo f in new DirectoryInfo(dir).GetFiles())
                {
                    if (f.Extension.StartsWith(".part")) { try { f.Delete(); } catch (Exception) { } continue; }
                    files[Path.GetFileNameWithoutExtension(f.Name)] = f.FullName;
                    total += f.Length;
                }
                TotalBytes = total;
                Trim();
            }
            catch (Exception) { dir = null; }
        }

        // Au-delà de la place à utiliser, les fichiers les moins récemment utilisés sont
        // retirés de l'index tout de suite, puis effacés en arrière-plan : l'index ne
        // désigne jamais un fichier effacé. (Avant ce correctif, l'effacement en
        // arrière-plan laissait l'index pointer vers des fichiers disparus : la TV levait
        // alors une division par zéro.) Appelé au démarrage et quand la limite baisse.
        // Renvoie le nombre de fichiers retirés.
        public static int Trim()
        {
            if (dir == null || TotalBytes <= MaxBytes) return 0;
            try
            {
                var all = new List<FileInfo>();
                long total = 0;
                foreach (string path in files.Values)
                {
                    var f = new FileInfo(path);
                    if (!f.Exists) continue;
                    all.Add(f);
                    total += f.Length;
                }
                all.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                var victims = new List<FileInfo>();
                foreach (FileInfo f in all)
                {
                    if (total <= KeepBytes) break;
                    victims.Add(f);
                    total -= f.Length;
                    files.Remove(Path.GetFileNameWithoutExtension(f.Name));
                }
                TotalBytes = total;
                Trimmed += victims.Count;
                // Une image retéléchargée entre-temps porte le même nom : elle a une autre
                // date et n'est pas effacée.
                if (victims.Count > 0)
                    Task.Run(() =>
                    {
                        foreach (FileInfo f in victims)
                        {
                            try { if (File.GetLastWriteTimeUtc(f.FullName) == f.LastWriteTimeUtc) File.Delete(f.FullName); }
                            catch (Exception) { }
                        }
                    });
                return victims.Count;
            }
            catch (Exception) { return 0; }
        }

        // « Tout effacer » : le dossier est renommé tout de suite (une image retéléchargée
        // va dans un dossier neuf, jamais effacée par erreur), puis effacé en arrière-plan.
        // Un téléchargement en cours à ce moment échoue (image absente jusqu'au prochain
        // affichage). Les images seront téléchargées de nouveau.
        public static void Clear()
        {
            if (dir == null) return;
            try
            {
                Trimmed += files.Count;
                files.Clear();
                TotalBytes = 0;
                string old = dir + ".old" + DateTime.UtcNow.Ticks;
                Directory.Move(dir, old);
                Directory.CreateDirectory(dir);
                Task.Run(() => { try { Directory.Delete(old, true); } catch (Exception) { } });
            }
            catch (Exception) { try { Directory.CreateDirectory(dir); } catch (Exception) { } }
        }

        // Place libre sur le disque de la TV, en octets (-1 si inconnue).
        public static long FreeBytes()
        {
            try { return dir == null ? -1 : new DriveInfo(dir).AvailableFreeSpace; }
            catch (Exception) { return -1; }
        }

        // Fichier que la TV n'a pas pu lire : effacé, il sera téléchargé de nouveau.
        public static void Forget(string path)
        {
            if (path == null) return;
            files.Remove(Path.GetFileNameWithoutExtension(path));
            try { long n = new FileInfo(path).Length; File.Delete(path); TotalBytes -= n; } catch (Exception) { }
        }

        static string Key(string url)
        {
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                var sb = new StringBuilder(40);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // Comme Request, avec des images de secours : si l'une ne se télécharge pas
        // (absente du serveur…), la suivante est demandée. « done » reçoit le premier
        // fichier obtenu, ou null si aucune n'a pu l'être.
        public static void RequestAny(IList<string> urls, Func<int> priority, Action<string> done, int from = 0)
        {
            if (urls == null || from >= urls.Count) { done(null); return; }
            Request(urls[from], priority, path =>
            {
                if (path != null || from + 1 >= urls.Count) done(path);
                else
                {
                    Fallbacks++;
                    string next = urls[from + 1];
                    try { next = new Uri(next).PathAndQuery; } catch (Exception) { }
                    Probe.Log("imgfallback", (from + 1) + "/" + (urls.Count - 1) + " " + (next.Length > 90 ? next.Substring(0, 90) : next));
                    RequestAny(urls, priority, done, from + 1);
                }
            });
        }

        public static int Fallbacks; // images de secours demandées

        // Demande une image. « done » reçoit le chemin du fichier (ou null en cas
        // d'échec), tout de suite si elle est déjà sur le disque.
        public static void Request(string url, Func<int> priority, Action<string> done)
        {
            if (url == null) { done(null); return; }
            if (dir == null) Init();
            if (dir == null) { done(null); return; }
            string key = Key(url);
            if (files.TryGetValue(key, out string path))
            {
                if (File.Exists(path))
                {
                    Hits++;
                    // Date de dernière utilisation : le nettoyage efface les moins utilisés.
                    Task.Run(() => { try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (Exception) { } });
                    done(path);
                    return;
                }
                files.Remove(key);
                Missing++;
            }
            if (!jobs.TryGetValue(url, out Job job))
            {
                job = new Job { Url = url };
                jobs[url] = job;
            }
            job.Waiters.Add(new Waiter { Priority = priority, Done = done });
            Pump();
        }

        // Lance les téléchargements les plus prioritaires tant qu'il y a de la place.
        public static void Pump()
        {
            while (running < Parallel)
            {
                Job best = null;
                int bestPrio = int.MaxValue;
                var gone = new List<string>();
                foreach (Job j in jobs.Values)
                {
                    if (j.Running) continue;
                    int p = int.MaxValue;
                    for (int i = j.Waiters.Count - 1; i >= 0; i--)
                    {
                        int wp;
                        try { wp = j.Waiters[i].Priority(); } catch (Exception) { wp = int.MaxValue; }
                        if (wp == int.MaxValue) j.Waiters.RemoveAt(i);
                        else if (wp < p) p = wp;
                    }
                    if (j.Waiters.Count == 0) { gone.Add(j.Url); continue; }
                    if (p < bestPrio) { bestPrio = p; best = j; }
                }
                foreach (string u in gone) jobs.Remove(u);
                if (best == null) return;
                Download(best);
            }
        }

        // Téléchargement écrit directement dans un fichier, par morceaux : pas de
        // tableau de la taille de l'image en mémoire (une image de fond fait jusqu'à
        // 770 Ko ; ces gros tableaux provoquaient des ramasse-miettes complets).
        static async void Download(Job job)
        {
            job.Running = true;
            running++;
            string path = null;
            long t0 = clock.ElapsedMilliseconds;
            string key = Key(job.Url);
            string part = Path.Combine(dir, key + ".part");
            try
            {
                string ext = await Fetch(job.Url, part);
                // Seulement les 500 dernières durées : la liste ne grossit plus pendant toute la
                // soirée, et le panneau de mesures la trie chaque seconde.
                if (Downloads.Count >= MaxDownloads) Downloads.RemoveAt(0);
                Downloads.Add(clock.ElapsedMilliseconds - t0);
                // La TV ne lit pas le WebP : on redemande l'image en JPEG (paramètre
                // « format » de l'API Jellyfin, pris en compte ou non par le serveur).
                if (ext == ".webp" && !job.Url.Contains("format="))
                {
                    WebpReceived++;
                    string part2 = part + "2";
                    try
                    {
                        string e2 = await Fetch(job.Url + (job.Url.Contains("?") ? "&" : "?") + "format=Jpg", part2);
                        if (e2 == ".jpg" || e2 == ".png")
                        {
                            File.Delete(part);
                            File.Move(part2, part);
                            ext = e2;
                            WebpConverted++;
                        }
                    }
                    catch (Exception) { }
                    try { File.Delete(part2); } catch (Exception) { }
                }
                string p = Path.Combine(dir, key + ext);
                if (File.Exists(p)) File.Delete(p);
                File.Move(part, p);
                files[key] = p;
                TotalBytes += new FileInfo(p).Length;
                path = p;
            }
            catch (Exception e)
            {
                Failures++;
                // Journal de la sonde : cause et chemin de l'image (sans l'adresse du serveur).
                string where = job.Url;
                try { where = new Uri(job.Url).PathAndQuery; } catch (Exception) { }
                Probe.Log("imgfail", e.Message + " " + where);
                try { File.Delete(part); } catch (Exception) { }
            }
            running--;
            jobs.Remove(job.Url);
            foreach (Waiter w in job.Waiters)
            {
                try { w.Done(path); } catch (Exception) { }
            }
            Pump();
        }

        // Télécharge vers « file » ; renvoie l'extension d'après les premiers octets.
        static async Task<string> Fetch(string url, string file)
        {
            using (var res = await Api.Http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
            {
                if (!res.IsSuccessStatusCode) throw new Exception("HTTP " + (int)res.StatusCode);
                using (var src = await res.Content.ReadAsStreamAsync())
                using (var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 16384, true))
                    await src.CopyToAsync(dst, 16384);
            }
            var head = new byte[16];
            int n;
            using (var f = File.OpenRead(file)) n = f.Read(head, 0, head.Length);
            return n < head.Length ? ".bin" : Extension(head);
        }

        // Extension d'après le contenu : la TV choisit son décodeur avec.
        static string Extension(byte[] d)
        {
            if (d.Length > 3 && d[0] == 0x89 && d[1] == 0x50) return ".png";
            if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8) return ".jpg";
            if (d.Length > 12 && d[0] == 0x52 && d[1] == 0x49 && d[8] == 0x57 && d[9] == 0x45) return ".webp";
            if (d.Length > 3 && d[0] == 0x47 && d[1] == 0x49) return ".gif";
            if (d.Length > 4 && d[0] == 0x3C) return ".svg"; // « < » : SVG ou page d'erreur
            return ".bin"; // inconnu : la TV ne s'en verra pas confier l'affichage
        }
    }
}
