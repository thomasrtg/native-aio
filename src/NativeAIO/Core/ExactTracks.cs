// Pistes exactes choisies à la main, par titre et par lien (demande de l'auteur, 2026-11-25 ;
// ajout de Native AIO, absent du web). Le choix par langue (série ou film, Prefs.ShowAudio et
// ShowSubtitle) ne distingue pas deux pistes de la même langue et du même type : à la reprise,
// la première était prise (vu sur la TV : 2e piste française choisie, 1re remise). Ici :
// - même titre et même lien (identifiant de la version, ou même release sur le même service,
//   comme LastVersions) : la piste exacte est remise ;
// - autre lien : rien ici, le choix par langue s'applique.
// Piste décrite pour être retrouvée dans le même fichier :
// - audio : « index|langue|titre » ;
// - sous-titres : « off », « f|valeur|langue|titre » (piste du fichier, valeur du menu) ou
//   « s|langue|titre|rang » (fichier du serveur : rang parmi ceux de même langue et même titre,
//   leur ordre pouvant changer quand les sous-titres des addons arrivent).
// Garde-fou : une piste du fichier n'est reprise que si sa langue concorde, sinon on cherche la
// même langue et le même titre.
// 500 entrées au plus par utilisateur, les plus récentes d'abord, sur la TV seulement
// (préférence play.tracks.{utilisateur}), écrites par Store.
using System;
using System.Collections.Generic;
using System.Text;
using Tizen.Applications;

namespace NativeAIO.Core
{
    static class ExactTracks
    {
        const int Max = 500;

        class Entry
        {
            public string Item, Id, Release, Service, Audio, Sub;
        }

        static List<Entry> list;
        static string listUser;

        static string Key => "play.tracks." + Session.UserId;

        static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('|', '/');

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
                        if (f.Length < 6 || f[0].Length == 0) continue;
                        list.Add(new Entry
                        {
                            Item = f[0], Id = f[1],
                            Release = f[2].Length > 0 ? f[2] : null, Service = f[3].Length > 0 ? f[3] : null,
                            Audio = f[4].Length > 0 ? f[4] : null, Sub = f[5].Length > 0 ? f[5] : null,
                        });
                    }
            }
            catch (Exception) { }
            return list;
        }

        static bool SameLink(Entry e, Source s) =>
            s != null && (e.Id == s.Id || (e.Release != null && s.Aio?.Release == e.Release && s.Aio?.Service == e.Service));

        static Entry Find(string itemId, Source s)
        {
            if (itemId == null || s == null) return null;
            foreach (Entry e in Load()) if (e.Item == itemId && SameLink(e, s)) return e;
            return null;
        }

        // Description gardée pour ce titre et ce lien, ou null. Sur le fil d'interface.
        public static string Audio(string itemId, Source s) => Find(itemId, s)?.Audio;
        public static string Subtitle(string itemId, Source s) => Find(itemId, s)?.Sub;

        public static void SetAudio(string itemId, Source s, string desc) => Put(itemId, s, desc, null);
        public static void SetSubtitle(string itemId, Source s, string desc) => Put(itemId, s, null, desc);

        // Une entrée par titre (le dernier lien utilisé) : changer de lien remplace l'ancienne.
        static void Put(string itemId, Source s, string audio, string sub)
        {
            if (itemId == null || s?.Id == null) return;
            List<Entry> l = Load();
            Entry old = l.Find(e => e.Item == itemId);
            var entry = new Entry { Item = itemId, Id = s.Id, Release = s.Aio?.Release, Service = s.Aio?.Service };
            if (old != null && SameLink(old, s)) { entry.Audio = old.Audio; entry.Sub = old.Sub; }
            if (audio != null) entry.Audio = audio;
            if (sub != null) entry.Sub = sub;
            l.RemoveAll(e => e.Item == itemId);
            l.Insert(0, entry);
            if (l.Count > Max) l.RemoveRange(Max, l.Count - Max);
            var sb = new StringBuilder();
            foreach (Entry e in l)
                sb.Append(e.Item).Append('\t').Append(e.Id).Append('\t').Append(Clean(e.Release)).Append('\t').Append(Clean(e.Service))
                    .Append('\t').Append(e.Audio ?? "").Append('\t').Append(e.Sub ?? "").Append('\n');
            Store.Set(Key, sb.ToString());
            Probe.Log("tracks", $"piste exacte retenue pour {itemId} : audio « {entry.Audio} », sous-titres « {entry.Sub} »");
        }

        // ---- Audio ----

        public static string Describe(AudioPick.Track t) => t.Index + "|" + Clean(t.Lang) + "|" + Clean(t.Title);

        public static AudioPick.Track Resolve(string desc, List<AudioPick.Track> tracks)
        {
            if (desc == null) return null;
            string[] f = desc.Split('|');
            if (f.Length < 3 || !int.TryParse(f[0], out int index)) return null;
            AudioPick.Track t = tracks.Find(x => x.Index == index && Clean(x.Lang) == f[1]);
            return t ?? tracks.Find(x => Clean(x.Lang) == f[1] && Clean(x.Title) == f[2]);
        }

        // ---- Sous-titres ----

        public const string Off = "off";

        public static string Describe(SubtitlePick.Track t, List<SubtitlePick.Track> all)
        {
            if (t == null) return Off;
            if (!t.Server) return "f|" + t.Value + "|" + Clean(t.Lang) + "|" + Clean(t.Title);
            int rank = 0;
            foreach (SubtitlePick.Track x in all)
            {
                if (x == t) break;
                if (x.Server && Clean(x.Lang) == Clean(t.Lang) && Clean(x.Title) == Clean(t.Title)) rank++;
            }
            return "s|" + Clean(t.Lang) + "|" + Clean(t.Title) + "|" + rank;
        }

        // Piste décrite retrouvée (found faux : introuvable, le choix habituel s'applique).
        // Renvoie null avec found vrai pour « aucun sous-titre ».
        public static SubtitlePick.Track Resolve(string desc, List<SubtitlePick.Track> file, List<SubtitlePick.Track> server, out bool found)
        {
            found = false;
            if (desc == null) return null;
            if (desc == Off) { found = true; return null; }
            string[] f = desc.Split('|');
            SubtitlePick.Track t = null;
            if (f[0] == "f" && f.Length >= 4 && int.TryParse(f[1], out int value))
                t = file.Find(x => x.Value == value && Clean(x.Lang) == f[2])
                    ?? file.Find(x => Clean(x.Lang) == f[2] && Clean(x.Title) == f[3]);
            else if (f[0] == "s" && f.Length >= 4 && int.TryParse(f[3], out int rank))
            {
                List<SubtitlePick.Track> same = server.FindAll(x => Clean(x.Lang) == f[1] && Clean(x.Title) == f[2]);
                if (rank < same.Count) t = same[rank];
            }
            found = t != null;
            return t;
        }
    }
}
