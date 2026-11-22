// Dernière version lue de chaque titre, retrouvée à la reprise (lastVersions du web,
// lib/playback/sources.ts) : OK sur « Reprendre » relance la même version sans passer par la liste.
// Gardée seulement si le serveur annonce des identifiants de version stables (fonction
// « versions ») ou si la version ne vient pas d'AIOStreams, comme le web. 500 titres au plus
// par utilisateur, les plus récents d'abord, dans la préférence play.last.{utilisateur}
// (une ligne « titre<TAB>version[<TAB>release<TAB>service] » par titre). Lue une fois, écrite par Store.
// Release et service (web du 2026-11-11, findVersion) : si l'identifiant n'est plus dans la
// liste, la même release sur le même service la remplace.
using System;
using System.Collections.Generic;
using Tizen.Applications;

namespace NativeAIO.Core
{
    static class LastVersions
    {
        const int Max = 500;

        class Entry
        {
            public string Item, Id, Release, Service;
        }

        static List<Entry> list;
        static string listUser;

        static string Key => "play.last." + Session.UserId;

        static List<Entry> Load()
        {
            if (list != null && listUser == Session.UserId) return list;
            listUser = Session.UserId;
            list = new List<Entry>();
            try
            {
                if (Preference.Contains(Key))
                    foreach (string line in (Preference.Get<string>(Key) ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] f = line.Split('\t');
                        if (f.Length < 2 || f[0].Length == 0) continue;
                        list.Add(new Entry
                        {
                            Item = f[0],
                            Id = f[1],
                            Release = f.Length > 2 && f[2].Length > 0 ? f[2] : null,
                            Service = f.Length > 3 && f[3].Length > 0 ? f[3] : null,
                        });
                    }
            }
            catch (Exception) { }
            return list;
        }

        static Entry EntryOf(string itemId)
        {
            if (itemId == null) return null;
            foreach (Entry e in Load()) if (e.Item == itemId) return e;
            return null;
        }

        // Identifiant de la version retenue pour ce titre, ou null.
        public static string Get(string itemId) => EntryOf(itemId)?.Id;

        // Version retenue retrouvée dans la liste : par son identifiant, sinon par sa release sur
        // le même service (comme findVersion du web). null si aucune.
        public static Source Find(string itemId, List<Source> playable, out bool byRelease)
        {
            byRelease = false;
            Entry e = EntryOf(itemId);
            if (e == null) return null;
            Source s = playable.Find(x => x.Id == e.Id);
            if (s != null || e.Release == null) return s;
            s = playable.Find(x => x.Aio?.Release == e.Release && x.Aio?.Service == e.Service);
            byRelease = s != null;
            return s;
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
            Put(itemId, new Entry { Item = itemId, Id = source.Id, Release = source.Aio?.Release, Service = source.Aio?.Service });
        }

        // Version retenue introuvable dans la liste : oubliée (le web fait de même).
        public static void Forget(string itemId)
        {
            if (EntryOf(itemId) == null) return;
            Put(itemId, null);
        }

        static void Put(string itemId, Entry entry)
        {
            List<Entry> l = Load();
            l.RemoveAll(e => e.Item == itemId);
            if (entry != null) l.Insert(0, entry);
            if (l.Count > Max) l.RemoveRange(Max, l.Count - Max);
            var sb = new System.Text.StringBuilder();
            foreach (Entry e in l)
            {
                sb.Append(e.Item).Append('\t').Append(e.Id);
                if (e.Release != null) sb.Append('\t').Append(e.Release).Append('\t').Append(e.Service ?? "");
                sb.Append('\n');
            }
            Store.Set(Key, sb.ToString());
            Probe.Log("lastversion", entry != null ? $"retenue pour {itemId}" + (entry.Release != null ? " avec sa release" : " sans release") + $" ({l.Count} titres)" : $"oubliée pour {itemId}");
        }
    }
}
