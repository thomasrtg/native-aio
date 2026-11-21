// Choix de la piste audio au lancement d'un fichier, comme la version web
// (lib/playback/track-picks.ts chooseTracks, audio-tracks.ts betterAudioFor, show-tracks.ts) :
// 1. la première piste dans la première des langues de l'utilisateur ; sinon celle que le
//    lecteur de la TV a prise ; « Langue originale » = langue de la piste par défaut du serveur ;
// 2. puis, si « Lire d'abord la piste par défaut » est mis, la piste par défaut de la version
//    (parmi plusieurs, celle d'une de ses langues) ; enfin, dans la même langue, le type
//    préféré (principale, audiodescription, commentaire, dans l'ordre choisi).
// Une langue choisie pour la série (« Mémoriser les choix ») passe en tête de ses langues et
// la piste par défaut ne passe plus avant.
// Le web connaît aussi les pistes illisibles par son lecteur et les écarte : pas Native AIO.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NativeAIO.Core
{
    static class AudioPick
    {
        public class Track
        {
            public int Index;      // place dans le lecteur de la TV
            public string Lang, Title;
            public bool IsDefault;
        }

        static readonly Regex Description = new Regex(@"\b(audio description|descriptive audio|described video)\b", RegexOptions.IgnoreCase);
        static readonly Regex Commentary = new Regex(@"\bcommentary\b", RegexOptions.IgnoreCase);

        static string TypeOf(Track t)
        {
            string title = t.Title ?? "";
            if (Description.IsMatch(title)) return "description";
            if (Commentary.IsMatch(title)) return "commentary";
            return "main";
        }

        // Langues de l'utilisateur, avec le choix de la série en tête ; preferDefault devient faux
        // s'il y en a un.
        public static List<string> Languages(string seriesId, out bool preferDefault, out string showPick)
        {
            var l = Prefs.AudioLanguages;
            preferDefault = Prefs.PlayDefaultAudio;
            showPick = Prefs.RememberAudio ? Prefs.ShowAudio(seriesId) : null;
            if (showPick != null)
            {
                string pick = showPick;
                l.RemoveAll(x => Core.Languages.Same(x, pick));
                l.Insert(0, pick);
                preferDefault = false;
            }
            return l;
        }

        // « Langue originale » remplacée par la langue de la piste par défaut du serveur.
        public static List<string> Codes(List<string> languages, Source source)
        {
            string original = null;
            if (source?.MediaStreams != null && source.DefaultAudioStreamIndex != null)
                foreach (MediaStream m in source.MediaStreams)
                    if (m.Type == "Audio" && m.Index == source.DefaultAudioStreamIndex) original = m.Language;
            var codes = new List<string>();
            foreach (string l in languages)
            {
                string c = l == Core.Languages.Original ? original : l;
                if (!string.IsNullOrEmpty(c)) codes.Add(c);
            }
            return codes;
        }

        // La piste à jouer ; current : celle du lecteur (-1 si inconnue).
        // order : types dans l'ordre de l'utilisateur (Prefs.AudioTypes, lu sur le fil d'interface).
        public static Track Choose(List<Track> tracks, int current, List<string> codes, bool preferDefault, List<string> order, out string why)
        {
            why = null;
            if (tracks.Count == 0) return null;
            Track chosen = Core.Languages.FirstIn(codes, tracks, t => t.Lang);
            why = chosen != null ? "langue " + chosen.Lang : "piste du lecteur";
            if (chosen == null) chosen = tracks.Find(t => t.Index == current) ?? tracks[0];

            // betterAudioFor
            int Rank(Track t) => order.IndexOf(TypeOf(t));
            int mainRank = order.IndexOf("main");
            List<Track> usual = tracks.FindAll(t => Rank(t) <= mainRank);
            List<Track> pool = usual.Count > 0 ? usual : tracks;
            Track InLanguages(List<Track> from) => Core.Languages.FirstIn(codes, from, t => t.Lang) ?? from[0];
            List<Track> defaults = preferDefault ? pool.FindAll(t => t.IsDefault) : new List<Track>();
            Track b = defaults.Count > 0 ? InLanguages(defaults) : pool.Contains(chosen) ? chosen : InLanguages(pool);
            if (defaults.Count > 0 && b != chosen) why = "piste par défaut";
            Track best = b;
            foreach (Track t in tracks)
            {
                bool same = !string.IsNullOrEmpty(b.Lang) ? Core.Languages.Same(b.Lang, t.Lang) : string.IsNullOrEmpty(t.Lang);
                if (same && Rank(t) < Rank(best)) best = t;
            }
            if (best != b) why += ", type " + TypeOf(best);
            return best;
        }

        // Langue que les réglages (sans le choix de la série) feraient jouer : la première des
        // langues de l'utilisateur que la version a (web : settingsAudio).
        public static string SettingsLanguage(List<Track> tracks, Source source)
        {
            Track t = Core.Languages.FirstIn(Codes(Prefs.AudioLanguages, source), tracks, x => x.Lang);
            return t != null ? Core.Languages.Code(t.Lang) : null;
        }
    }
}
