// Bandes-annonces YouTube des cartes ouvertes (module 8), reprises du POC trailer/ et de la
// version web (lib/hosts/tizen/youtube.ts, lib/playback/trailers.ts, lib/playback/bars.ts) :
// - lien demandé à l'API du lecteur YouTube, client VISIONOS puis ANDROID (ceux du web) ;
//   une réponse refusée qui donne un visitorData est redemandée avec lui : sans ce second
//   appel, YouTube coupe le flux au bout d'environ 40 s (vérifié avec le POC) ;
// - vidéo H.264 et son AAC séparés, décrits dans un manifeste DASH servi par un petit serveur
//   local (127.0.0.1:8124 ; le lecteur refuse un fichier .mpd local ; 8123 est la sonde) ;
//   morceaux listés avec leurs plages d'octets (boîte sidx de chaque fichier), relayés vers
//   YouTube avec 25 s d'avance au plus sur la lecture, comme dans le POC (pas vérifié qu'on
//   puisse s'en passer) ;
// - carte : départ sur la meilleure taille jusqu'à 720p, montée possible jusqu'à 1080p (H.264) ;
// - plein écran (page du titre) : H.264 en 1080p (la meilleure taille jusqu'à 1080p). Au-dessus,
//   YouTube ne donne que de l'AV1 ou du VP9, que le lecteur de la TV refuse en DASH (essayés le
//   2026-11-11, voir LECONS.md) : l'auteur a choisi de rester en 1080p ;
// - sans DASH : la variante HLS jusqu'à 720p, sinon le MP4 360p tout-en-un ;
// - bandes noires repérées sur trois petites images de la vidéo (mq1 à mq3), mémorisées par vidéo.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Tizen.Multimedia;
using Tizen.Multimedia.Util;

namespace NativeAIO.Core
{
    static class Trailers
    {
        public const int Port = 8124;
        // Avance accordée au lecteur sur la position de lecture (POC : YouTube refuse un
        // client qui demande la vidéo beaucoup plus vite qu'il ne la lit).
        const long AheadMs = 25000;
        const int MaxHeight = 1080, StartHeight = 720;
        const long KeepMs = 3600_000; // liens YouTube valables quelques heures : gardés 1 h
        public const string UA = "Mozilla/5.0 (SMART-TV; LINUX; Tizen 6.0) AppleWebKit/537.36 (KHTML, like Gecko) 76.0.3809.146/6.0 TV Safari/537.36";
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        static readonly (string Name, string Id, string Version, string Key, string Fields)[] Clients =
        {
            ("VISIONOS", "101", "1.02", null, "\"deviceMake\":\"Apple\",\"deviceModel\":\"RealityDevice17,1\",\"osName\":\"visionOS\",\"osVersion\":\"26.5.23O471\""),
            ("ANDROID", "3", "20.10.41", "AIzaSyA8eiZmM1FaDVjRy-df2KTyQ_vz_yYM39w", "\"deviceMake\":\"Google\",\"deviceModel\":\"Pixel 5\",\"osName\":\"Android\",\"osVersion\":\"11\",\"androidSdkVersion\":\"30\",\"platform\":\"MOBILE\""),
        };

        // Une vidéo résolue : l'adresse à confier au lecteur et, pour le relais, les liens
        // YouTube de ses fichiers et le début de chaque morceau.
        public class Stream
        {
            public string Address, Kind, Client;
            // Manifestes à essayer dans l'ordre (le premier est Address).
            public readonly List<Choice> Choices = new List<Choice>();
            public long Ms, At;
            internal readonly ConcurrentDictionary<string, string> Mpds = new ConcurrentDictionary<string, string>();
            internal readonly ConcurrentDictionary<string, string> Files = new ConcurrentDictionary<string, string>();
            internal readonly ConcurrentDictionary<string, long> SegStart = new ConcurrentDictionary<string, long>();
        }

        public class Choice
        {
            public string Address, Kind;
        }

        static readonly ConcurrentDictionary<string, Stream> streams = new ConcurrentDictionary<string, Stream>();
        static readonly ConcurrentDictionary<string, Task<Stream>> resolving = new ConcurrentDictionary<string, Task<Stream>>();

        // Lecture en cours, pour le rythme des morceaux.
        static long playedMs;
        static int generation;
        public static int SegOk, SegHeld, SegFailed;
        public static string SegLast = "";

        public static void Begin()
        {
            Interlocked.Exchange(ref playedMs, 0);
            Interlocked.Increment(ref generation);
            SegOk = SegFailed = 0;
            SegLast = "";
        }

        public static void Played(long ms) => Interlocked.Exchange(ref playedMs, ms);

        // Lecture arrêtée : les morceaux retenus sont abandonnés.
        public static void End() => Interlocked.Increment(ref generation);

        // Identifiant d'une vidéo YouTube dans un lien (youTubeId du web), sinon null.
        public static string YouTubeId(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri u)) return null;
            string host = Regex.Replace(u.Host.ToLowerInvariant(), @"^(www|m)\.", "");
            string id = null;
            if (host == "youtu.be") id = u.AbsolutePath.TrimStart('/');
            else if (host == "youtube.com" || host == "youtube-nocookie.com")
            {
                Match q = Regex.Match(u.Query, @"[?&]v=([^&]+)");
                if (q.Success) id = Uri.UnescapeDataString(q.Groups[1].Value);
                else
                {
                    Match p = Regex.Match(u.AbsolutePath, @"^/(?:embed|shorts|v)/([^/]+)");
                    if (p.Success) id = p.Groups[1].Value;
                }
            }
            return id != null && Regex.IsMatch(id, @"^[\w-]{11}$") ? id : null;
        }

        // ---- Résolution ----

        // Le flux d'une vidéo, ou null si YouTube n'en donne aucun : pour une carte, ou pour le
        // plein écran (full). Une seule demande à la fois par vidéo ; résultat gardé 1 h.
        public static Task<Stream> Resolve(string id, bool full = false)
        {
            string key = full ? id + "-full" : id;
            if (streams.TryGetValue(key, out Stream s) && Probe.Now - s.At < KeepMs) return Task.FromResult(s);
            return resolving.GetOrAdd(key, k => Task.Run(async () =>
            {
                try { return await DoResolve(id, k, full); }
                finally { resolving.TryRemove(k, out _); }
            }));
        }

        static bool Avc(Format f) => f.MimeType.Contains("avc1");
        static async Task<Stream> DoResolve(string id, string key, bool full)
        {
            long t0 = Probe.Now;
            bool served = EnsureServer();
            Stream fallback = null;
            foreach (var c in Clients)
            {
                StreamingData sd;
                try { sd = await Ask(c, id); }
                catch (Exception e) { Probe.Log("trailer", $"{c.Name} {e.GetType().Name} {e.Message}"); continue; }
                if (sd == null) continue;
                var st = new Stream { Client = c.Name };
                if (served && sd.AdaptiveFormats != null)
                {
                    try
                    {
                        if (await Manifests(st, key, sd.AdaptiveFormats, full)) return Keep(key, st, t0);
                    }
                    catch (Exception e) { Probe.Log("trailer", $"{c.Name} DASH impossible : {e.GetType().Name} {e.Message}"); }
                }
                if (fallback != null) continue;
                string hls = null;
                if (sd.HlsManifestUrl != null)
                {
                    try { hls = await Variant(sd.HlsManifestUrl); } catch (Exception) { }
                }
                string muxed = sd.Formats?.FirstOrDefault(f => f.Url != null)?.Url;
                if (hls != null) fallback = new Stream { Client = c.Name, Address = hls, Kind = "HLS" };
                else if (muxed != null) fallback = new Stream { Client = c.Name, Address = muxed, Kind = "MP4 360p" };
                if (fallback != null) Probe.Log("trailer", $"{c.Name} sans DASH : {fallback.Kind}");
            }
            return fallback != null ? Keep(key, fallback, t0) : null;
        }

        // Manifestes d'une vidéo : faux s'il n'y a pas de quoi en faire un.
        static async Task<bool> Manifests(Stream st, string key, Format[] formats, bool full)
        {
            if (!full)
            {
                var card = await Dash(st, key, formats, Avc, StartHeight);
                if (card == null) return false;
                st.Mpds["main"] = card.Value.Mpd;
                st.Address = $"http://127.0.0.1:{Port}/{key}/main.mpd";
                st.Kind = card.Value.Kind;
                return true;
            }
            var avc = await Dash(st, key, formats, Avc, MaxHeight);
            if (avc != null)
            {
                st.Mpds["avc"] = avc.Value.Mpd;
                st.Choices.Add(new Choice { Address = $"http://127.0.0.1:{Port}/{key}/avc.mpd", Kind = "H.264 " + avc.Value.Kind });
            }
            if (st.Choices.Count == 0) return false;
            st.Address = st.Choices[0].Address;
            st.Kind = st.Choices[0].Kind;
            return true;
        }

        static Stream Keep(string id, Stream st, long t0)
        {
            st.At = Probe.Now;
            st.Ms = st.At - t0;
            // Les vieilles résolutions sont oubliées (liens expirés, mémoire).
            foreach (var kv in streams) if (st.At - kv.Value.At > KeepMs) streams.TryRemove(kv.Key, out _);
            streams[id] = st;
            Probe.Log("trailer", $"{id} résolu en {st.Ms} ms par {st.Client} : {st.Kind}");
            return st;
        }

        static async Task<StreamingData> Ask((string Name, string Id, string Version, string Key, string Fields) c, string id)
        {
            string visitor = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string body = "{\"videoId\":\"" + id + "\",\"context\":{\"client\":{\"clientName\":\"" + c.Name + "\",\"clientVersion\":\"" + c.Version + "\",\"hl\":\"en\",\"gl\":\"US\","
                    + (visitor != null ? "\"visitorData\":\"" + visitor + "\"," : "") + c.Fields + "}},\"contentCheckOk\":true,\"racyCheckOk\":true}";
                var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?" + (c.Key != null ? "key=" + c.Key + "&" : "") + "prettyPrint=false")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("User-Agent", UA);
                req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", c.Id);
                req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", c.Version);
                if (visitor != null) req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
                Answer answer;
                using (HttpResponseMessage res = await http.SendAsync(req))
                {
                    if (!res.IsSuccessStatusCode) throw new Exception("HTTP " + (int)res.StatusCode);
                    using (System.IO.Stream s = await res.Content.ReadAsStreamAsync())
                        answer = (Answer)new DataContractJsonSerializer(typeof(Answer)).ReadObject(s);
                }
                if (answer?.Playability?.Status == "OK" && answer.StreamingData != null) return answer.StreamingData;
                // Sans identifiant de visiteur, YouTube refuse, mais en donne un pour la demande suivante.
                visitor = answer?.Context?.VisitorData;
                if (visitor == null)
                {
                    Probe.Log("trailer", $"{c.Name} {answer?.Playability?.Status ?? "?"} {answer?.Playability?.Reason ?? ""}");
                    return null;
                }
            }
            Probe.Log("trailer", c.Name + " : refusé deux fois");
            return null;
        }

        // La liste de la plus grande taille jusqu'à 720p d'une liste HLS principale : le
        // lecteur de la TV reste sur la première taille de la liste principale (POC).
        static async Task<string> Variant(string master)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, master);
            req.Headers.TryAddWithoutValidation("User-Agent", UA);
            string[] lines;
            using (HttpResponseMessage res = await http.SendAsync(req))
                lines = (await res.Content.ReadAsStringAsync()).Split('\n');
            string best = null;
            int bestH = 0;
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                Match m = Regex.Match(lines[i], @"RESOLUTION=\d+x(\d+)");
                if (!lines[i].StartsWith("#EXT-X-STREAM-INF") || !m.Success) continue;
                int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (h <= StartHeight && h > bestH) { bestH = h; best = new Uri(new Uri(master), lines[i + 1].Trim()).ToString(); }
            }
            return best;
        }

        // Manifeste DASH des tailles d'un codec, de la meilleure jusqu'à startHeight à maxHeight,
        // et du son AAC par défaut (dashOf du web) : le lecteur démarre sur la plus petite
        // taille donnée. Null s'il manque la vidéo, le son ou la durée.
        static async Task<(string Mpd, string Kind, int Top)?> Dash(Stream st, string key, Format[] formats, Func<Format, bool> codec, int startHeight, int maxHeight = MaxHeight)
        {
            var usable = formats.Where(f => f.Url != null && f.InitRange != null && f.IndexRange != null && f.MimeType != null && f.MimeType.Contains("codecs=\"")).ToList();
            var video = usable.Where(f => f.MimeType.StartsWith("video/mp4") && codec(f) && f.Height > 0 && f.Height <= maxHeight).ToList();
            var aac = usable.Where(f => f.MimeType.StartsWith("audio/mp4") && f.MimeType.Contains("mp4a")).ToList();
            // La langue d'origine plutôt qu'un doublage, puis le meilleur débit.
            Format audio = aac.Where(f => f.AudioTrack?.AudioIsDefault != false).OrderByDescending(f => f.Bitrate).FirstOrDefault() ?? aac.FirstOrDefault();
            long.TryParse(formats.FirstOrDefault(f => f.ApproxDurationMs != null)?.ApproxDurationMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out long duration);
            if (video.Count == 0 || audio == null || duration == 0) return null;
            int start = video.Where(f => f.Height <= startHeight).Select(f => f.Height).DefaultIfEmpty(video.Min(f => f.Height)).Max();
            video = video.Where(f => f.Height >= start).GroupBy(f => f.Height).Select(g => g.OrderByDescending(f => f.Bitrate).First()).OrderBy(f => f.Height).ToList();
            // Index des fichiers demandés ensemble.
            var reps = new List<Task<string>>();
            foreach (Format v in video) reps.Add(Rep(st, key, v, true));
            Task<string> audioRep = Rep(st, key, audio, false);
            await Task.WhenAll(reps.Concat(new[] { audioRep }));
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" minBufferTime=\"PT2S\" mediaPresentationDuration=\"PT")
              .Append((duration / 1000.0).ToString("0.###", CultureInfo.InvariantCulture))
              .Append("S\" profiles=\"urn:mpeg:dash:profile:isoff-on-demand:2011\"><Period><AdaptationSet mimeType=\"video/mp4\" segmentAlignment=\"true\">");
            foreach (Task<string> r in reps) sb.Append(r.Result);
            sb.Append("</AdaptationSet><AdaptationSet mimeType=\"audio/mp4\" segmentAlignment=\"true\">").Append(audioRep.Result).Append("</AdaptationSet></Period></MPD>");
            int top = video[video.Count - 1].Height;
            return (sb.ToString(), video.Count == 1 ? $"DASH {start}p" : $"DASH {start}p à {top}p", top);
        }

        // Une taille du manifeste, ses morceaux lus dans l'index du fichier (boîte sidx) et
        // listés avec leurs plages d'octets : demandé entier à partir d'un point, YouTube
        // coupe le fichier après quelques mégaoctets et le lecteur s'arrête (POC).
        static async Task<string> Rep(Stream st, string id, Format f, bool isVideo)
        {
            string codecs = f.MimeType.Split(new[] { "codecs=\"" }, StringSplitOptions.None)[1].TrimEnd('"');
            long indexStart = long.Parse(f.IndexRange.Start, CultureInfo.InvariantCulture), indexEnd = long.Parse(f.IndexRange.End, CultureInfo.InvariantCulture);
            var req = new HttpRequestMessage(HttpMethod.Get, f.Url);
            req.Headers.TryAddWithoutValidation("User-Agent", UA);
            req.Headers.TryAddWithoutValidation("Range", $"bytes={indexStart}-{indexEnd}");
            byte[] sidx;
            using (HttpResponseMessage res = await http.SendAsync(req))
            {
                if (!res.IsSuccessStatusCode) throw new Exception("index HTTP " + (int)res.StatusCode);
                sidx = await res.Content.ReadAsByteArrayAsync();
            }
            if (sidx.Length < 32 || Encoding.ASCII.GetString(sidx, 4, 4) != "sidx") throw new Exception("index illisible");
            uint U32(int o) => (uint)(sidx[o] << 24 | sidx[o + 1] << 16 | sidx[o + 2] << 8 | sidx[o + 3]);
            int version = sidx[8];
            uint timescale = U32(16);
            ulong first = version == 0 ? U32(24) : ((ulong)U32(28) << 32 | U32(32));
            int refsAt = version == 0 ? 28 : 36;
            int count = sidx[refsAt + 2] << 8 | sidx[refsAt + 3];
            if (timescale == 0 || sidx.Length < refsAt + 4 + count * 12) throw new Exception("index incomplet");
            string itag = f.Itag.ToString(CultureInfo.InvariantCulture);
            var list = new StringBuilder();
            list.Append($"<SegmentList timescale=\"{timescale}\"><Initialization range=\"{f.InitRange.Start}-{f.InitRange.End}\"/><SegmentTimeline>");
            var urls = new StringBuilder();
            long at = indexEnd + 1 + (long)first;
            ulong t = 0;
            for (int i = 0; i < count; i++)
            {
                int r = refsAt + 4 + i * 12;
                long bytes = U32(r) & 0x7fffffff;
                uint d = U32(r + 4);
                list.Append($"<S t=\"{t}\" d=\"{d}\"/>");
                st.SegStart[$"{itag}:{at}"] = (long)(t * 1000 / timescale);
                urls.Append($"<SegmentURL mediaRange=\"{at}-{at + bytes - 1}\"/>");
                at += bytes;
                t += d;
            }
            list.Append("</SegmentTimeline>").Append(urls).Append("</SegmentList>");
            st.Files[itag] = f.Url;
            string size = isVideo ? $" width=\"{f.Width}\" height=\"{f.Height}\"" : "";
            return $"<Representation id=\"{itag}\" bandwidth=\"{f.Bitrate}\" codecs=\"{codecs}\"{size}><BaseURL>http://127.0.0.1:{Port}/{id}/seg/{itag}</BaseURL>{list}</Representation>";
        }

        // ---- Serveur local ----

        static HttpListener server;
        static bool serverFailed;
        static readonly object serverLock = new object();

        static bool EnsureServer()
        {
            lock (serverLock)
            {
                if (server != null) return true;
                if (serverFailed) return false;
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add($"http://127.0.0.1:{Port}/");
                    l.Start();
                    server = l;
                    Task.Run(Loop);
                    return true;
                }
                catch (Exception e)
                {
                    serverFailed = true;
                    Probe.Log("trailer", "serveur local impossible : " + e.Message);
                    return false;
                }
            }
        }

        static async Task Loop()
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); }
                catch (Exception) { return; }
                _ = Serve(ctx);
            }
        }

        // « /{vidéo}/{nom}.mpd » : un manifeste ; « /{vidéo}/seg/{itag} » : un morceau, relayé.
        static async Task Serve(HttpListenerContext ctx)
        {
            try
            {
                string[] parts = ctx.Request.Url.AbsolutePath.Trim('/').Split('/');
                if (parts.Length == 2 && parts[1].EndsWith(".mpd") && streams.TryGetValue(parts[0], out Stream st)
                    && st.Mpds.TryGetValue(parts[1].Substring(0, parts[1].Length - 4), out string mpd))
                {
                    byte[] data = Encoding.UTF8.GetBytes(mpd);
                    ctx.Response.ContentType = "application/dash+xml";
                    ctx.Response.ContentLength64 = data.Length;
                    await ctx.Response.OutputStream.WriteAsync(data, 0, data.Length);
                }
                else if (parts.Length == 3 && parts[1] == "seg" && streams.TryGetValue(parts[0], out Stream s2))
                {
                    await Relay(ctx, s2, parts[2]);
                }
                else ctx.Response.StatusCode = 404;
            }
            catch (Exception) { }
            try { ctx.Response.Close(); } catch (Exception) { }
        }

        // Transmet la demande d'un morceau à YouTube, avec la plage d'octets du lecteur, au plus
        // 25 s en avance sur la lecture.
        static async Task Relay(HttpListenerContext ctx, Stream st, string itag)
        {
            string range = ctx.Request.Headers["Range"];
            if (!st.Files.TryGetValue(itag, out string url)) { ctx.Response.StatusCode = 404; return; }
            int mine = Volatile.Read(ref generation);
            Match from = Regex.Match(range ?? "", @"bytes=(\d+)-");
            if (from.Success && st.SegStart.TryGetValue($"{itag}:{from.Groups[1].Value}", out long startMs) && startMs > Volatile.Read(ref playedMs) + AheadMs)
            {
                Interlocked.Increment(ref SegHeld);
                try
                {
                    while (startMs > Volatile.Read(ref playedMs) + AheadMs && mine == Volatile.Read(ref generation)) await Task.Delay(500);
                }
                finally { Interlocked.Decrement(ref SegHeld); }
                if (mine != Volatile.Read(ref generation)) { ctx.Response.StatusCode = 503; return; }
            }
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", UA);
                if (range != null) req.Headers.TryAddWithoutValidation("Range", range);
                using (HttpResponseMessage res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                {
                    ctx.Response.StatusCode = (int)res.StatusCode;
                    if (res.Content.Headers.ContentRange != null) ctx.Response.AddHeader("Content-Range", res.Content.Headers.ContentRange.ToString());
                    if (res.Content.Headers.ContentLength != null) ctx.Response.ContentLength64 = res.Content.Headers.ContentLength.Value;
                    ctx.Response.ContentType = res.Content.Headers.ContentType?.ToString() ?? "video/mp4";
                    if (res.IsSuccessStatusCode) Interlocked.Increment(ref SegOk);
                    else { Interlocked.Increment(ref SegFailed); SegLast = $"HTTP {(int)res.StatusCode}"; }
                    // Tampon de 64 Ko : sous le seuil des gros objets (ramasse-miettes complets).
                    using (System.IO.Stream body = await res.Content.ReadAsStreamAsync())
                        await body.CopyToAsync(ctx.Response.OutputStream, 65536);
                }
            }
            catch (Exception e)
            {
                // Lecteur fermé pendant la copie : normal quand on quitte la carte.
                if (mine == Volatile.Read(ref generation)) { Interlocked.Increment(ref SegFailed); SegLast = e.GetType().Name; }
            }
        }

        // ---- Bandes noires (lib/playback/bars.ts du web) ----

        const int Level = 24; // valeur la plus forte d'un pixel de bande
        const double Margin = 0.005, Most = 0.3;
        const int MaxCrops = 500;
        static readonly object cropLock = new object();
        static List<KeyValuePair<string, double>> crops;
        static string CropFile => Tizen.Applications.Application.Current.DirectoryInfo.Data + "trailer-crops.txt";

        // Part de chaque bord de l'image à couper pour que l'image, sans ses bandes noires,
        // remplisse une carte en 16:9 ; -1 si aucune image de la vidéo n'est lisible.
        public static async Task<double> Crop(string id)
        {
            lock (cropLock)
            {
                LoadCrops();
                int i = crops.FindIndex(x => x.Key == id);
                if (i >= 0) return crops[i].Value;
            }
            double c = await Task.Run(() => ReadCrop(id));
            if (c < 0) return c;
            lock (cropLock)
            {
                crops.RemoveAll(x => x.Key == id);
                crops.Add(new KeyValuePair<string, double>(id, c));
                if (crops.Count > MaxCrops) crops.RemoveRange(0, crops.Count - MaxCrops);
                try { File.WriteAllLines(CropFile, crops.Select(x => x.Key + "\t" + x.Value.ToString("0.####", CultureInfo.InvariantCulture))); }
                catch (Exception) { }
            }
            return c;
        }

        static void LoadCrops()
        {
            if (crops != null) return;
            crops = new List<KeyValuePair<string, double>>();
            try
            {
                if (!File.Exists(CropFile)) return;
                foreach (string line in File.ReadAllLines(CropFile))
                {
                    string[] p = line.Split('\t');
                    if (p.Length == 2 && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        crops.Add(new KeyValuePair<string, double>(p[0], v));
                }
            }
            catch (Exception) { }
        }

        // Trois images prises au quart, à la moitié et aux trois quarts de la vidéo (320×180) :
        // pour chacune, les bandes du haut et du bas, de gauche et de droite ; la valeur du
        // milieu l'emporte (un titre clair dans une bande, une scène sombre aux bords).
        static async Task<double> ReadCrop(string id)
        {
            var jpegs = await Task.WhenAll(new[] { "mq1", "mq2", "mq3" }.Select(async name =>
            {
                try { return await http.GetByteArrayAsync($"https://i.ytimg.com/vi/{id}/{name}.jpg"); }
                catch (Exception) { return null; }
            }));
            var rows = new List<double>();
            var cols = new List<double>();
            foreach (byte[] jpeg in jpegs)
            {
                if (jpeg == null) continue;
                try
                {
                    BitmapFrame img;
                    using (var dec = new JpegDecoder())
                    {
                        dec.SetColorSpace(ColorSpace.Rgba8888);
                        img = (await dec.DecodeAsync(jpeg)).First();
                    }
                    if (Edges(img.Buffer, img.Size.Width, img.Size.Height, out double r, out double c)) { rows.Add(r); cols.Add(c); }
                }
                catch (Exception e) { Probe.Log("trailer", "image illisible : " + e.Message); }
            }
            if (rows.Count == 0) return -1;
            return Math.Max(Combine(rows), Combine(cols));
        }

        // edgesIn du web : faux pour une image toute sombre (fondu, image noire).
        static bool Edges(byte[] px, int w, int h, out double rows, out double cols)
        {
            rows = cols = 0;
            if (w <= 0 || h <= 0 || px.Length < w * h * 4) return false;
            bool Dark(int x, int y)
            {
                int o = (y * w + x) * 4;
                return Math.Max(px[o], Math.Max(px[o + 1], px[o + 2])) <= Level;
            }
            bool DarkRow(int y) { for (int x = 0; x < w; x++) if (!Dark(x, y)) return false; return true; }
            bool DarkCol(int x) { for (int y = 0; y < h; y++) if (!Dark(x, y)) return false; return true; }
            int top = 0;
            while (top < h / 2 && DarkRow(top)) top++;
            if (top >= h / 2) return false;
            int bottom = 0;
            while (bottom < h / 2 && DarkRow(h - 1 - bottom)) bottom++;
            int left = 0;
            while (left < w / 2 && DarkCol(left)) left++;
            int right = 0;
            while (right < w / 2 && DarkCol(w - 1 - right)) right++;
            rows = Math.Min(top, bottom) / (double)h;
            cols = Math.Min(left, right) / (double)w;
            return true;
        }

        // combineBars du web : la valeur du milieu ; avec deux images, la plus petite.
        static double Combine(List<double> bars)
        {
            if (bars.Count == 0) return 0;
            var sorted = bars.OrderBy(b => b).ToList();
            double middle = sorted.Count == 2 ? sorted[0] : sorted[(sorted.Count - 1) / 2];
            return middle > 0 ? Math.Min(middle + Margin, Most) : 0;
        }

#pragma warning disable CS0649 // champs remplis par DataContractJsonSerializer
        // ---- Réponse de YouTube ----

        [DataContract] class Answer
        {
            [DataMember(Name = "playabilityStatus")] public Playability Playability;
            [DataMember(Name = "responseContext")] public ResponseContext Context;
            [DataMember(Name = "streamingData")] public StreamingData StreamingData;
        }
        [DataContract] class Playability { [DataMember(Name = "status")] public string Status; [DataMember(Name = "reason")] public string Reason; }
        [DataContract] class ResponseContext { [DataMember(Name = "visitorData")] public string VisitorData; }
        [DataContract] class StreamingData
        {
            [DataMember(Name = "hlsManifestUrl")] public string HlsManifestUrl;
            [DataMember(Name = "formats")] public Format[] Formats;
            [DataMember(Name = "adaptiveFormats")] public Format[] AdaptiveFormats;
        }
        [DataContract] class Format
        {
            [DataMember(Name = "itag")] public int Itag;
            [DataMember(Name = "url")] public string Url;
            [DataMember(Name = "mimeType")] public string MimeType;
            [DataMember(Name = "bitrate")] public long Bitrate;
            [DataMember(Name = "width")] public int Width;
            [DataMember(Name = "height")] public int Height;
            [DataMember(Name = "initRange")] public ByteRange InitRange;
            [DataMember(Name = "indexRange")] public ByteRange IndexRange;
            [DataMember(Name = "approxDurationMs")] public string ApproxDurationMs;
            [DataMember(Name = "audioTrack")] public AudioTrack AudioTrack;
        }
        [DataContract] class ByteRange { [DataMember(Name = "start")] public string Start; [DataMember(Name = "end")] public string End; }
        [DataContract] class AudioTrack { [DataMember(Name = "audioIsDefault")] public bool? AudioIsDefault; }
    }
}
