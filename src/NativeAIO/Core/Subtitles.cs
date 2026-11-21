// Sous-titres affichés par l'application, repris de la version web (lib/subtitles/cues.ts,
// files.ts, tracks.ts, delay.ts) : lecture d'un fichier WebVTT, SRT, ASS ou SSA en répliques
// (début, fin, texte sans balises), nature d'une piste (normale, malentendants, forcée,
// panneaux, commentaire), écriture d'un décalage, et décalages mémorisés par version et par piste.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NativeAIO.Core
{
    // Une réplique : début et fin en ms (temps du fichier, avant décalage), texte à afficher.
    class Cue
    {
        public int Start, End;
        public string Text;
    }

    static class Subtitles
    {
        public const int DelayLimit = 600000, DelayStep = 100;
        // Un appui à l'écoute d'une réplique vient en moyenne aussi tard (version web).
        const int ReactionMs = 300;
        // Deux appuis plus éloignés concernent deux répliques différentes.
        const int MaxTapGap = 30000;

        const string Stamp = @"(?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3}";
        static readonly Regex Timing = new Regex("(" + Stamp + @")\s*-->\s*(" + Stamp + ")");
        static readonly Regex Ass = new Regex(@"^﻿?[ \t]*\[(?:script info|events)\][ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        static readonly Regex Tags = new Regex(@"<[^>]*>|\{[^}]*\}");
        static readonly Regex Blocks = new Regex(@"\n{2,}");
        static readonly Regex Drawing = new Regex(@"\{[^}]*\\p[1-9]");

        // ---- Lecture d'un fichier ----

        // Texte d'un fichier : UTF-16 ou UTF-8 s'il est marqué ou valide, sinon Windows-1252
        // (la version web prend l'encodage ancien de la langue du navigateur ; pour le français
        // et l'anglais, c'est celui-ci).
        public static string Decode(byte[] b)
        {
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch (Exception) { return Windows1252(b); }
        }

        // 0x80 à 0x9F de Windows-1252 (guillemets, apostrophe, œ, €…) ; le reste est Latin-1.
        // Écrit à la main : les tables d'encodages anciens ne sont pas sûres d'être sur la TV.
        static readonly string High = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";

        static string Windows1252(byte[] b)
        {
            var sb = new StringBuilder(b.Length);
            foreach (byte x in b) sb.Append(x >= 0x80 && x <= 0x9F ? High[x - 0x80] : (char)x);
            return sb.ToString();
        }

        static int ToMs(string stamp)
        {
            string[] cf = stamp.Replace(',', '.').Split('.');
            int seconds = 0;
            foreach (string part in cf[0].Split(':')) seconds = seconds * 60 + int.Parse(part, CultureInfo.InvariantCulture);
            string f = cf.Length > 1 ? cf[1] : "0";
            f = (f + "000").Substring(0, 3);
            return seconds * 1000 + int.Parse(f, CultureInfo.InvariantCulture);
        }

        // Texte affiché : balises retirées, entités WebVTT courantes rendues.
        static string Plain(string text)
        {
            string t = Tags.Replace(text, "");
            t = t.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&nbsp;", " ").Replace("&lrm;", "").Replace("&rlm;", "").Replace("&amp;", "&");
            var lines = new List<string>();
            foreach (string l in t.Split('\n')) { string x = l.Trim(); if (x.Length > 0) lines.Add(x); }
            return string.Join("\n", lines);
        }

        // Répliques d'un fichier WebVTT, SRT, ASS ou SSA, triées par début.
        public static List<Cue> Parse(string body)
        {
            List<Cue> cues = Ass.IsMatch(body) ? ParseAss(body) : ParseCues(body);
            cues.Sort((a, b) => a.Start.CompareTo(b.Start));
            return cues;
        }

        static List<Cue> ParseCues(string body)
        {
            var cues = new List<Cue>();
            foreach (string block in Blocks.Split(body.Replace("\r", "")))
            {
                string[] rows = block.Split('\n');
                int at = Array.FindIndex(rows, r => Timing.IsMatch(r));
                if (at < 0) continue;
                string text = Plain(string.Join("\n", rows, at + 1, rows.Length - at - 1));
                Match m = Timing.Match(rows[at]);
                if (text.Length > 0) cues.Add(new Cue { Start = ToMs(m.Groups[1].Value), End = ToMs(m.Groups[2].Value), Text = text });
            }
            return cues;
        }

        // Dialogues d'un fichier ASS ou SSA, sans leur style ni leurs dessins.
        static List<Cue> ParseAss(string body)
        {
            var cues = new List<Cue>();
            bool events = false;
            var fields = new List<string>();
            foreach (string row in body.Replace("\r", "").Split('\n'))
            {
                string line = row.Trim();
                if (line.StartsWith("[")) { events = line.ToLowerInvariant() == "[events]"; continue; }
                int colon = line.IndexOf(':');
                if (!events || colon < 0) continue;
                string key = line.Substring(0, colon).ToLowerInvariant();
                string[] values = line.Substring(colon + 1).Split(',');
                if (key == "format") { fields = new List<string>(); foreach (string f in values) fields.Add(f.Trim().ToLowerInvariant()); }
                int textAt = fields.IndexOf("text");
                if (key != "dialogue" || textAt < 0 || textAt >= values.Length) continue;
                string raw = string.Join(",", values, textAt, values.Length - textAt);
                if (Drawing.IsMatch(raw)) continue;
                string text = Plain(Tags.Replace(raw, "").Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " "));
                int s = fields.IndexOf("start"), e = fields.IndexOf("end");
                if (text.Length == 0 || s < 0 || e < 0 || s >= values.Length || e >= values.Length) continue;
                try { cues.Add(new Cue { Start = ToMs(values[s].Trim()), End = ToMs(values[e].Trim()), Text = text }); }
                catch (Exception) { }
            }
            return cues;
        }

        // Temps multipliés par ratio (fichier fait pour une autre cadence d'image).
        public static List<Cue> Retime(List<Cue> cues, double ratio)
        {
            var l = new List<Cue>(cues.Count);
            foreach (Cue c in cues) l.Add(new Cue { Start = (int)Math.Round(c.Start * ratio), End = (int)Math.Round(c.End * ratio), Text = c.Text });
            return l;
        }

        // ---- Nature d'une piste (tracks.ts) ----

        public enum Kind { Normal, Sdh, Forced, Signs, Commentary }

        public static Kind KindOf(string title, bool forced, bool hearingImpaired)
        {
            string t = (title ?? "").ToLowerInvariant();
            if (Regex.IsMatch(t, @"\bcommentary\b")) return Kind.Commentary;
            if (Regex.IsMatch(t, @"\b(signs?|songs?)\b")) return Kind.Signs;
            if (forced || Regex.IsMatch(t, @"\bforced\b")) return Kind.Forced;
            if (hearingImpaired || Regex.IsMatch(t, @"\b(sdh|cc|hearing[ -]impaired)\b")) return Kind.Sdh;
            return Kind.Normal;
        }

        public static bool SameLanguage(string a, string b)
        {
            string x = Short(a), y = Short(b);
            return x != null && x == y;
        }

        // Codes à 2 ou 3 lettres ramenés à 2 lettres pour les langues courantes.
        static string Short(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            string c = code.ToLowerInvariant().Split('-', '_')[0];
            switch (c)
            {
                case "fre": case "fra": return "fr";
                case "eng": return "en";
                case "spa": return "es";
                case "ger": case "deu": return "de";
                case "ita": return "it";
                case "por": return "pt";
                case "dut": case "nld": return "nl";
                case "jpn": return "ja";
                case "rus": return "ru";
                case "chi": case "zho": return "zh";
                case "kor": return "ko";
                case "ara": return "ar";
                case "pol": return "pl";
                case "tur": return "tr";
                case "swe": return "sv";
                case "dan": return "da";
                case "nor": case "nob": return "no";
                case "fin": return "fi";
                default: return c;
            }
        }

        // ---- Décalage (delay.ts) ----

        public static int Clamp(double ms) => (int)Math.Max(-DelayLimit, Math.Min(DelayLimit, Math.Round(ms)));

        // Décalage qui montre la réplique au moment où elle a été entendue.
        public static int DelayForLine(int heardAt, Cue line) => Clamp(heardAt - line.Start - ReactionMs);

        // Décalage d'après « Entendu » et « Vu » (le temps de réaction est dans les deux appuis et
        // s'annule) ; null si les appuis sont trop éloignés pour une seule réplique.
        public static int? DelayForTaps(int delay, int heardAt, int sawAt)
        {
            int gap = heardAt - sawAt;
            return Math.Abs(gap) > MaxTapGap ? (int?)null : Clamp(delay + gap);
        }

        // « +1,5 s », ou « +1:02,5 » au-delà d'une minute.
        public static string Amount(int ms)
        {
            double total = Math.Round(Math.Abs(ms) / 100.0) / 10;
            int minutes = (int)Math.Floor(total / 60);
            double sec = total - minutes * 60;
            string sep = Lang.Current == "fr" ? "," : ".";
            string s = sec.ToString(minutes > 0 ? "00.0" : "0.0", CultureInfo.InvariantCulture).Replace(".", sep);
            string sign = ms > 0 ? "+" : "−";
            return minutes > 0 ? sign + minutes + ":" + s : Lang.T("sub.seconds", sign + s);
        }

        // ---- Décalages mémorisés ----
        // Par version et par piste, comme la version web : deux éditions d'un même épisode, ou
        // deux sous-titres d'une même édition, sont rarement décalés d'autant. fixed : réglé à la
        // main, le recalage automatique n'y touche plus. Fichier texte, 500 entrées au plus.

        public struct Saved
        {
            public int Ms;
            public bool Fixed;
        }

        static readonly object fileLock = new object();
        static List<KeyValuePair<string, Saved>> delays;

        static string DelayFile => Tizen.Applications.Application.Current.DirectoryInfo.Data + "subtitle-delays.txt";

        static void LoadDelays()
        {
            if (delays != null) return;
            delays = new List<KeyValuePair<string, Saved>>();
            try
            {
                if (!File.Exists(DelayFile)) return;
                foreach (string line in File.ReadAllLines(DelayFile))
                {
                    string[] p = line.Split('\t');
                    if (p.Length == 3 && int.TryParse(p[1], out int ms))
                        delays.Add(new KeyValuePair<string, Saved>(p[0], new Saved { Ms = ms, Fixed = p[2] == "1" }));
                }
            }
            catch (Exception) { }
        }

        public static Saved? SavedDelay(string sourceId, string track)
        {
            if (string.IsNullOrEmpty(sourceId) || track == null) return null;
            LoadDelays();
            string key = sourceId + "|" + track;
            int i = delays.FindIndex(x => x.Key == key);
            return i < 0 ? (Saved?)null : delays[i].Value;
        }

        public static void SaveDelay(string sourceId, string track, int ms, bool isFixed)
        {
            if (string.IsNullOrEmpty(sourceId) || track == null) return;
            LoadDelays();
            string key = sourceId + "|" + track;
            delays.RemoveAll(x => x.Key == key);
            delays.Add(new KeyValuePair<string, Saved>(key, new Saved { Ms = ms, Fixed = isFixed }));
            if (delays.Count > 500) delays.RemoveRange(0, delays.Count - 500);
            var sb = new StringBuilder();
            foreach (var d in delays) sb.Append(d.Key.Replace('\t', ' ').Replace('\n', ' ')).Append('\t').Append(d.Value.Ms).Append('\t').Append(d.Value.Fixed ? "1" : "0").Append('\n');
            string text = sb.ToString();
            // Écrit hors du fil d'interface.
            Task.Run(() => { lock (fileLock) { try { File.WriteAllText(DelayFile, text); } catch (Exception) { } } });
        }
    }
}
