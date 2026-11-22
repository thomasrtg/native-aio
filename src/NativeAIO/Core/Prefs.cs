// Réglages de l'utilisateur gardés sur le serveur, lus et écrits au même endroit que la
// version web (module 13c, étape 3 ; docs/PARAMETRES.md) :
// - « Compte W » : préférences de la version web, /DisplayPreferences/aiostreams-web,
//   champ CustomPrefs (textes). Une clé absente ou vide vaut la valeur par défaut du web ;
//   le web ne les lit que si la marque « saved » est posée.
// - « Compte J » : réglages Jellyfin du compte, /Users/Me (champ Configuration), renvoyés
//   en entier à /Users/Configuration.
// Une copie est gardée sur la TV (Preference) : le lecteur n'attend jamais le serveur, qui
// est relu à l'ouverture, au changement d'utilisateur et à l'ouverture des Paramètres.
// En écrivant, Native AIO relit d'abord ce que garde le serveur et n'y change que ses propres
// réglages : les autres (thème, choix par série du web, réglages Jellyfin…) restent intacts.
// Un retour à la valeur par défaut est envoyé vide : on ne sait pas si le serveur garde
// une clé qu'on ne lui renvoie pas (le web suppose que oui, show-tracks.ts).
// Tout se passe sur le fil d'interface, sauf la lecture du JSON et l'écriture de la copie.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tizen.Applications;

namespace NativeAIO.Core
{
    static class Prefs
    {
        const string WebId = "aiostreams-web", SavedMark = "saved";
        const int PushDelayMs = 500; // comme le web : envoi quand les changements s'arrêtent

        // Valeurs proposées par le web (lib/settings.ts).
        public static readonly string[] Prompts = { "credits", "end", "off" };
        public static readonly int[] Leads = { 15, 30, 45, 60, 90, 120 };
        public static readonly int[] Countdowns = { 5, 10, 15, 30 };
        public static readonly int[] StillAfter = { 0, 2, 3, 4, 5, 8 };
        public static readonly string[] SegmentTypes = { "Intro", "Recap", "Outro", "Preview", "Commercial" };
        public static readonly string[] SegmentActions = { "ask", "skip", "none" };
        public static readonly int[] SeekSteps = { 5, 10, 15, 30 };

        static Dictionary<string, string> web = new Dictionary<string, string>();
        static Dictionary<string, object> config = new Dictionary<string, object>();
        static readonly Dictionary<string, string> pendingWeb = new Dictionary<string, string>();
        static readonly Dictionary<string, object> pendingConfig = new Dictionary<string, object>();
        static string user;
        static int gen, pushSeq;
        static Task pushing = Task.CompletedTask;

        // Valeurs changées (serveur relu, ou réglage modifié) : sur le fil d'interface.
        public static event Action Changed;

        // Dernier échec d'envoi ou de lecture, pour les mesures (null si tout va bien).
        public static string LastError { get; private set; }
        public static DateTime? LastSync { get; private set; }

        // ---- Réglages ----

        // Lecture
        public static bool AutoPlayFirst { get => Flag("autoPlayFirst", false); set => SetFlag("autoPlayFirst", value, false); }
        public static bool PreloadVersions { get => Flag("preloadVersions", false); set => SetFlag("preloadVersions", value, false); }
        // Épisode suivant
        public static bool NextAuto { get => ConfigFlag("EnableNextEpisodeAutoPlay"); set => SetConfig("EnableNextEpisodeAutoPlay", value); }
        public static bool NextFallbackFirst { get => Flag("nextFallbackFirst", true); set => SetFlag("nextFallbackFirst", value, true); }
        public static string NextPrompt { get => Choice("nextPrompt", Prompts, "credits"); set => SetChoice("nextPrompt", value, "credits"); }
        public static int NextLead { get => Number("nextLead", Leads, 30); set => SetNumber("nextLead", value, 30); }
        public static int NextCountdown { get => Number("nextCountdown", Countdowns, 15); set => SetNumber("nextCountdown", value, 15); }
        public static int StillWatching { get => Number("stillWatching", StillAfter, 3); set => SetNumber("stillWatching", value, 3); }
        // Passages : ask (bouton), skip (sauter), none (rien).
        public static string Segment(string type) => Choice("segment" + type, SegmentActions, "ask");
        public static void SetSegment(string type, string action) => SetChoice("segment" + type, action, "ask");
        public static bool ChapterSkips { get => Flag("chapterSkips", true); set => SetFlag("chapterSkips", value, true); }

        // Audio (compte J). Langues : la liste si le serveur en garde une (AudioLanguages),
        // sinon la seule langue de Jellyfin (AudioLanguagePreference), comme le web.
        public static bool AudioLists => config.TryGetValue("AudioLanguages", out object o) && o is List<object>;
        public static List<string> AudioLanguages => ConfigList("AudioLanguages", "AudioLanguagePreference");
        public static void SetAudioLanguages(List<string> next)
        {
            var c = new Dictionary<string, object>();
            if (AudioLists) c["AudioLanguages"] = new List<object>(next);
            c["AudioLanguagePreference"] = next.Count > 0 ? next[0] : "";
            SetConfig(c);
        }
        public static bool PlayDefaultAudio { get => ConfigFlag("PlayDefaultAudioTrack"); set => SetConfig("PlayDefaultAudioTrack", value); }
        public static readonly string[] AudioTypeNames = { "main", "description", "commentary" };
        public static bool AudioTypesKnown => config.TryGetValue("AudioTypes", out object o) && o is List<object>;
        // L'ordre de l'utilisateur, les types qu'il omet à la suite (web : audioTypes).
        public static List<string> AudioTypes
        {
            get
            {
                var l = new List<string>();
                if (config.TryGetValue("AudioTypes", out object o) && o is List<object> saved)
                    foreach (object t in saved)
                        if (t is string s && Array.IndexOf(AudioTypeNames, s) >= 0 && !l.Contains(s)) l.Add(s);
                foreach (string s in AudioTypeNames) if (!l.Contains(s)) l.Add(s);
                return l;
            }
            set => SetConfig("AudioTypes", new List<object>(value));
        }
        public static bool RememberAudio { get => ConfigFlag("RememberAudioSelections"); set => SetConfig("RememberAudioSelections", value); }

        // Sous-titres (compte J), comme le web (user-config.ts, subtitles/tracks.ts).
        public static readonly string[] SubtitleModes = { "Default", "Always", "Smart", "OnlyForced", "None" };
        public static bool SubtitleLists => config.TryGetValue("SubtitleLanguages", out object o) && o is List<object>;
        public static List<string> SubtitleLanguages => ConfigList("SubtitleLanguages", "SubtitleLanguagePreference");
        public static void SetSubtitleLanguages(List<string> next)
        {
            var c = new Dictionary<string, object>();
            if (SubtitleLists) c["SubtitleLanguages"] = new List<object>(next);
            c["SubtitleLanguagePreference"] = next.Count > 0 ? next[0] : "";
            SetConfig(c);
        }
        public static string SubtitleMode
        {
            get => config.TryGetValue("SubtitleMode", out object o) && o is string s && Array.IndexOf(SubtitleModes, s) >= 0 ? s : "Default";
            set => SetConfig("SubtitleMode", value);
        }
        public static readonly string[] SubtitleTypeNames = { "normal", "sdh", "forced", "signs" };
        public static bool SubtitleTypesKnown => config.TryGetValue("SubtitleTypes", out object o) && o is List<object>;
        // L'ordre de l'utilisateur, les types qu'il omet à la suite (web : subtitleTypes).
        public static List<string> SubtitleTypes
        {
            get
            {
                var l = new List<string>();
                if (config.TryGetValue("SubtitleTypes", out object o) && o is List<object> saved)
                    foreach (object t in saved)
                        if (t is string s && Array.IndexOf(SubtitleTypeNames, s) >= 0 && !l.Contains(s)) l.Add(s);
                foreach (string s in SubtitleTypeNames) if (!l.Contains(s)) l.Add(s);
                return l;
            }
            set => SetConfig("SubtitleTypes", new List<object>(value));
        }
        // Montré seulement si le serveur garde ce réglage ; « vrai » sauf s'il vaut false.
        public static bool SubtitleOnlyMineKnown => config.TryGetValue("ListPreferredSubtitlesOnly", out object o) && o is bool;
        public static bool SubtitleOnlyMine { get => ConfigFlag("ListPreferredSubtitlesOnly"); set => SetConfig("ListPreferredSubtitlesOnly", value); }
        public static bool RememberSubtitles { get => ConfigFlag("RememberSubtitleSelections"); set => SetConfig("RememberSubtitleSelections", value); }

        // Apparence des sous-titres (compte W, comme le web : lib/settings.ts subtitleLook).
        public static readonly string[] SubtitleSizes = { "small", "normal", "large", "huge" };
        public static readonly string[] SubtitleFonts = { "default", "sans-serif", "serif", "monospace", "serif-monospace", "casual", "cursive" };
        public static readonly string[] SubtitleOutlines = { "none", "thin", "normal", "thick" };
        // Couleurs proposées par le web sur TV (ui/src/color-input : pas de sélecteur libre).
        public static readonly string[] SubtitleColors = { "#ffffff", "#ffff00", "#00ffff", "#00ff00", "#ff00ff", "#ff0000", "#0000ff", "#808080", "#000000" };
        public static readonly string[] SubtitleColorNames = { "white", "yellow", "cyan", "green", "magenta", "red", "blue", "grey", "black" };
        public const int PositionMin = -3, PositionMax = 30;
        public static string SubtitleSize { get => Choice("subtitleSize", SubtitleSizes, "normal"); set => SetChoice("subtitleSize", value, "normal"); }
        public static string SubtitleFont { get => Choice("subtitleFont", SubtitleFonts, "default"); set => SetChoice("subtitleFont", value, "default"); }
        public static bool SubtitleBold { get => Flag("subtitleBold", false); set => SetFlag("subtitleBold", value, false); }
        public static string SubtitleColor { get => Hex("subtitleColor", "#ffffff"); set => SetChoice("subtitleColor", value, "#ffffff"); }
        public static int SubtitlePosition { get => Range("subtitlePosition", PositionMin, PositionMax, 0); set => SetNumber("subtitlePosition", value, 0); }
        public static string SubtitleOutline { get => Choice("subtitleOutline", SubtitleOutlines, "normal"); set => SetChoice("subtitleOutline", value, "normal"); }
        public static string SubtitleOutlineColor { get => Hex("subtitleOutlineColor", "#000000"); set => SetChoice("subtitleOutlineColor", value, "#000000"); }
        public static bool SubtitleShadow { get => Flag("subtitleShadow", false); set => SetFlag("subtitleShadow", value, false); }
        public static string SubtitleBackgroundColor { get => Hex("subtitleBackgroundColor", "#000000"); set => SetChoice("subtitleBackgroundColor", value, "#000000"); }
        public static int SubtitleBackgroundOpacity { get => Range("subtitleBackgroundOpacity", 0, 100, 0); set => SetNumber("subtitleBackgroundOpacity", value, 0); }

        // Gardé sur la TV, comme le web (« Appareil ») : longueur du saut, en secondes.
        const string SeekPref = "controls.seek";
        static int? seekStep;
        public static int SeekStep
        {
            get
            {
                if (seekStep == null)
                {
                    int v = 10;
                    try { if (Preference.Contains(SeekPref)) v = Preference.Get<int>(SeekPref); } catch (Exception) { }
                    seekStep = Array.IndexOf(SeekSteps, v) >= 0 ? v : 10;
                }
                return seekStep.Value;
            }
            set
            {
                seekStep = value;
                Save(() => Preference.Set(SeekPref, value));
            }
        }

        // Gardé sur la TV, comme le web : vitesse tant que OK est maintenu dans le lecteur.
        public static readonly float[] HoldRates = { 1.5f, 2f, 3f };
        const string HoldPref = "controls.hold";
        static float? holdRate;
        public static float HoldRate
        {
            get
            {
                if (holdRate == null)
                {
                    float v = 2f;
                    try
                    {
                        if (Preference.Contains(HoldPref))
                            float.TryParse(Preference.Get<string>(HoldPref), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
                    }
                    catch (Exception) { }
                    holdRate = Array.IndexOf(HoldRates, v) >= 0 ? v : 2f;
                }
                return holdRate.Value;
            }
            set
            {
                holdRate = value;
                string text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Save(() => Preference.Set(HoldPref, text));
            }
        }

        // Gardé sur la TV, comme le web : place à utiliser pour les images, en Mo
        // (le web va jusqu'à 5 Go ; la TV n'a qu'environ 1 Go libre : 1 Go retiré, choix de l'auteur du 2026-11-15).
        public static readonly int[] CacheLimits = { 250, 500 };
        const string CachePref = "cache.size";
        static int? cacheLimit;
        public static int CacheLimitMb
        {
            get
            {
                if (cacheLimit == null)
                {
                    int v = 500;
                    try { if (Preference.Contains(CachePref)) v = Preference.Get<int>(CachePref); } catch (Exception) { }
                    cacheLimit = Array.IndexOf(CacheLimits, v) >= 0 ? v : 500;
                }
                return cacheLimit.Value;
            }
            set
            {
                cacheLimit = value;
                Save(() => Preference.Set(CachePref, value));
            }
        }

        // Gardé sur la TV, comme le web : « Qui regarde ? » à l'ouverture et au retour après
        // une demi-heure. Oui par défaut (le web : non), comportement de Native AIO avant le réglage.
        const string AskWhoPref = "who.ask";
        static bool? askWho;
        public static bool AskWho
        {
            get
            {
                if (askWho == null)
                {
                    bool v = true;
                    try { if (Preference.Contains(AskWhoPref)) v = Preference.Get<bool>(AskWhoPref); } catch (Exception) { }
                    askWho = v;
                }
                return askWho.Value;
            }
            set
            {
                askWho = value;
                Save(() => Preference.Set(AskWhoPref, value));
            }
        }

        // Interface (web : settings.interface)
        public static readonly string[] PosterSizes = { "small", "medium", "large" };
        public static bool MergeNextUp { get => Flag("mergeNextUp", false); set => SetFlag("mergeNextUp", value, false); }
        public static bool CombineSearch { get => Flag("combineSearch", false); set => SetFlag("combineSearch", value, false); }
        // Flou des épisodes pas commencés (web : settings.interface.blurSpoilers), non par défaut.
        public static bool BlurSpoilers { get => Flag("blurSpoilers", false); set => SetFlag("blurSpoilers", value, false); }
        public static string PosterSize { get => Choice("posterSize", PosterSizes, "medium"); set => SetChoice("posterSize", value, "medium"); }
        // Sous les affiches (posterText) : « title,year » par défaut (absent), « none » pour rien.
        public static readonly string[] PosterTexts = { "title,year", "title", "year", "none" };
        // Bannière à la une (module 14b). heroMode du compte (web : rotate par défaut, follow,
        // corner) ; « Désactivée » (off) est propre à Native AIO, gardée sur la TV et jamais écrite
        // sur le compte (le web ne connaît pas cette valeur).
        public static readonly string[] HeroModes = { "rotate", "follow", "corner" };
        public static string HeroMode { get => Choice("heroMode", HeroModes, "rotate"); set => SetChoice("heroMode", value, "rotate"); }
        // Temps passé sur chaque titre quand la bannière défile (heroSeconds du web, compte W).
        public static readonly int[] HeroSecondsChoices = { 5, 7, 9, 12, 15, 20, 30 };
        public static int HeroSeconds { get => Number("heroSeconds", HeroSecondsChoices, 9); set => SetNumber("heroSeconds", value, 9); }
        const string HeroOffPref = "hero.off";
        static bool? heroOff;
        static bool HeroOff
        {
            get
            {
                if (heroOff == null)
                {
                    bool v = false;
                    try { if (Preference.Contains(HeroOffPref)) v = Preference.Get<bool>(HeroOffPref); } catch (Exception) { }
                    heroOff = v;
                }
                return heroOff.Value;
            }
            set
            {
                heroOff = value;
                Save(() => Preference.Set(HeroOffPref, value));
            }
        }
        // « off », ou le mode du compte.
        public static string Hero
        {
            get => HeroOff ? "off" : HeroMode;
            set
            {
                if (value == "off") { HeroOff = true; return; }
                HeroOff = false;
                if (value != HeroMode) HeroMode = value;
            }
        }
        // À la une sur l'accueil (featured), lu seulement : null = automatique (premiers
        // catalogues de films et de séries), liste vide = rien (« none »), sinon jusqu'à 4
        // sources : « resume », « next-up », « view:<id> » (web : parseFeatured).
        public static List<string> Featured
        {
            get
            {
                string r = Raw("featured");
                if (r == null || r == "auto") return null;
                var l = new List<string>();
                if (r == "none") return l;
                foreach (string s in r.Split(',')) if (s.Length > 0 && l.Count < 4) l.Add(s);
                return l;
            }
        }
        public static bool PosterTitle => PosterLine("title");
        public static bool PosterYear => PosterLine("year");
        public static string PosterText
        {
            get => PosterTitle ? (PosterYear ? "title,year" : "title") : PosterYear ? "year" : "none";
            set => SetChoice("posterText", value, "title,year");
        }
        static bool PosterLine(string line) => Array.IndexOf((Raw("posterText") ?? "title,year").Split(','), line) >= 0;

        // Thème (web : settings.theme.colors) : couleurs « #rrggbb » ; celles du thème Default
        // ne sont pas écrites (absentes), comme le web, pour qu'un changement du défaut s'applique.
        public const string DefaultAccent = "#6152df", DefaultBackground = "#070707";
        public static string AccentColor => Hex("accentColor", DefaultAccent);
        public static string BackgroundColor => Hex("backgroundColor", DefaultBackground);
        // Arrière-plan d'après l'image (web : settings.theme.artBackground), non par défaut.
        public static bool ArtBackground { get => Flag("artBackground", false); set => SetFlag("artBackground", value, false); }

        // Les deux à la fois (un préréglage), un seul envoi.
        public static void SetTheme(string accent, string background)
        {
            if (user == null) return;
            foreach (var (key, v, d) in new[] { ("accentColor", accent, DefaultAccent), ("backgroundColor", background, DefaultBackground) })
            {
                string value = v == d ? null : v;
                if (value == null) web.Remove(key); else web[key] = value;
                pendingWeb[key] = value ?? "";
            }
            Done();
        }

        // Affiches par ligne d'une grille qui en met « medium » à la taille moyenne
        // (choix de l'auteur : moyenne = la disposition d'avant ; 8 → 10 / 6, 5 → 6 / 4).
        public static int GridCols(int medium)
        {
            switch (PosterSize)
            {
                case "small": return medium >= 8 ? medium + 2 : medium + 1;
                case "large": return medium >= 8 ? medium - 2 : medium - 1;
                default: return medium;
            }
        }

        // ---- Lecture des valeurs ----

        static string Raw(string key) => web.TryGetValue(key, out string v) && !string.IsNullOrEmpty(v) ? v : null;

        static bool Flag(string key, bool fallback)
        {
            string r = Raw(key);
            return r == null ? fallback : r == "1";
        }

        static string Choice(string key, string[] options, string fallback)
        {
            string r = Raw(key);
            return r != null && Array.IndexOf(options, r) >= 0 ? r : fallback;
        }

        static int Number(string key, int[] options, int fallback)
        {
            string r = Raw(key);
            return r != null && int.TryParse(r, out int n) && Array.IndexOf(options, n) >= 0 ? n : fallback;
        }

        // Couleur « #rrggbb » (web : isHex), sinon la valeur par défaut.
        static string Hex(string key, string fallback)
        {
            string r = Raw(key);
            if (r == null || r.Length != 7 || r[0] != '#') return fallback;
            for (int i = 1; i < 7; i++) if (!Uri.IsHexDigit(r[i])) return fallback;
            return r.ToLowerInvariant();
        }

        // Nombre entre min et max (web : syncedNumber), sinon la valeur par défaut.
        static int Range(string key, int min, int max, int fallback)
        {
            string r = Raw(key);
            return r != null && double.TryParse(r, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)
                && d >= min && d <= max ? (int)Math.Round(d) : fallback;
        }

        // Réglage Jellyfin « désactivé seulement s'il vaut false ».
        static bool ConfigFlag(string key) => !(config.TryGetValue(key, out object v) && v is bool b && !b);

        // Liste du compte J si elle n'est pas vide, sinon la seule valeur (web : languages).
        static List<string> ConfigList(string list, string one)
        {
            var l = new List<string>();
            if (config.TryGetValue(list, out object o) && o is List<object> items)
                foreach (object it in items) if (it is string s && s.Length > 0) l.Add(s);
            if (l.Count == 0 && config.TryGetValue(one, out object v) && v is string single && single.Length > 0) l.Add(single);
            return l;
        }

        // ---- Écriture ----

        static void SetFlag(string key, bool v, bool fallback) => SetWeb(key, v == fallback ? null : v ? "1" : "0");
        static void SetChoice(string key, string v, string fallback) => SetWeb(key, v == fallback ? null : v);
        static void SetNumber(string key, int v, int fallback) => SetWeb(key, v == fallback ? null : v.ToString());

        static void SetWeb(string key, string value)
        {
            if (user == null) return;
            if (value == null) web.Remove(key); else web[key] = value;
            pendingWeb[key] = value ?? "";
            Done();
        }

        static void SetConfig(string key, object value) => SetConfig(new Dictionary<string, object> { [key] = value });

        static void SetConfig(Dictionary<string, object> values)
        {
            if (user == null) return;
            foreach (var kv in values) { config[kv.Key] = kv.Value; pendingConfig[kv.Key] = kv.Value; }
            Done();
        }

        static void Done()
        {
            SaveLocal();
            Changed?.Invoke();
            SchedulePush();
        }

        async static void SchedulePush()
        {
            int p = ++pushSeq;
            await Task.Delay(PushDelayMs);
            if (p != pushSeq || user == null) return;
            var w = new Dictionary<string, string>(pendingWeb);
            var c = new Dictionary<string, object>(pendingConfig);
            pendingWeb.Clear();
            pendingConfig.Clear();
            string u = user;
            Api api = Session.Client;
            Task previous = pushing;
            pushing = Task.Run(async () =>
            {
                await previous;
                await Push(api, u, w, c);
            });
            await pushing;
        }

        static string WebPath(string u) => $"/DisplayPreferences/{WebId}?client={WebId}&userId={Uri.EscapeDataString(u)}";

        // Préférences du web sur le serveur ; jamais enregistrées (404 possible) : aucune.
        static async Task<byte[]> WebBytes(Api api, string u)
        {
            try { return await api.GetBytes(WebPath(u)); }
            catch (ApiError e) when (e.Status == 404) { return System.Text.Encoding.UTF8.GetBytes("{}"); }
        }

        // Relit ce que garde le serveur, y change les réglages modifiés, renvoie le tout.
        static async Task Push(Api api, string u, Dictionary<string, string> w, Dictionary<string, object> c)
        {
            try
            {
                if (w.Count > 0)
                {
                    var prefs = new Dictionary<string, object>();
                    if (JsonTree.Parse(await WebBytes(api, u)) is Dictionary<string, object> d
                        && d.TryGetValue("CustomPrefs", out object cp) && cp is Dictionary<string, object> cur)
                        prefs = cur;
                    foreach (var kv in w) prefs[kv.Key] = kv.Value;
                    prefs[SavedMark] = "1";
                    var body = new Dictionary<string, object> { ["Id"] = WebId, ["Client"] = WebId, ["CustomPrefs"] = prefs };
                    await api.PostJson(WebPath(u), JsonTree.Bytes(body));
                    Probe.Log("prefs", "envoyé (compte W) : " + string.Join(", ", w.Keys));
                }
                if (c.Count > 0)
                {
                    if (!(JsonTree.Parse(await api.GetBytes("/Users/Me")) is Dictionary<string, object> me)
                        || !me.TryGetValue("Configuration", out object o) || !(o is Dictionary<string, object> cfg))
                        throw new FormatException("Configuration absente");
                    foreach (var kv in c) cfg[kv.Key] = kv.Value;
                    await api.PostJson("/Users/Configuration?userId=" + Uri.EscapeDataString(u), JsonTree.Bytes(cfg));
                    Probe.Log("prefs", "envoyé (compte J) : " + string.Join(", ", c.Keys));
                }
                LastError = null;
            }
            catch (Exception e)
            {
                LastError = e is ApiError a ? "HTTP " + a.Status : e.GetType().Name;
                Probe.Log("prefs", "échec de l'envoi : " + LastError);
            }
        }

        // ---- Choix par série (web : show-tracks.ts) ----
        // /DisplayPreferences/aiostreams-web-tracks, CustomPrefs : identifiant de la série →
        // « audio|sous-titres » (un code de langue chacun, « off » pour aucun sous-titre, ou
        // vide ; le type du sous-titre peut suivre sa langue : « eng:forced »).

        const string TracksId = "aiostreams-web-tracks";
        static Dictionary<string, string> shows = new Dictionary<string, string>();

        static string TracksPath(string u) => $"/DisplayPreferences/{TracksId}?client={TracksId}&userId={Uri.EscapeDataString(u)}";

        static async Task<byte[]> TracksBytes(Api api, string u)
        {
            try { return await api.GetBytes(TracksPath(u)); }
            catch (ApiError e) when (e.Status == 404) { return System.Text.Encoding.UTF8.GetBytes("{}"); }
        }

        // Le serveur ajoute ses propres valeurs : un choix contient toujours « | ».
        static Dictionary<string, string> ShowsOf(object tree)
        {
            var s = new Dictionary<string, string>();
            if (tree is Dictionary<string, object> d && d.TryGetValue("CustomPrefs", out object o) && o is Dictionary<string, object> cp)
                foreach (var kv in cp)
                    if (kv.Value is string v && v.Contains("|")) s[kv.Key] = v;
            return s;
        }

        // Partie d'un choix (0 : audio, 1 : sous-titres), null s'il n'y en a pas.
        static string ShowPart(string seriesId, int part)
        {
            if (seriesId == null || !shows.TryGetValue(seriesId, out string e)) return null;
            int bar = e.IndexOf('|');
            string v = part == 0 ? e.Substring(0, bar) : e.Substring(bar + 1);
            return v.Length > 0 ? v : null;
        }

        // Langue audio choisie pour la série, null s'il n'y en a pas.
        public static string ShowAudio(string seriesId) => ShowPart(seriesId, 0);

        // Sous-titres choisis pour la série : « off », une langue, ou « langue:type » ; null : aucun choix.
        public static string ShowSubtitle(string seriesId) => ShowPart(seriesId, 1);

        // Enregistre (ou oublie, avec null) la langue audio choisie pour la série.
        public static void SetShowAudio(string seriesId, string lang) => SetShow(seriesId, lang, ShowPart(seriesId, 1));

        // Enregistre (ou oublie, avec null) les sous-titres choisis pour la série.
        public static void SetShowSubtitle(string seriesId, string pick) => SetShow(seriesId, ShowPart(seriesId, 0), pick);

        static void SetShow(string seriesId, string audio, string sub)
        {
            if (user == null || seriesId == null) return;
            string old = shows.TryGetValue(seriesId, out string e) ? e : "";
            string entry = !string.IsNullOrEmpty(audio) || !string.IsNullOrEmpty(sub) ? (audio ?? "") + "|" + (sub ?? "") : "";
            if (entry == old) return;
            if (entry.Length > 0) shows[seriesId] = entry; else shows.Remove(seriesId);
            SaveLocal();
            string u = user;
            Api api = Session.Client;
            Task previous = pushing;
            pushing = Task.Run(async () =>
            {
                await previous;
                try
                {
                    var prefs = new Dictionary<string, object>();
                    if (JsonTree.Parse(await TracksBytes(api, u)) is Dictionary<string, object> d
                        && d.TryGetValue("CustomPrefs", out object cp) && cp is Dictionary<string, object> cur)
                        prefs = cur;
                    // Un choix oublié est envoyé vide : le serveur peut garder une clé qu'on ne lui envoie pas.
                    prefs[seriesId] = entry;
                    var body = new Dictionary<string, object> { ["Id"] = TracksId, ["Client"] = TracksId, ["CustomPrefs"] = prefs };
                    await api.PostJson(TracksPath(u), JsonTree.Bytes(body));
                    Probe.Log("prefs", $"envoyé (choix par série) : {seriesId} = « {entry} »");
                }
                catch (Exception ex)
                {
                    LastError = ex is ApiError a ? "HTTP " + a.Status : ex.GetType().Name;
                    Probe.Log("prefs", "échec de l'envoi (choix par série) : " + LastError);
                }
            });
        }

        // ---- Utilisateur et serveur ----

        // À l'ouverture et à chaque changement d'utilisateur (Session) : copie de la TV, puis serveur.
        public static void Use(string userId)
        {
            if (userId == user) return;
            user = userId;
            gen++;
            pushSeq++;
            pendingWeb.Clear();
            pendingConfig.Clear();
            web = new Dictionary<string, string>();
            config = new Dictionary<string, object>();
            shows = new Dictionary<string, string>();
            if (userId != null) LoadLocal();
            Changed?.Invoke();
            Sync();
        }

        // Relit le serveur ; un réglage modifié et pas encore envoyé garde sa valeur.
        public static async void Sync()
        {
            string u = user;
            if (u == null || Session.Base == null || Session.Token == null) return;
            int g = gen;
            Api api = Session.Client;
            try
            {
                await pushing;
                byte[] wb = await WebBytes(api, u);
                byte[] mb = await api.GetBytes("/Users/Me");
                // Choix par série : un échec ne doit pas empêcher de relire le reste.
                byte[] tb = null;
                try { tb = await TracksBytes(api, u); } catch (Exception) { }
                var (w, c, s) = await Task.Run(() => (WebOf(JsonTree.Parse(wb)), ConfigOf(JsonTree.Parse(mb)), tb == null ? null : ShowsOf(JsonTree.Parse(tb))));
                if (g != gen) return;
                if (s != null) shows = s;
                foreach (var kv in pendingWeb) { if (kv.Value.Length == 0) w.Remove(kv.Key); else w[kv.Key] = kv.Value; }
                web = w;
                if (c != null)
                {
                    foreach (var kv in pendingConfig) c[kv.Key] = kv.Value;
                    config = c;
                }
                LastError = null;
                LastSync = DateTime.Now;
                SaveLocal();
                Changed?.Invoke();
                Probe.Log("prefs", $"relu : {web.Count} réglages (compte W), compte J {(c != null ? "lu" : "absent")}, {shows.Count} choix par série"
                    + (c != null ? $" ; audio : langues [{string.Join(", ", AudioLanguages)}] ({(AudioLists ? "liste" : "une seule")}), types {(AudioTypesKnown ? "connus" : "absents")}"
                        + $" ; sous-titres : langues [{string.Join(", ", SubtitleLanguages)}] ({(SubtitleLists ? "liste" : "une seule")}), mode {SubtitleMode}, types {(SubtitleTypesKnown ? "connus" : "absents")}, "
                        + $"mes langues seulement {(SubtitleOnlyMineKnown ? (SubtitleOnlyMine ? "oui" : "non") : "absent")}" : ""));
            }
            catch (Exception e)
            {
                if (g != gen) return;
                LastError = e is ApiError a ? "HTTP " + a.Status : e.GetType().Name;
                Probe.Log("prefs", "échec de la lecture : " + LastError);
            }
        }

        // Préférences du web : seulement si elles ont été enregistrées (marque « saved »), sinon
        // le serveur peut y avoir mis ses propres valeurs. Clés vides retirées.
        static Dictionary<string, string> WebOf(object tree)
        {
            var w = new Dictionary<string, string>();
            if (tree is Dictionary<string, object> d && d.TryGetValue("CustomPrefs", out object o) && o is Dictionary<string, object> cp
                && cp.TryGetValue(SavedMark, out object mark) && mark is string m && m.Length > 0)
                foreach (var kv in cp)
                    if (kv.Key != SavedMark && kv.Value is string s && s.Length > 0) w[kv.Key] = s;
            return w;
        }

        static Dictionary<string, object> ConfigOf(object tree) =>
            tree is Dictionary<string, object> d && d.TryGetValue("Configuration", out object o) ? o as Dictionary<string, object> : null;

        // ---- Copie sur la TV ----

        static string WebKey(string u) => "prefs.web." + u;
        static string ConfigKey(string u) => "prefs.config." + u;
        static string TracksKey(string u) => "prefs.tracks." + u;

        // Réglages du compte J lus par Native AIO, seuls copiés sur la TV.
        static readonly string[] ConfigKeys =
        {
            "EnableNextEpisodeAutoPlay", "AudioLanguagePreference", "AudioLanguages", "PlayDefaultAudioTrack",
            "AudioTypes", "RememberAudioSelections", "SubtitleLanguagePreference", "SubtitleLanguages", "SubtitleMode",
            "SubtitleTypes", "ListPreferredSubtitlesOnly", "RememberSubtitleSelections",
        };

        static void LoadLocal()
        {
            try
            {
                if (Preference.Contains(WebKey(user)) && JsonTree.Parse(Preference.Get<string>(WebKey(user))) is Dictionary<string, object> d)
                    foreach (var kv in d) if (kv.Value is string s && s.Length > 0) web[kv.Key] = s;
                if (Preference.Contains(ConfigKey(user)) && JsonTree.Parse(Preference.Get<string>(ConfigKey(user))) is Dictionary<string, object> c)
                    config = c;
                if (Preference.Contains(TracksKey(user)) && JsonTree.Parse(Preference.Get<string>(TracksKey(user))) is Dictionary<string, object> t)
                    foreach (var kv in t) if (kv.Value is string s && s.Contains("|")) shows[kv.Key] = s;
            }
            catch (Exception) { }
        }

        static void SaveLocal()
        {
            string u = user;
            if (u == null) return;
            var w = new Dictionary<string, object>();
            foreach (var kv in web) w[kv.Key] = kv.Value;
            string wt = JsonTree.Write(w);
            // Seuls les réglages lus par Native AIO : la copie reste petite.
            var c = new Dictionary<string, object>();
            foreach (string k in ConfigKeys)
                if (config.TryGetValue(k, out object v)) c[k] = v;
            string ct = JsonTree.Write(c);
            var t = new Dictionary<string, object>();
            foreach (var kv in shows) t[kv.Key] = kv.Value;
            string tt = JsonTree.Write(t);
            Save(() => { Preference.Set(WebKey(u), wt); Preference.Set(ConfigKey(u), ct); Preference.Set(TracksKey(u), tt); });
        }

        // Hors du fil d'interface (voir LECONS.md : jusqu'à 75 ms par écriture), dans l'ordre.
        static void Save(Action write)
        {
            Store.Run("réglages", write); // hors du fil d'interface, file commune (Store)
        }
    }
}
