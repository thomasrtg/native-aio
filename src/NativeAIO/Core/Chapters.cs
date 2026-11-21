// Chapitres d'un fichier MKV, lus par l'application dans l'en-tête du fichier (le lecteur de
// la TV ne les donne pas, le serveur non plus), puis reconnus comme intro, récap, générique ou
// aperçu, avec les règles de la version web (lib/playback/chapters.ts) : noms des chapitres,
// sinon durée des chapitres seulement numérotés (génériques d'anime).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NativeAIO.Core
{
    class Chapter
    {
        public string Title;
        public int StartMs;
    }

    // Moment à passer, comme un segment du serveur.
    class Skippable
    {
        public string Type;
        public int Start, End;
    }

    static class Chapters
    {
        const int Chunk = 64 * 1024; // moins de 85 Ko : pas de ramasse-miettes complet (LECONS.md)
        const uint EbmlId = 0x1A45DFA3, SegmentId = 0x18538067, SeekHeadId = 0x114D9B74, SeekId = 0x4DBB,
            SeekIdId = 0x53AB, SeekPosId = 0x53AC, ChaptersId = 0x1043A770, EditionId = 0x45B9, AtomId = 0xB6,
            StartId = 0x91, HiddenId = 0x98, DisplayId = 0x80, StringId = 0x85, ClusterId = 0x1F43B675;

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // ---- Lecture ----

        // Chapitres du fichier (vide si ce n'est pas un MKV ou s'il n'en a pas). Deux lectures
        // de 64 Ko au plus : le début du fichier, puis l'endroit indiqué par l'index (SeekHead)
        // si les chapitres n'y sont pas. Lu avant d'ouvrir le lecteur, pour ne jamais avoir deux
        // connexions au même lien. L'adresse contient des clés : jamais dans un message.
        public static async Task<List<Chapter>> Read(string url, CancellationToken cancel)
        {
            byte[] head = await Range(url, 0, cancel);
            if (head.Length < 4 || U(head, 0, 4) != EbmlId) return new List<Chapter>();
            int p = 0;
            Element ebml = Next(head, ref p);
            if (ebml == null) return new List<Chapter>();
            p = ebml.Data + (int)ebml.Size;
            Element seg = Next(head, ref p);
            if (seg == null || seg.Id != SegmentId) return new List<Chapter>();
            long segStart = seg.Data;
            long chaptersAt = -1;
            p = seg.Data;
            while (p < head.Length)
            {
                Element e = Next(head, ref p);
                if (e == null || e.Id == ClusterId) break;
                long end = e.Data + e.Size;
                if (e.Id == ChaptersId && end <= head.Length) return Parse(head, e.Data, (int)end);
                if (e.Id == ChaptersId) { chaptersAt = e.Start; break; }
                if (e.Id == SeekHeadId && end <= head.Length) chaptersAt = SeekTo(head, e.Data, (int)end, segStart);
                if (e.Unknown) break;
                p = (int)Math.Min(end, int.MaxValue);
            }
            if (chaptersAt < 0) return new List<Chapter>();
            byte[] more = await Range(url, chaptersAt, cancel);
            int q = 0;
            Element c = Next(more, ref q);
            if (c == null || c.Id != ChaptersId) return new List<Chapter>();
            return Parse(more, c.Data, (int)Math.Min(more.Length, c.Data + c.Size));
        }

        // 64 Ko à partir de « from » ; si le serveur ignore la demande de plage, le début du flux
        // est lu puis la connexion fermée.
        static async Task<byte[]> Range(string url, long from, CancellationToken cancel)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, from + Chunk - 1);
            using (HttpResponseMessage res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                if (!res.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)res.StatusCode);
                if (from > 0 && res.StatusCode != System.Net.HttpStatusCode.PartialContent) throw new HttpRequestException("plage refusée");
                using (Stream s = await res.Content.ReadAsStreamAsync())
                {
                    var buf = new byte[Chunk];
                    int n = 0, got;
                    while (n < Chunk && (got = await s.ReadAsync(buf, n, Chunk - n, cancel)) > 0) n += got;
                    if (n == Chunk) return buf;
                    var r = new byte[n];
                    Array.Copy(buf, r, n);
                    return r;
                }
            }
        }

        class Element
        {
            public uint Id;
            public long Size;
            public int Start, Data;
            public bool Unknown;
        }

        // Élément EBML à la position p (identifiant, taille, début des données) ; null si coupé.
        static Element Next(byte[] d, ref int p)
        {
            int start = p;
            if (!VInt(d, ref p, true, out ulong id, out _)) return null;
            if (!VInt(d, ref p, false, out ulong size, out bool unknown)) return null;
            return new Element { Id = (uint)id, Size = unknown ? long.MaxValue / 2 : (long)size, Start = start, Data = p, Unknown = unknown };
        }

        static bool VInt(byte[] d, ref int p, bool keepMarker, out ulong v, out bool allOnes)
        {
            v = 0;
            allOnes = false;
            if (p >= d.Length) return false;
            byte b = d[p];
            int len = 1;
            byte mask = 0x80;
            while (len <= 8 && (b & mask) == 0) { mask >>= 1; len++; }
            if (len > 8 || p + len > d.Length) return false;
            v = keepMarker ? b : (ulong)(b & (mask - 1));
            bool ones = (b & (mask - 1)) == mask - 1;
            for (int i = 1; i < len; i++) { v = (v << 8) | d[p + i]; ones &= d[p + i] == 0xFF; }
            allOnes = !keepMarker && ones;
            p += len;
            return true;
        }

        static ulong U(byte[] d, int p, int n)
        {
            ulong v = 0;
            for (int i = 0; i < n && p + i < d.Length; i++) v = (v << 8) | d[p + i];
            return v;
        }

        // Position des chapitres d'après l'index du début du fichier, -1 si absente.
        static long SeekTo(byte[] d, int p, int end, long segStart)
        {
            while (p < end)
            {
                Element seek = Next(d, ref p);
                if (seek == null) return -1;
                int sEnd = (int)Math.Min(end, seek.Data + seek.Size);
                if (seek.Id == SeekId)
                {
                    ulong id = 0, pos = 0;
                    bool has = false;
                    int q = seek.Data;
                    while (q < sEnd)
                    {
                        Element e = Next(d, ref q);
                        if (e == null) break;
                        if (e.Id == SeekIdId) id = U(d, e.Data, (int)Math.Min(8, e.Size));
                        if (e.Id == SeekPosId) { pos = U(d, e.Data, (int)Math.Min(8, e.Size)); has = true; }
                        q = (int)Math.Min(sEnd, e.Data + e.Size);
                    }
                    if (id == ChaptersId && has) return segStart + (long)pos;
                }
                p = sEnd;
            }
            return -1;
        }

        // Première édition : chapitres dans l'ordre, sans les chapitres cachés ni les sous-chapitres.
        static List<Chapter> Parse(byte[] d, int p, int end)
        {
            var list = new List<Chapter>();
            while (p < end)
            {
                Element ed = Next(d, ref p);
                if (ed == null) break;
                int edEnd = (int)Math.Min(end, ed.Data + ed.Size);
                if (ed.Id == EditionId)
                {
                    int q = ed.Data;
                    while (q < edEnd)
                    {
                        Element atom = Next(d, ref q);
                        if (atom == null) break;
                        int aEnd = (int)Math.Min(edEnd, atom.Data + atom.Size);
                        if (atom.Id == AtomId) { Chapter c = Atom(d, atom.Data, aEnd); if (c != null) list.Add(c); }
                        q = aEnd;
                    }
                    break;
                }
                p = edEnd;
            }
            list.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
            return list;
        }

        static Chapter Atom(byte[] d, int p, int end)
        {
            long startNs = -1;
            bool hidden = false;
            string title = null;
            while (p < end)
            {
                Element e = Next(d, ref p);
                if (e == null) break;
                int eEnd = (int)Math.Min(end, e.Data + e.Size);
                if (e.Id == StartId) startNs = (long)U(d, e.Data, (int)Math.Min(8, e.Size));
                else if (e.Id == HiddenId) hidden = U(d, e.Data, (int)Math.Min(8, e.Size)) != 0;
                else if (e.Id == DisplayId && title == null)
                {
                    int q = e.Data;
                    while (q < eEnd)
                    {
                        Element s = Next(d, ref q);
                        if (s == null) break;
                        if (s.Id == StringId) { title = Encoding.UTF8.GetString(d, s.Data, (int)Math.Max(0, Math.Min(s.Size, eEnd - s.Data))); break; }
                        q = (int)Math.Min(eEnd, s.Data + s.Size);
                    }
                }
                p = eEnd;
            }
            if (startNs < 0 || hidden) return null;
            return new Chapter { Title = (title ?? "").Trim(), StartMs = (int)Math.Min(int.MaxValue, startNs / 1000000) };
        }

        // ---- Reconnaissance (lib/playback/chapters.ts du web) ----

        // Un titre entier, ou les mots avant sa première ponctuation, en minuscules et sans
        // chiffres : « OP 2 » et « OP - Song » comptent, « The Opening of the Gates » non.
        static readonly (string, Regex)[] Kinds =
        {
            ("Intro", new Regex("^(op|opening|opening (credits|song|theme)|title sequence|main titles?)$")),
            ("Outro", new Regex("^(ed|ending|ending (credits|song|theme)|end credits|credits|closing credits|outro)$")),
            ("Recap", new Regex("^(recap|previously|previously on)$")),
            ("Preview", new Regex("^(preview|next episode preview|next episode|next time)$")),
        };
        // Nomme parfois la scène d'avant le générique : compte seulement si rien ne nomme le générique.
        static readonly Regex LooseIntro = new Regex("^(intro|introduction)$");
        static readonly Regex Untitled = new Regex("^(chapter)?$");
        static readonly Regex NotLetters = new Regex("[^a-z]+");
        static readonly Regex Lead = new Regex("[^a-zA-Z0-9 '’]");
        const int SongMin = 85000, SongMax = 110000, NamedMax = 180000;

        static string Plain(string title) => NotLetters.Replace(title.ToLowerInvariant(), " ").Trim();

        static bool Names(string title, Regex pattern)
        {
            string lead = Lead.Split(title)[0];
            return pattern.IsMatch(Plain(title)) || pattern.IsMatch(Plain(lead));
        }

        static string KindOf(string title)
        {
            foreach (var (kind, pattern) in Kinds) if (Names(title, pattern)) return kind;
            return null;
        }

        // Chapitres nommés : un chapitre dure jusqu'au suivant ; au-delà de 3 min il contient aussi
        // l'épisode (sauf un générique de fin, long sur les films).
        public static List<Skippable> Named(List<Chapter> chapters, int durationMs)
        {
            var list = new List<Skippable>();
            bool hasOpening = false;
            foreach (Chapter c in chapters) if (KindOf(c.Title) == "Intro") hasOpening = true;
            for (int i = 0; i < chapters.Count; i++)
            {
                Chapter c = chapters[i];
                string type = KindOf(c.Title) ?? (!hasOpening && Names(c.Title, LooseIntro) ? "Intro" : null);
                int end = i + 1 < chapters.Count ? chapters[i + 1].StartMs : durationMs;
                if (type == null || end <= c.StartMs) continue;
                if (type != "Outro" && end - c.StartMs > NamedMax) continue;
                list.Add(new Skippable { Type = type, Start = c.StartMs, End = end });
            }
            return list;
        }

        // Chapitres seulement numérotés : un chapitre de 1 min 25 à 1 min 50 est le générique
        // d'ouverture dans le premier tiers, celui de fin dans le dernier.
        public static List<Skippable> Guessed(List<Chapter> chapters, int durationMs)
        {
            var list = new List<Skippable>();
            if (durationMs <= 0) return list;
            foreach (Chapter c in chapters) if (!Untitled.IsMatch(Plain(c.Title))) return list;
            for (int i = 0; i < chapters.Count; i++)
            {
                Chapter c = chapters[i];
                int end = i + 1 < chapters.Count ? chapters[i + 1].StartMs : durationMs;
                int len = end - c.StartMs;
                if (len < SongMin || len > SongMax) continue;
                if (end <= durationMs / 3) list.Add(new Skippable { Type = "Intro", Start = c.StartMs, End = end });
                else if (c.StartMs >= durationMs * 2L / 3) list.Add(new Skippable { Type = "Outro", Start = c.StartMs, End = end });
            }
            return list;
        }
    }
}
