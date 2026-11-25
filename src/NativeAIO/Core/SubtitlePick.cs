// Choix des sous-titres au lancement d'un fichier, comme la version web
// (lib/playback/track-picks.ts chooseTracks, subtitles/tracks.ts preferredSubtitle et
// bestOfTypes, show-tracks.ts withShowPick et setSubtitle) :
// 1. parmi les pistes du fichier puis celles du serveur, la première dans les langues de
//    l'utilisateur (la première tout court s'il n'en a pas), selon le mode (« désactivés » :
//    aucune ; « forcés seulement » : seulement les forcées) ; puis, dans la même langue, le
//    type préféré (complets, malentendants, forcés, panneaux ; commentaires en dernier) ;
// 2. « forcés seulement », ou « quand l'audio n'est pas dans ma langue » alors que l'audio y
//    est : une piste forcée de cette langue (fichier, sinon serveur), sinon aucune ;
// 3. « toujours », ou « quand l'audio n'est pas dans ma langue » alors qu'il n'y est pas :
//    pour la première langue de l'utilisateur que le fichier n'a pas, mais le serveur oui,
//    une piste du serveur.
// Un choix fait pour la série (« Mémoriser les choix ») passe devant : « off » met le mode
// « désactivés » ; une langue passe en tête et met le mode « toujours », son type en tête.
// Différence voulue : les sous-titres en image du fichier (PGS…) sont écartés, Native AIO ne sait
// pas les afficher.
using System;
using System.Collections.Generic;

namespace NativeAIO.Core
{
    static class SubtitlePick
    {
        public class Track
        {
            public int Value;      // valeur du menu du lecteur (piste du fichier, ou ServerBase + n)
            public bool Server;    // fichier à part fourni par le serveur
            public string Lang, Title;
            public bool Forced, Sdh;
        }

        public class Inputs
        {
            public List<string> Languages, Types;
            public string Mode, ShowPick;
            public string Exact; // piste exacte retenue pour ce titre et ce lien (ExactTracks), posée par le lecteur
        }

        public const string Off = "off";

        // Réglages, avec le choix de la série (web : withShowPick). Sur le fil d'interface.
        public static Inputs Read(string seriesId)
        {
            var i = new Inputs { Languages = Prefs.SubtitleLanguages, Types = Prefs.SubtitleTypes, Mode = Prefs.SubtitleMode };
            i.ShowPick = Prefs.RememberSubtitles ? Prefs.ShowSubtitle(seriesId) : null;
            if (i.ShowPick == Off) i.Mode = "None";
            else if (i.ShowPick != null)
            {
                string[] parts = i.ShowPick.Split(':');
                string lang = parts[0];
                i.Languages.RemoveAll(x => Core.Languages.Same(x, lang));
                i.Languages.Insert(0, lang);
                i.Mode = "Always";
                if (parts.Length > 1 && Array.IndexOf(Prefs.SubtitleTypeNames, parts[1]) >= 0)
                {
                    i.Types.Remove(parts[1]);
                    i.Types.Insert(0, parts[1]);
                }
            }
            return i;
        }

        // normal, sdh, forced, signs ou commentary (web : subtitleType).
        public static string TypeOf(Track t)
        {
            switch (Subtitles.KindOf(t.Title, t.Forced, t.Sdh))
            {
                case Subtitles.Kind.Commentary: return "commentary";
                case Subtitles.Kind.Signs: return "signs";
                case Subtitles.Kind.Forced: return "forced";
                case Subtitles.Kind.Sdh: return "sdh";
                default: return "normal";
            }
        }

        static bool ForcedOnly(Track t) { string k = TypeOf(t); return k == "forced" || k == "signs"; }

        // La piste du type le mieux classé, la première du fichier à égalité (web : bestOfTypes).
        public static Track BestOfTypes(IEnumerable<Track> tracks, List<string> order)
        {
            int Rank(Track t) { string k = TypeOf(t); return k == "commentary" ? order.Count : order.IndexOf(k); }
            Track best = null;
            foreach (Track t in tracks)
                if (best == null || Rank(t) < Rank(best)) best = t;
            return best;
        }

        static bool SameLang(string a, string b) => Core.Languages.Same(a, b);

        // Web : preferredSubtitle.
        static Track Preferred(List<Track> all, Inputs i)
        {
            if (i.Mode == "None") return null;
            List<Track> pool = i.Mode == "OnlyForced" ? all.FindAll(ForcedOnly) : all;
            Track first = i.Languages.Count > 0 ? Core.Languages.FirstIn(i.Languages, pool, t => t.Lang) : pool.Count > 0 ? pool[0] : null;
            if (first == null) return null;
            return BestOfTypes(pool.FindAll(t => t == first || (!string.IsNullOrEmpty(t.Lang) && SameLang(first.Lang, t.Lang))), i.Types);
        }

        // La piste à montrer (null : aucune). file : pistes du fichier ; server : pistes du
        // serveur ; audioLang : langue de la piste audio jouée.
        public static Track Choose(List<Track> file, List<Track> server, string audioLang, Inputs i, out string why)
        {
            var all = new List<Track>(file);
            all.AddRange(server);
            Track pick = Preferred(all, i);
            why = pick != null ? "langue " + (pick.Lang ?? "?") + ", type " + TypeOf(pick) : "aucune selon le mode";

            bool audioMine = i.Languages.Exists(l => SameLang(l, audioLang));
            bool onlyForced = i.Mode == "OnlyForced" || (i.Mode == "Smart" && audioMine);
            Track ForcedFromServer(string lang) =>
                BestOfTypes(server.FindAll(s => SameLang(lang, s.Lang) && ForcedOnly(s)), i.Types);

            if (pick != null && !pick.Server)
            {
                Track chosen = pick;
                List<Track> same = file.FindAll(t => t == chosen
                    || (!string.IsNullOrEmpty(t.Lang) && !string.IsNullOrEmpty(chosen.Lang) && Core.Languages.Code(t.Lang) == Core.Languages.Code(chosen.Lang)));
                Track best = BestOfTypes(onlyForced ? same.FindAll(ForcedOnly) : same, i.Types);
                if (best != null) pick = best;
                else { pick = ForcedFromServer(chosen.Lang); why = pick != null ? "forcés du serveur" : "forcés seulement : aucun"; }
            }
            else if (pick != null && onlyForced)
            {
                pick = ForcedFromServer(pick.Lang);
                why = pick != null ? "forcés du serveur" : "forcés seulement : aucun";
            }

            if (i.Mode == "Always" || (i.Mode == "Smart" && !audioMine))
            {
                foreach (string lang in i.Languages)
                {
                    if (file.Exists(t => SameLang(lang, t.Lang))) break;
                    List<Track> offered = server.FindAll(s => SameLang(lang, s.Lang));
                    if (offered.Count == 0) continue;
                    if (!offered.Contains(pick))
                    {
                        pick = BestOfTypes(offered, i.Types);
                        why = "serveur, langue " + lang + ", type " + TypeOf(pick);
                    }
                    break;
                }
            }
            return pick;
        }

        // Choix à garder pour la série quand l'utilisateur prend une piste (null : désactivés),
        // ou null s'il faut oublier le choix (web : setSubtitle). all : pistes du menu.
        // keep : faux si rien n'est à enregistrer (piste sans langue).
        public static string ShowPickFor(Track t, List<Track> all, out bool keep)
        {
            keep = true;
            if (t == null) return Prefs.SubtitleMode == "None" ? null : Off;
            string lang = Core.Languages.Real(t.Lang);
            if (lang == null) { keep = false; return null; }
            string type = TypeOf(t);
            Track first = Core.Languages.FirstIn(Prefs.SubtitleLanguages, all, x => x.Lang);
            Track shown = first != null && Core.Languages.Real(first.Lang) == lang
                ? BestOfTypes(all.FindAll(x => Core.Languages.Real(x.Lang) == lang), Prefs.SubtitleTypes) : null;
            bool usual = Prefs.SubtitleMode == "Always" && shown != null && TypeOf(shown) == type;
            return usual ? null : type == "commentary" ? lang : lang + ":" + type;
        }
    }
}
