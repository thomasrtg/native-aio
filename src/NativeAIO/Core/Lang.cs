// Textes de l'interface. Un fichier par langue dans res/lang (fr.txt, en.txt),
// une ligne « cle = texte ». Aucun texte n'est écrit dans les écrans : ils
// demandent Lang.T("cle"). Une clé absente s'affiche telle quelle, pour que
// l'oubli se voie à l'écran.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tizen.Applications;

namespace NativeAIO.Core
{
    static class Lang
    {
        public static readonly string[] Available = { "fr", "en" };
        const string PrefKey = "lang";

        static Dictionary<string, string> texts = new Dictionary<string, string>();

        public static string Current { get; private set; } = "en";

        // Langue mémorisée, sinon celle de la TV si elle est proposée, sinon l'anglais.
        public static void Init()
        {
            string code = null;
            try { if (Preference.Contains(PrefKey)) code = Preference.Get<string>(PrefKey); }
            catch (Exception) { }
            if (Array.IndexOf(Available, code) < 0) code = TvLanguage();
            Load(code);
        }

        // Change de langue et la mémorise. Les écrans relisent ensuite leurs textes.
        public static void Set(string code)
        {
            Load(code);
            Store.Set(PrefKey, Current); // hors du fil d'interface
        }

        public static string T(string key)
        {
            return texts.TryGetValue(key, out string s) ? s : key;
        }

        public static string T(string key, params object[] args)
        {
            if (!texts.TryGetValue(key, out string s)) return key;
            try { return string.Format(CultureInfo.InvariantCulture, s, args); }
            catch (FormatException) { return key; }
        }

        static string TvLanguage()
        {
            string locale = null;
            try { locale = Tizen.System.SystemSettings.LocaleLanguage; } // ex. « fr_FR »
            catch (Exception) { }
            if (string.IsNullOrEmpty(locale)) locale = CultureInfo.CurrentUICulture.Name;
            string two = locale.Length >= 2 ? locale.Substring(0, 2).ToLowerInvariant() : "";
            return Array.IndexOf(Available, two) >= 0 ? two : "en";
        }

        static void Load(string code)
        {
            if (Array.IndexOf(Available, code) < 0) code = "en";
            var map = new Dictionary<string, string>();
            string path = Path.Combine(Application.Current.DirectoryInfo.Resource, "lang", code + ".txt");
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
                }
            }
            catch (Exception) { } // fichier absent : les clés s'afficheront telles quelles
            texts = map;
            Current = code;
        }
    }
}
