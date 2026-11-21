// Dernière version lue de chaque titre, retrouvée à la reprise (lastVersions du web,
// lib/playback/play.ts) : OK sur « Reprendre » relance la même version sans passer par la liste.
// Gardée seulement si le serveur annonce des identifiants de version stables (fonction
// « versions ») ou si la version ne vient pas d'AIOStreams, comme le web. 500 titres au plus
// par utilisateur, les plus récents d'abord, dans la préférence play.last.{utilisateur}
// (une ligne « titre<TAB>version » par titre). Lue une fois, écrite par Store.
using System;
using System.Collections.Generic;
using Tizen.Applications;

namespace NativeAIO.Core
{
    static class LastVersions
    {
        const int Max = 500;
        static List<KeyValuePair<string, string>> list;
        static string listUser;

        static string Key => "play.last." + Session.UserId;

        static List<KeyValuePair<string, string>> Load()
        {
            if (list != null && listUser == Session.UserId) return list;
            listUser = Session.UserId;
            list = new List<KeyValuePair<string, string>>();
            try
            {
                if (Preference.Contains(Key))
                    foreach (string line in (Preference.Get<string>(Key) ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0) list.Add(new KeyValuePair<string, string>(line.Substring(0, tab), line.Substring(tab + 1)));
                    }
            }
            catch (Exception) { }
            return list;
        }

        // Version retenue pour ce titre, ou null.
        public static string Get(string itemId)
        {
            if (itemId == null) return null;
            foreach (var kv in Load()) if (kv.Key == itemId) return kv.Value;
            return null;
        }

        // Lecture démarrée sur cette version (lecteur).
        public static async void Note(string itemId, Source source)
        {
            if (itemId == null || source?.Id == null) return;
            bool lasting = source.Aio == null;
            if (!lasting)
            {
                try { lasting = ((await Library.Features()).Versions ?? 0) > 0; }
                catch (Exception) { return; }
            }
            if (!lasting) { Probe.Log("lastversion", "identifiants de version non stables : rien de retenu"); return; }
            Put(itemId, source.Id);
        }

        // Version retenue introuvable dans la liste : oubliée (le web fait de même).
        public static void Forget(string itemId)
        {
            if (Get(itemId) == null) return;
            Put(itemId, null);
        }

        static void Put(string itemId, string sourceId)
        {
            List<KeyValuePair<string, string>> l = Load();
            l.RemoveAll(kv => kv.Key == itemId);
            if (sourceId != null) l.Insert(0, new KeyValuePair<string, string>(itemId, sourceId));
            if (l.Count > Max) l.RemoveRange(Max, l.Count - Max);
            var sb = new System.Text.StringBuilder();
            foreach (var kv in l) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            Store.Set(Key, sb.ToString());
            Probe.Log("lastversion", sourceId != null ? $"retenue pour {itemId} ({l.Count} titres)" : $"oubliée pour {itemId}");
        }
    }
}
