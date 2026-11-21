// Langues proposées dans les Paramètres et comparaison des langues des pistes, comme la
// version web (lib/languages.ts) : les langues des filtres d'AIOStreams, plus catalan,
// filipino et islandais. Le code gardé sur le compte est celui de Jellyfin (ISO 639-2/B :
// « fre », « ger »…) ; le portugais du Brésil et l'espagnol d'Amérique latine gardent leur
// région (« pt-BR », « es-MX »).
// Deux codes désignent la même langue s'ils mènent à la même entrée : « fr », « fra » et
// « fre » sont le français. Une région n'est gardée que pour ces deux-là (« pt-BR » n'est
// pas « por », « en-US » est l'anglais), comme dans le web.
// Les noms sont dans les fichiers de traduction (« language.<code> »).
using System;
using System.Collections.Generic;

namespace NativeAIO.Core
{
    static class Languages
    {
        // « Langue d'origine » : celle de la piste audio par défaut, que seul le serveur connaît.
        public const string Original = "OriginalLanguage";

        // Code de Jellyfin, puis les autres codes de la même langue.
        static readonly string[][] Table =
        {
            new[] { "eng", "en" }, new[] { "jpn", "ja" }, new[] { "chi", "zho", "zh" }, new[] { "rus", "ru" },
            new[] { "ara", "ar" }, new[] { "por", "pt" }, new[] { "pt-br" }, new[] { "spa", "es" },
            new[] { "fre", "fra", "fr" }, new[] { "ger", "deu", "de" }, new[] { "ita", "it" }, new[] { "kor", "ko" },
            new[] { "hin", "hi" }, new[] { "ben", "bn" }, new[] { "pan", "pa" }, new[] { "mar", "mr" },
            new[] { "guj", "gu" }, new[] { "tam", "ta" }, new[] { "tel", "te" }, new[] { "kan", "kn" },
            new[] { "mal", "ml" }, new[] { "tha", "th" }, new[] { "vie", "vi" }, new[] { "ind", "id" },
            new[] { "tur", "tr" }, new[] { "heb", "he", "iw" }, new[] { "per", "fas", "fa" }, new[] { "ukr", "uk" },
            new[] { "gre", "ell", "el" }, new[] { "lit", "lt" }, new[] { "lav", "lv" }, new[] { "est", "et" },
            new[] { "pol", "pl" }, new[] { "cze", "ces", "cs" }, new[] { "slo", "slk", "sk" }, new[] { "hun", "hu" },
            new[] { "rum", "ron", "ro", "mo" }, new[] { "bul", "bg" }, new[] { "srp", "sr" }, new[] { "hrv", "hr" },
            new[] { "slv", "sl" }, new[] { "dut", "nld", "nl" }, new[] { "dan", "da" }, new[] { "fin", "fi" },
            new[] { "swe", "sv" }, new[] { "nor", "no", "nob", "nb", "nno", "nn" }, new[] { "may", "msa", "ms" },
            new[] { "es-mx" }, new[] { "cat", "ca" }, new[] { "fil" }, new[] { "ice", "isl", "is" },
        };

        static readonly Dictionary<string, string[]> byCode = new Dictionary<string, string[]>();

        static Languages()
        {
            foreach (string[] e in Table)
                foreach (string c in e) byCode[c] = e;
        }

        // Codes proposés à l'ajout (code de Jellyfin, avec la région en majuscules).
        public static IEnumerable<string> Codes
        {
            get { foreach (string[] e in Table) yield return Stored(e[0]); }
        }

        static string Stored(string code) => code.Length == 5 ? code.Substring(0, 3) + code.Substring(3).ToUpperInvariant() : code;

        static string[] EntryOf(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string key = tag.Trim().ToLowerInvariant().Replace('_', '-');
            if (byCode.TryGetValue(key, out string[] e)) return e;
            int dash = key.IndexOf('-');
            return dash > 0 && byCode.TryGetValue(key.Substring(0, dash), out e) ? e : null;
        }

        // Identifiant de comparaison d'une langue (le code lui-même si elle est inconnue).
        static string Key(string tag) => EntryOf(tag)?[0] ?? tag.Trim().ToLowerInvariant();

        public static bool Same(string a, string b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && Key(a) == Key(b);

        // Le code que Jellyfin garde pour la langue d'une piste (web : languageCode).
        public static string Code(string tag) => EntryOf(tag) is string[] e ? Stored(e[0]) : tag.Trim().ToLowerInvariant();

        // Langue réelle d'une piste : null pour « und », « mul », « zxx », « mis » (web : langOf).
        public static string Real(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string k = tag.Trim().ToLowerInvariant();
            return k == "und" || k == "mul" || k == "zxx" || k == "mis" ? null : Code(tag);
        }

        // Nom dans la langue de l'interface, null si la langue n'a pas de nom traduit.
        public static string Name(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            if (tag == Original) return Lang.T("set.original_lang");
            foreach (string k in new[] { "language." + Key(tag), "language." + tag.Trim().ToLowerInvariant() })
            {
                string n = Lang.T(k);
                if (n != k) return n;
            }
            return null;
        }

        // Le premier élément dans la première des langues qui en a un (web : firstInLanguages).
        public static T FirstIn<T>(IList<string> codes, IList<T> items, Func<T, string> tagOf) where T : class
        {
            foreach (string code in codes)
                foreach (T it in items)
                    if (Same(code, tagOf(it))) return it;
            return null;
        }
    }
}
