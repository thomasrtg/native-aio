// Contenu de l'accueil, demandé comme la version web (lib/queries.ts) :
// catalogues de l'utilisateur, reprises, épisodes suivants, pages d'un
// catalogue, détails d'un titre. Adresses des images reprises de lib/images.ts.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace NativeAIO.Core
{
    static class Library
    {
        // Types de catalogues montrés, comme la version web.
        static readonly HashSet<string> Shown = new HashSet<string> { "movies", "tvshows", "boxsets", "mixed" };

        static string Q(string path, params (string, object)[] query)
        {
            var sb = new StringBuilder(path);
            char sep = '?';
            foreach (var (k, v) in query)
            {
                if (v == null) continue;
                string s = v is bool b ? (b ? "true" : "false") : Convert.ToString(v, CultureInfo.InvariantCulture);
                if (s.Length == 0) continue;
                sb.Append(sep).Append(k).Append('=').Append(Uri.EscapeDataString(s));
                sep = '&';
            }
            return sb.ToString();
        }

        // Les catalogues de l'accueil : types montrés, sans ceux qui exigent un genre
        // (Découvrir les veut tous : withGenreRequired).
        public static async Task<List<Item>> Views(bool withGenreRequired = false)
        {
            ItemsResult r = await Session.Client.Get<ItemsResult>(Q("/UserViews", ("userId", Session.UserId)));
            var list = new List<Item>();
            foreach (Item v in r?.Items ?? new Item[0])
            {
                if (v.CollectionType != null && !Shown.Contains(v.CollectionType)) continue;
                if (v.Aio?.GenreRequired == true && !withGenreRequired) continue;
                list.Add(v);
            }
            return list;
        }

        // ---- Découvrir (pages/discover.tsx de la version web) ----

        // La sorte d'un catalogue : Movie, Series, BoxSet ; null pour un catalogue mixte.
        public static string KindOf(Item view)
        {
            switch (view?.CollectionType)
            {
                case "movies": return "Movie";
                case "tvshows": return "Series";
                case "boxsets": return "BoxSet";
                default: return null;
            }
        }

        // Les genres d'un catalogue (pour certains : années, services ou langues).
        public static Task<ItemsResult> Genres(string viewId) =>
            Session.Client.Get<ItemsResult>(Q("/Genres", ("userId", Session.UserId), ("ParentId", viewId)));

        // Une page d'un catalogue filtrée (useItemPages du web) : types, genre, non vus.
        public static Task<ItemsResult> Browse(string viewId, string types, string genreId, bool unplayed, int start, int size)
        {
            return Session.Client.GetSlow<ItemsResult>(Q("/Items",
                ("userId", Session.UserId), ("ParentId", viewId), ("StartIndex", start), ("Limit", size),
                ("IncludeItemTypes", types), ("GenreIds", genreId), ("Recursive", true),
                ("Filters", unplayed ? "IsUnplayed" : null), ("EnableTotalRecordCount", true)));
        }

        // « Reprendre » : lectures en cours et épisodes suivants réunis (recherche).
        public static async Task<List<Item>> Continue() => (await HomeRows(true)).Continue;

        // « Reprendre » et « À suivre » de l'accueil (pages/home.tsx du web). Réunis
        // (réglage mergeNextUp) : les épisodes suivants vont dans « Reprendre », après les
        // lectures en cours, et « À suivre » vaut null. Dans « Reprendre », la plus récente
        // d'abord, un seul élément par série (mergeRows de la version web).
        public static async Task<(List<Item> Continue, List<Item> NextUp)> HomeRows(bool merge)
        {
            Task<ItemsResult> resume = Session.Client.Get<ItemsResult>(Q("/UserItems/Resume",
                ("userId", Session.UserId), ("Limit", 24), ("MediaTypes", "Video")));
            Task<ItemsResult> next = Session.Client.Get<ItemsResult>(Q("/Shows/NextUp",
                ("userId", Session.UserId), ("Limit", 24), ("EnableResumable", false)));
            await Task.WhenAll(resume, next);
            var seen = new HashSet<string>();
            var list = new List<Item>();
            foreach (Item[] part in new[] { resume.Result?.Items, merge ? next.Result?.Items : null })
            {
                foreach (Item it in part ?? new Item[0])
                {
                    if (it.Id == null || seen.Contains(it.Id) || (it.SeriesId != null && seen.Contains(it.SeriesId))) continue;
                    seen.Add(it.Id);
                    if (it.SeriesId != null) seen.Add(it.SeriesId);
                    list.Add(it);
                }
            }
            // Tri stable : à date égale (épisodes suivants sans date), l'ordre reçu reste.
            var order = new List<(Item, long, int)>();
            for (int i = 0; i < list.Count; i++) order.Add((list[i], WatchedAt(list[i]), i));
            order.Sort((a, b) => a.Item2 != b.Item2 ? b.Item2.CompareTo(a.Item2) : a.Item3.CompareTo(b.Item3));
            list.Clear();
            foreach (var o in order) list.Add(o.Item1);
            if (merge) return (list, null);
            // « À suivre » : tel que le serveur l'envoie, comme le web.
            var nextUp = new List<Item>();
            foreach (Item it in next.Result?.Items ?? new Item[0]) if (it.Id != null) nextUp.Add(it);
            return (list, nextUp);
        }

        // ---- Calendrier (useUpcoming et useCalendar de lib/queries.ts du web) ----

        // « À venir » : le prochain épisode de chaque série suivie dont on est à jour.
        public static async Task<List<Item>> Upcoming()
        {
            ItemsResult r = await Session.Client.Get<ItemsResult>(Q("/Shows/Upcoming", ("userId", Session.UserId), ("Limit", 24)));
            return new List<Item>(r?.Items ?? new Item[0]);
        }

        // Épisodes et films de la bibliothèque qui sortent entre deux dates (déjà sortis compris).
        public static Task<ItemsResult> Calendar(DateTime from, DateTime to) =>
            Session.Client.GetSlow<ItemsResult>(Q("/Items", ("userId", Session.UserId), ("IncludeItemTypes", "Episode,Movie"),
                ("Recursive", true), ("MinPremiereDate", Iso(from)), ("MaxPremiereDate", Iso(to)),
                ("SortBy", "PremiereDate"), ("Limit", 500)));

        static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        // Date sans heure : le serveur de l'auteur envoie « 2026-11-13T00:00:00.002Z » (minuit en
        // temps universel, quelques millièmes en plus). Le web ne reconnaît que « .0000000Z » ;
        // ici, toute heure à minuit pile en temps universel compte comme une date sans heure.
        static bool DateOnly(string iso, DateTime utc) =>
            !iso.Contains("T") || (utc.Hour == 0 && utc.Minute == 0 && utc.Second == 0);

        // Le jour de sortie, à l'heure de la TV ; une date sans heure garde son jour (airDay du web).
        public static DateTime? AirDay(string iso)
        {
            DateTime? d = Date(iso);
            if (d == null) return null;
            DateTime u = d.Value;
            if (DateOnly(iso, u)) return new DateTime(u.Year, u.Month, u.Day, 0, 0, 0, DateTimeKind.Local);
            return u.ToLocalTime().Date;
        }

        // L'heure de sortie à l'heure de la TV (« 21:00 »), ou null pour une date sans heure.
        public static string AirTime(string iso)
        {
            DateTime? d = Date(iso);
            if (d == null || DateOnly(iso, d.Value)) return null;
            return d.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        static long WatchedAt(Item it)
        {
            string at = it.Aio?.ShowWatchedAt ?? it.UserData?.LastPlayedDate;
            return at != null && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime d) ? d.Ticks : 0;
        }

        // Ce qu'un catalogue contient, selon son type.
        public static string Types(Item view)
        {
            switch (view.CollectionType)
            {
                case "movies": return "Movie";
                case "tvshows": return "Series";
                case "boxsets": return "BoxSet";
                default: return "Movie,Series";
            }
        }

        // Une page d'un catalogue, dans l'ordre du catalogue.
        public static Task<ItemsResult> Page(Item view, int start, int size)
        {
            return Session.Client.Get<ItemsResult>(Q("/Items",
                ("userId", Session.UserId), ("ParentId", view.Id), ("StartIndex", start), ("Limit", size),
                ("IncludeItemTypes", Types(view)), ("Recursive", true), ("EnableTotalRecordCount", true)));
        }

        // Premiers titres d'un catalogue avec genres et résumé (bannière à la une, comme
        // useLibraryHeads du web).
        public static Task<ItemsResult> Heads(Item view, int size)
        {
            return Session.Client.Get<ItemsResult>(Q("/Items",
                ("userId", Session.UserId), ("ParentId", view.Id), ("StartIndex", 0), ("Limit", size),
                ("IncludeItemTypes", Types(view)), ("Recursive", true), ("Fields", "Genres,Overview")));
        }

        // Genres, résumé et bandes-annonces d'un titre, demandés par son identifiant (comme
        // requestItem du web).
        public static async Task<Item> Details(string id)
        {
            ItemsResult r = await Session.Client.Get<ItemsResult>(Q("/Items",
                ("userId", Session.UserId), ("Ids", id), ("Fields", "Genres,Overview,RemoteTrailers")));
            return r?.Items != null && r.Items.Length > 0 ? r.Items[0] : null;
        }

        // ---- Page d'un titre (pages/item.tsx et lib/queries.ts de la version web) ----

        // Ce que la page montre en plus d'une carte (DETAIL_FIELDS du web).
        const string DetailFields = "Genres,Overview,People,ProductionLocations,RemoteTrailers,Studios,Taglines";

        // La fiche complète, demandée par son identifiant et non par /Items/{id},
        // qui fait chercher les versions par les addons du serveur à chaque page.
        public static async Task<Item> Full(string id)
        {
            ItemsResult r = await Session.Client.Get<ItemsResult>(Q("/Items",
                ("userId", Session.UserId), ("Ids", id), ("Fields", DetailFields)));
            return r?.Items != null && r.Items.Length > 0 ? r.Items[0] : null;
        }

        public static Task<ItemsResult> Seasons(string seriesId) =>
            Session.Client.Get<ItemsResult>(Q($"/Shows/{seriesId}/Seasons", ("userId", Session.UserId)));

        public static Task<ItemsResult> Episodes(string seriesId, string seasonId) =>
            Session.Client.Get<ItemsResult>(Q($"/Shows/{seriesId}/Episodes",
                ("userId", Session.UserId), ("SeasonId", seasonId), ("Fields", "Overview")));

        // L'épisode par lequel une série continue.
        public static async Task<Item> NextUp(string seriesId)
        {
            ItemsResult r = await Session.Client.Get<ItemsResult>(Q("/Shows/NextUp",
                ("userId", Session.UserId), ("SeriesId", seriesId), ("Limit", 1)));
            return r?.Items != null && r.Items.Length > 0 ? r.Items[0] : null;
        }

        public static Task<ItemsResult> Similar(string id) =>
            Session.Client.Get<ItemsResult>(Q($"/Items/{id}/Similar", ("userId", Session.UserId), ("Limit", 20), ("Fields", "ProviderIds,Path")));

        // Les titres d'une collection (une seule page de 60).
        public static Task<ItemsResult> Members(string id) =>
            Session.Client.Get<ItemsResult>(Q("/Items", ("userId", Session.UserId), ("ParentId", id),
                ("IncludeItemTypes", "Movie,Series"), ("StartIndex", 0), ("Limit", 60)));

        // ---- Favoris, Historique, fiche d'une personne (module 13b) ----

        // Favoris (useItemPages avec filter « favorite » du web) : Movie,Series, Movie ou Series.
        public static Task<ItemsResult> Favorites(string types, int start, int size, string fields = null) =>
            Session.Client.Get<ItemsResult>(Q("/Items", ("userId", Session.UserId), ("Filters", "IsFavorite"),
                ("IncludeItemTypes", types), ("Recursive", true), ("StartIndex", start), ("Limit", size),
                ("EnableTotalRecordCount", true), ("Fields", fields)));

        // Films et épisodes vus, le plus récent d'abord (useOwnHistory du web).
        public static Task<ItemsResult> Played(string types, int start, int size) =>
            Session.Client.Get<ItemsResult>(Q("/Items", ("userId", Session.UserId), ("Filters", "IsPlayed"),
                ("SortBy", "DatePlayed"), ("SortOrder", "Descending"), ("Recursive", true),
                ("IncludeItemTypes", types), ("StartIndex", start), ("Limit", size), ("EnableTotalRecordCount", true)));

        // Ce dans quoi une personne a joué ou travaillé, le plus récent d'abord (usePersonItems du web).
        public static Task<ItemsResult> PersonItems(string personId, string types, int start, int size) =>
            Session.Client.GetSlow<ItemsResult>(Q("/Items", ("userId", Session.UserId), ("PersonIds", personId),
                ("IncludeItemTypes", types), ("Recursive", true), ("SortBy", "PremiereDate,ProductionYear,SortName"),
                ("SortOrder", "Descending"), ("StartIndex", start), ("Limit", size)));

        // ---- Recherche (useSearch de lib/queries.ts du web) ----

        // Une demande par rangée (Movie,Series ou chacun à part, Person), pour qu'aucune ne
        // remplisse la limite de l'autre. Délai long : le serveur peut interroger les addons.
        public static Task<ItemsResult> Search(string term, string types) =>
            Session.Client.GetSlow<ItemsResult>(Q("/Items", ("userId", Session.UserId), ("SearchTerm", term),
                ("Recursive", true), ("IncludeItemTypes", types), ("Limit", 48), ("Fields", "ProviderIds,Path")));

        // La saison où la page s'ouvre : la première pas encore vue, sans les épisodes spéciaux.
        public static Item OpeningSeason(Item[] seasons)
        {
            Item first = null;
            foreach (Item s in seasons)
            {
                if ((s.IndexNumber ?? 1) <= 0) continue;
                if (first == null) first = s;
                if (s.UserData?.Played != true) return s;
            }
            return first ?? (seasons.Length > 0 ? seasons[0] : null);
        }

        // L'épisode regardé à l'ouverture : celui en cours, sinon le premier pas vu et diffusé.
        public static int UpTo(List<Item> episodes)
        {
            for (int i = 0; i < episodes.Count; i++)
                if (episodes[i].UserData?.Played != true && Progress(episodes[i]) > 0) return i;
            for (int i = 0; i < episodes.Count; i++)
                if (episodes[i].UserData?.Played != true && !Unaired(episodes[i])) return i;
            return 0;
        }

        // Épisode annoncé mais pas encore diffusé (unavailableKind du web).
        public static bool Unaired(Item it)
        {
            if (it.LocationType != "Virtual") return false;
            DateTime? d = Date(it.PremiereDate);
            return d == null || d.Value > DateTime.UtcNow;
        }

        public static DateTime? Date(string iso) =>
            iso != null && DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime d) ? d : (DateTime?)null;

        // Dates écrites avec les noms de la langue de l'interface (sans dépendre des
        // cultures du système de la TV) : « ven. 13 nov. », « 30 oct. 2026 », « 30 mars 2025 ».
        public static string ShortDate(DateTime d, bool year)
        {
            DateTime l = d.ToLocalTime();
            string s = Lang.T("date.short", l.Day, Part("date.months_short", l.Month - 1));
            return year ? s + " " + l.Year : s;
        }

        public static string LongDate(DateTime d)
        {
            DateTime l = d.ToLocalTime();
            return Lang.T("date.long", l.Day, Part("date.months", l.Month - 1), l.Year);
        }

        // « mardi 10 novembre 2026 » (titre d'un jour de l'Historique).
        public static string DayLong(DateTime d)
        {
            DateTime l = d.ToLocalTime();
            return Lang.T("date.day_long", Part("date.days", (int)l.DayOfWeek), LongDate(d));
        }

        // « il y a 3 h », « il y a 2 j », puis la date (relativeTime du web).
        public static string Ago(DateTime d)
        {
            TimeSpan t = DateTime.UtcNow - d.ToUniversalTime();
            if (t.TotalMinutes < 1) return Lang.T("date.just_now");
            if (t.TotalHours < 1) return Lang.T("date.ago_minutes", (int)t.TotalMinutes);
            if (t.TotalDays < 1) return Lang.T("date.ago_hours", (int)t.TotalHours);
            if (t.TotalDays < 7) return Lang.T("date.ago_days", (int)t.TotalDays);
            return ShortDate(d, true);
        }

        public static string DayDate(DateTime d)
        {
            DateTime l = d.ToLocalTime();
            return Part("date.days_short", (int)l.DayOfWeek) + " " + ShortDate(d, false);
        }

        // « dans 5 jours », « demain », « aujourd'hui ».
        public static string Until(DateTime d)
        {
            int days = (int)Math.Round((d.ToLocalTime().Date - DateTime.Now.Date).TotalDays);
            if (days <= 0) return Lang.T("date.today");
            if (days == 1) return Lang.T("date.tomorrow");
            return Lang.T("date.in_days", days);
        }

        static string Part(string key, int i)
        {
            string[] a = Lang.T(key).Split(',');
            return i >= 0 && i < a.Length ? a[i].Trim() : "";
        }

        // Note sur 10 : « ★ 8.3 ».
        public static string Score(Item it) =>
            it.CommunityRating > 0 ? "\u2605 " + it.CommunityRating.Value.ToString("0.0", CultureInfo.InvariantCulture) : null;

        // Année et note du public, pour la légende d'une affiche : « 2021 · ★ 7.9 » (l'un ou l'autre seul).
        public static string YearScore(Item it)
        {
            string y = it.ProductionYear?.ToString(CultureInfo.InvariantCulture), s = Score(it);
            return y != null && s != null ? y + " · " + s : y ?? s;
        }

        // Code d'un épisode : « S02E08 ».
        public static string Code(Item it) =>
            it.ParentIndexNumber != null && it.IndexNumber != null
                ? "S" + it.ParentIndexNumber.Value.ToString("00") + "E" + it.IndexNumber.Value.ToString("00") : null;

        // Position de reprise : « 1:02:03 » ou « 12:34 ».
        public static string Clock(long ticks)
        {
            var t = TimeSpan.FromTicks(ticks);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        // Photo d'une personne (distribution, équipe technique).
        public static string PersonImage(Person p, int w) => Image(p.Id, "Primary", p.PrimaryImageTag, w);

        // ---- Versions (choix du lien) ----

        // Liste des versions d'un titre : le serveur fait chercher ses addons, ce qui
        // peut prendre plusieurs secondes. Profil repris du web : tout fichier se lit tel quel.
        // refresh : « Rechercher à nouveau » du web (Refresh: true), le serveur refait chercher
        // ses addons au lieu de reprendre une liste gardée.
        // SkipSubtitles (web, 2026-11-07) : le serveur ne demande pas les sous-titres des addons
        // pour la première version de la liste ; le lecteur les demande pour la version lue
        // (PlayerScreen.AskSubtitles).
        public static Task<PlaybackInfo> Versions(string id, bool refresh = false) =>
            Session.Client.PostSlow<PlaybackInfo>(Q($"/Items/{id}/PlaybackInfo", ("userId", Session.UserId)),
                new PlaybackInfoBody
                {
                    UserId = Session.UserId,
                    Fresh = true,
                    Refresh = refresh,
                    SkipSubtitles = true,
                    DeviceProfile = new DeviceProfile
                    {
                        DirectPlayProfiles = new[] { new DirectPlayProfile { Type = "Video" } },
                        SubtitleProfiles = new[] { new SubtitleProfile { Format = "vtt", Method = "External" } },
                    },
                });

        // Versions demandées dès l'ouverture de la page d'un titre (réglage « Trouver des versions
        // quand un titre s'ouvre ») : reprises par la liste pendant 3 min, comme le web
        // (PRELOADED_MS). Sur le fil d'interface seulement.
        const int PreloadedMs = 3 * 60000;
        static readonly Dictionary<string, (Task<PlaybackInfo> task, int at)> preloaded = new Dictionary<string, (Task<PlaybackInfo>, int)>();

        static bool Fresh((Task<PlaybackInfo> task, int at) p) =>
            unchecked(Environment.TickCount - p.at) < PreloadedMs && !p.task.IsFaulted && !p.task.IsCanceled;

        public static void Preload(string id)
        {
            if (id == null || (preloaded.TryGetValue(id, out var p) && Fresh(p))) return;
            var stale = new List<string>();
            foreach (var kv in preloaded) if (!Fresh(kv.Value)) stale.Add(kv.Key);
            foreach (string k in stale) preloaded.Remove(k);
            Task<PlaybackInfo> t = Versions(id);
            t.ContinueWith(x => { var _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            preloaded[id] = (t, Environment.TickCount);
            Probe.Log("versions", "versions demandées à l'ouverture de la page : " + id);
        }

        // Les versions préchargées si elles sont encore fraîches, sinon une nouvelle demande.
        public static Task<PlaybackInfo> VersionsOrPreloaded(string id, out bool reused)
        {
            reused = preloaded.TryGetValue(id, out var p) && Fresh(p);
            return reused ? p.task : Versions(id);
        }

        // Vrai si la liste préchargée de ce titre est déjà là, fraîche, avec une seule version
        // jouable (les messages des addons ne comptent pas) : la liste n'a pas à se montrer.
        public static bool PreloadedSingle(string id)
        {
            if (id == null || !preloaded.TryGetValue(id, out var p) || !Fresh(p) || p.task.Status != TaskStatus.RanToCompletion) return false;
            int n = 0;
            foreach (Source s in p.task.Result?.MediaSources ?? new Source[0]) if (s.Type != "Placeholder") n++;
            return n == 1;
        }

        // Nouvelle liste après « Rechercher à nouveau » : reprise par une prochaine ouverture de la
        // liste, comme le web (setQueryData).
        public static void Remember(string id, PlaybackInfo info) =>
            preloaded[id] = (Task.FromResult(info), Environment.TickCount);

        // Fonctions annoncées par le serveur (/System/Info/Public, comme useFeature du web).
        // Demandées une fois par serveur ; jamais null (aucune fonction si la demande échoue).
        static Task<AioFeatures> features;
        static string featuresBase;

        public static Task<AioFeatures> Features()
        {
            if (features == null || featuresBase != Session.Base || features.IsFaulted)
            {
                featuresBase = Session.Base;
                features = AskFeatures();
            }
            return features;
        }

        static async Task<AioFeatures> AskFeatures()
        {
            ServerAbout about = await Session.Client.Get<ServerAbout>("/System/Info/Public");
            return about?.Aio?.Features ?? new AioFeatures();
        }

        // Le serveur sait-il refaire chercher les versions (fonction « refreshVersions ») ?
        public static async Task<bool> CanRefresh() => ((await Features()).RefreshVersions ?? 0) > 0;

        // Une version précise : le serveur y ajoute les sous-titres trouvés par ses addons de
        // sous-titres (la liste des versions ne les contient pas toujours). Comme la version web
        // (requestPlaybackInfo avec sourceId) : identifiant propre de la version, sans « Fresh ».
        public static Task<PlaybackInfo> Version(string id, Source s) =>
            Session.Client.PostSlow<PlaybackInfo>(Q($"/Items/{id}/PlaybackInfo", ("userId", Session.UserId)),
                new PlaybackInfoBody
                {
                    UserId = Session.UserId,
                    MediaSourceId = s.Aio?.Id ?? s.Id,
                    DeviceProfile = new DeviceProfile
                    {
                        DirectPlayProfiles = new[] { new DirectPlayProfile { Type = "Video" } },
                        SubtitleProfiles = new[] { new SubtitleProfile { Format = "vtt", Method = "External" } },
                    },
                });

        // ---- Fin d'épisode (module 7) ----

        // Récap, intro, générique… : segments trouvés par le serveur (useSegments du web).
        public static async Task<Segment[]> Segments(string id)
        {
            SegmentsResult r = await Session.Client.Get<SegmentsResult>($"/MediaSegments/{id}");
            return r?.Items ?? new Segment[0];
        }

        // Épisode suivant d'une série (useAdjacentEpisodes du web) : null s'il n'y en a pas, ou
        // s'il n'est pas encore diffusé ou manque (l'enchaînement s'arrête, comme le web).
        public static async Task<Item> NextEpisode(Item ep) => (await Adjacent(ep)).next;

        // Épisodes précédent et suivant (AdjacentTo renvoie le précédent, l'épisode et le suivant).
        // Un épisode non diffusé ou manquant (Virtual) n'est pas proposé.
        public static async Task<(Item prev, Item next)> Adjacent(Item ep)
        {
            ItemsResult r = await Session.Client.Get<ItemsResult>(Q($"/Shows/{ep.SeriesId}/Episodes",
                ("userId", Session.UserId), ("AdjacentTo", ep.Id), ("Fields", "Overview")));
            Item[] l = r?.Items ?? new Item[0];
            Item prev = null, next = null;
            for (int i = 0; i < l.Length; i++)
            {
                if (l[i].Id != ep.Id) continue;
                if (i > 0 && l[i - 1].LocationType != "Virtual") prev = l[i - 1];
                if (i + 1 < l.Length && l[i + 1].LocationType != "Virtual") next = l[i + 1];
            }
            return (prev, next);
        }

        // ---- Vu et favori ----

        public static Task<UserItemData> SetPlayed(string id, bool played)
        {
            string path = Q($"/UserPlayedItems/{id}", ("userId", Session.UserId));
            return played ? Session.Client.Post<UserItemData>(path) : Session.Client.Delete<UserItemData>(path);
        }

        // « Retirer de Reprendre la lecture » : position remise à zéro (useClearResume du web).
        public static Task<UserItemData> ClearResume(string id) =>
            Session.Client.Post<UserItemData>(Q($"/UserItems/{id}/UserData", ("userId", Session.UserId)),
                new ResumeReset { PlaybackPositionTicks = 0 });

        // « Marquer comme vu jusqu'ici » (useSetPlayedUpTo du web) : l'épisode et ceux diffusés
        // avant lui. Par le serveur s'il le sait (fonction « playedUpTo »), sinon comme le web :
        // une saison antérieure dont rien n'est vu part en une fois, le reste épisode par
        // épisode, 4 envois à la fois.
        public static async Task SetPlayedUpTo(Item ep)
        {
            if (((await Features()).PlayedUpTo ?? 0) > 0)
            {
                await Session.Client.Post<UserItemData>($"/AIOStreams/PlayedUpTo/{ep.Id}");
                return;
            }
            ItemsResult r = await Session.Client.Get<ItemsResult>(Q($"/Shows/{ep.SeriesId}/Episodes", ("userId", Session.UserId)));
            Item[] all = r?.Items ?? new Item[0];
            int ts = ep.ParentIndexNumber ?? 0, te = ep.IndexNumber ?? 0;
            bool UpTo(Item e)
            {
                if (e.ParentIndexNumber == null || e.IndexNumber == null) return false;
                int s = e.ParentIndexNumber.Value;
                return s == ts ? e.IndexNumber.Value <= te : ts > 0 && s > 0 && s < ts;
            }
            bool Aired(Item e) { DateTime? d = Date(e.PremiereDate); return d == null || d.Value <= DateTime.UtcNow; }
            var bySeason = new Dictionary<string, List<Item>>();
            var order = new List<string>();
            foreach (Item e in all)
            {
                string key = e.SeasonId ?? "";
                if (!bySeason.TryGetValue(key, out List<Item> l)) { bySeason[key] = l = new List<Item>(); order.Add(key); }
                l.Add(e);
            }
            var ids = new Queue<string>();
            foreach (string seasonId in order)
            {
                List<Item> eps = bySeason[seasonId];
                List<Item> wanted = eps.FindAll(e => UpTo(e) && Aired(e) && e.UserData?.Played != true);
                if (wanted.Count == 0) continue;
                bool whole = seasonId != "" && eps[0].ParentIndexNumber != ts && wanted.Count == eps.Count;
                if (whole) ids.Enqueue(seasonId);
                else foreach (Item e in wanted) ids.Enqueue(e.Id);
            }
            Probe.Log("menu", $"vu jusqu'ici : {ids.Count} envois");
            async Task Worker()
            {
                while (true)
                {
                    string id;
                    lock (ids) { if (ids.Count == 0) return; id = ids.Dequeue(); }
                    await SetPlayed(id, true);
                }
            }
            await Task.WhenAll(Worker(), Worker(), Worker(), Worker());
        }

        // Note personnelle de 0 à 10 (useSetRating du web) ; null l'efface.
        public static Task<UserItemData> SetRating(string id, double? rating) =>
            rating == null
                ? Session.Client.Delete<UserItemData>(Q($"/UserItems/{id}/Rating", ("userId", Session.UserId)))
                : Session.Client.Post<UserItemData>(Q($"/UserItems/{id}/UserData", ("userId", Session.UserId)), new RatingBody { Rating = rating.Value });

        public static Task<UserItemData> SetFavorite(string id, bool favorite)
        {
            string path = Q($"/UserFavoriteItems/{id}", ("userId", Session.UserId));
            return favorite ? Session.Client.Post<UserItemData>(path) : Session.Client.Delete<UserItemData>(path);
        }

        // ---- Images ----

        // Adresse d'une image, réduite par le serveur à la largeur d'affichage.
        public static string Image(string itemId, string type, string tag, int maxWidth)
        {
            if (itemId == null || tag == null) return null;
            string path = type == "Backdrop" ? $"/Items/{itemId}/Images/Backdrop/0" : $"/Items/{itemId}/Images/{type}";
            return Q(Session.Base + path, ("tag", tag), ("maxWidth", maxWidth > 0 ? (object)maxWidth : null), ("quality", 90));
        }

        // Affiche en portrait ; un épisode prend celle de sa série.
        public static string Poster(Item it, int w)
        {
            if (it.Type == "Episode")
                return Image(it.SeriesId, "Primary", it.SeriesPrimaryImageTag, w) ?? Image(it.Id, "Primary", it.ImageTags?.Primary, w);
            return Image(it.Id, "Primary", it.ImageTags?.Primary, w);
        }

        public static string Backdrop(Item it, int w) =>
            Image(it.Id, "Backdrop", First(it.BackdropImageTags), w)
            ?? Image(it.ParentBackdropItemId, "Backdrop", First(it.ParentBackdropImageTags), w);

        // Image en largeur, la meilleure disponible.
        // Toutes les images en largeur possibles, la meilleure d'abord (landscapeUrls du
        // web) : si la première ne se télécharge pas, la carte essaie la suivante.
        public static List<string> LandscapeCandidates(Item it, int w)
        {
            var l = new List<string>();
            void Add(string u) { if (u != null && !l.Contains(u)) l.Add(u); }
            if (it.Type == "Episode")
            {
                Add(Image(it.Id, "Primary", it.ImageTags?.Primary, w));
                Add(Image(it.ParentThumbItemId, "Thumb", it.ParentThumbImageTag, w));
                Add(Backdrop(it, w));
            }
            else
            {
                Add(Image(it.Id, "Thumb", it.ImageTags?.Thumb, w));
                Add(Backdrop(it, w));
                Add(Image(it.Id, "Primary", it.ImageTags?.Primary, w));
            }
            return l;
        }

        public static string Landscape(Item it, int w)
        {
            if (it.Type == "Episode")
                return Image(it.Id, "Primary", it.ImageTags?.Primary, w)
                    ?? Image(it.ParentThumbItemId, "Thumb", it.ParentThumbImageTag, w)
                    ?? Backdrop(it, w);
            return Image(it.Id, "Thumb", it.ImageTags?.Thumb, w)
                ?? Backdrop(it, w)
                ?? Image(it.Id, "Primary", it.ImageTags?.Primary, w);
        }

        public static string Logo(Item it, int w) =>
            Image(it.Id, "Logo", it.ImageTags?.Logo, w) ?? Image(it.ParentLogoItemId, "Logo", it.ParentLogoImageTag, w);

        static string First(string[] a) => a != null && a.Length > 0 ? a[0] : null;

        // ---- Textes d'une carte ----

        // Le nom d'une série pour un épisode, sinon le nom du titre.
        public static string Title(Item it) => (it.Type == "Episode" ? it.SeriesName : null) ?? it.Name ?? "";

        // Avancement en pourcentage, ou 0.
        public static double Progress(Item it)
        {
            double? pct = it.UserData?.PlayedPercentage;
            if (pct != null && pct > 0) return Math.Min(100, pct.Value);
            long pos = it.UserData?.PlaybackPositionTicks ?? 0;
            if (pos > 0 && it.RunTimeTicks > 0) return Math.Min(100, pos * 100.0 / it.RunTimeTicks.Value);
            return 0;
        }

        // Durée lisible : « 45 min », « 1 h 52 ».
        public static string Duration(long ticks)
        {
            long minutes = (long)Math.Round(ticks / 600000000.0);
            if (minutes <= 0) return null;
            if (minutes < 60) return Lang.T("time.minutes", minutes);
            long h = minutes / 60, m = minutes % 60;
            return m == 0 ? Lang.T("time.hours", h) : Lang.T("time.hours_minutes", h, m.ToString("00"));
        }

        // Ligne d'informations d'une carte ouverte : épisode, genres, année,
        // durée (ou temps restant), âge conseillé, vu.
        public static string Meta(Item it)
        {
            var parts = new List<string>();
            if (it.Type == "Episode")
            {
                string code = it.ParentIndexNumber != null && it.IndexNumber != null
                    ? Lang.T("episode.code", it.ParentIndexNumber, it.IndexNumber) : null;
                if (code != null) parts.Add(code);
                if (!string.IsNullOrEmpty(it.Name)) parts.Add(it.Name);
            }
            else
            {
                if (it.Genres != null)
                    for (int i = 0; i < it.Genres.Length && i < 2; i++) parts.Add(it.Genres[i]);
                if (it.ProductionYear != null) parts.Add(it.ProductionYear.Value.ToString(CultureInfo.InvariantCulture));
            }
            // Note du public juste après le titre ou l'année (demande de l'auteur : visible depuis l'accueil).
            string score = Score(it);
            if (score != null) parts.Add(score);
            if (it.Type != "Series" && it.RunTimeTicks > 0)
            {
                long pos = it.UserData?.PlaybackPositionTicks ?? 0;
                string d = pos > 0 && pos < it.RunTimeTicks ? Duration(it.RunTimeTicks.Value - pos) : Duration(it.RunTimeTicks.Value);
                if (d != null) parts.Add(pos > 0 && pos < it.RunTimeTicks ? Lang.T("time.left", d) : d);
            }
            if (!string.IsNullOrEmpty(it.OfficialRating)) parts.Add(it.OfficialRating);
            if (it.UserData?.Played == true) parts.Add(Lang.T("item.watched"));
            return string.Join("  ·  ", parts);
        }
    }

    // Fiches du serveur (BaseItemDto de Jellyfin) : seuls les champs utilisés sont lus.
    // Les dates restent du texte : DataContractJsonSerializer ne lit pas le format ISO.

    [DataContract]
    public class ItemsResult
    {
        [DataMember] public Item[] Items;
        [DataMember] public int TotalRecordCount;
    }

    [DataContract]
    public class Item
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string Type;
        [DataMember] public string CollectionType;
        [DataMember] public string Overview;
        [DataMember] public string[] Genres;
        [DataMember] public int? ProductionYear;
        [DataMember] public long? RunTimeTicks;
        [DataMember] public string OfficialRating;
        [DataMember] public int? IndexNumber;
        [DataMember] public int? ParentIndexNumber;
        [DataMember] public string SeriesId;
        [DataMember] public string SeriesName;
        [DataMember] public string SeriesPrimaryImageTag;
        [DataMember] public ImageTags ImageTags;
        [DataMember] public string[] BackdropImageTags;
        [DataMember] public string ParentBackdropItemId;
        [DataMember] public string[] ParentBackdropImageTags;
        [DataMember] public string ParentThumbItemId;
        [DataMember] public string ParentThumbImageTag;
        [DataMember] public string ParentLogoItemId;
        [DataMember] public string ParentLogoImageTag;
        [DataMember] public UserItemData UserData;
        [DataMember(Name = "aiostreams")] public AioExtension Aio;
        // Page d'un titre.
        [DataMember] public string SeasonId;
        [DataMember] public string[] Taglines;
        [DataMember] public double? CommunityRating;
        [DataMember] public string Status;
        [DataMember] public string EndDate;
        [DataMember] public string PremiereDate;
        [DataMember] public string LocationType;
        [DataMember] public Person[] People;
        [DataMember] public NameOnly[] Studios;
        [DataMember] public string[] ProductionLocations;
        // Accueil : bandes-annonces de la carte ouverte.
        [DataMember] public MediaUrl[] RemoteTrailers;
        [DataMember] public ProviderIds ProviderIds; // demandés par la recherche et les titres similaires
        [DataMember] public string Path;
    }

    [DataContract]
    public class ProviderIds
    {
        [DataMember] public string Imdb;
        [DataMember] public string Tmdb;
        [DataMember] public string Tvdb;
    }

    [DataContract]
    public class Person
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string Role;
        [DataMember] public string Type;
        [DataMember] public string PrimaryImageTag;
    }

    [DataContract]
    public class NameOnly
    {
        [DataMember] public string Name;
    }

    // Bande-annonce d'un titre (RemoteTrailers) : lien YouTube le plus souvent.
    [DataContract]
    public class MediaUrl
    {
        [DataMember] public string Url;
        [DataMember] public string Name;
    }

    // Réponse de /Items/{id}/PlaybackInfo : les versions, et les messages des addons
    // (Type « Placeholder »). Les champs « aiostreams » sont ceux d'AIOStreams.
    [DataContract]
    public class PlaybackInfo
    {
        [DataMember] public Source[] MediaSources;
        [DataMember] public string PlaySessionId;
    }

    [DataContract]
    public class Source
    {
        public bool SubtitlesAsked; // sous-titres des addons déjà demandés pour cette version (lecteur)
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string Type;
        [DataMember(Name = "aiostreams")] public SourceExtension Aio;
        // Lecteur : adresse du fichier, conteneur, débit, pistes annoncées par le serveur.
        [DataMember] public string Path;
        [DataMember] public string Container;
        [DataMember] public long? Bitrate;
        [DataMember] public MediaStream[] MediaStreams;
        [DataMember] public int? DefaultAudioStreamIndex; // piste par défaut : « Langue originale »
    }

    // Une piste d'une version (vidéo, audio, sous-titres), telle que le serveur la décrit.
    [DataContract]
    public class MediaStream
    {
        [DataMember] public string Type;
        [DataMember] public int Index;
        [DataMember] public string Codec;
        [DataMember] public string Language;
        [DataMember] public string DisplayTitle;
        [DataMember] public string Title;
        [DataMember] public bool IsExternal;
        // Sous-titres du serveur : fichier à part, téléchargé et affiché par l'application.
        [DataMember] public string DeliveryMethod;
        [DataMember] public string DeliveryUrl;
        [DataMember] public bool IsForced;
        [DataMember] public bool IsHearingImpaired;
        [DataMember] public bool IsDefault;
        [DataMember] public int? Width;
        [DataMember] public int? Height;
        [DataMember] public int? BitRate;
        [DataMember] public string VideoRange;
        [DataMember] public string VideoRangeType;
        [DataMember] public string Profile;
    }

    [DataContract]
    public class SourceExtension
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "description")] public string Description;
        [DataMember(Name = "index")] public int? Index;
        [DataMember(Name = "id")] public string Id;
        // Message d'un addon : error, statistic, external ou info.
        [DataMember(Name = "type")] public string Type;
        // Même source d'un épisode à l'autre (behaviorHints.bingeGroup de Stremio).
        [DataMember(Name = "bingeGroup")] public string BingeGroup;
        // Service de débrid, et release : la même quel que soit l'addon qui donne le fichier
        // (serveur du 2026-11-11 et après) ; la reprise s'y rabat si l'identifiant a disparu.
        [DataMember(Name = "service")] public string Service;
        [DataMember(Name = "release")] public string Release;
    }

    // Réponse de /MediaSegments/{id}. Type : Intro, Recap, Outro, Preview, Commercial.
    [DataContract]
    public class SegmentsResult
    {
        [DataMember] public Segment[] Items;
    }

    [DataContract]
    public class Segment
    {
        [DataMember] public string Type;
        [DataMember] public long StartTicks;
        [DataMember] public long EndTicks;
    }

    [DataContract]
    public class PlaybackInfoBody
    {
        [DataMember] public string UserId;
        [DataMember] public DeviceProfile DeviceProfile;
        [DataMember(EmitDefaultValue = false)] public bool Fresh;
        [DataMember(EmitDefaultValue = false)] public bool Refresh;
        [DataMember(EmitDefaultValue = false)] public bool SkipSubtitles;
        [DataMember(EmitDefaultValue = false)] public string MediaSourceId;
    }

    [DataContract]
    public class DeviceProfile
    {
        [DataMember] public DirectPlayProfile[] DirectPlayProfiles;
        [DataMember] public SubtitleProfile[] SubtitleProfiles;
    }

    [DataContract] public class DirectPlayProfile { [DataMember] public string Type; }

    [DataContract]
    public class SubtitleProfile
    {
        [DataMember] public string Format;
        [DataMember] public string Method;
    }

    [DataContract]
    public class ImageTags
    {
        [DataMember] public string Primary;
        [DataMember] public string Thumb;
        [DataMember] public string Logo;
    }

    [DataContract]
    public class RatingBody
    {
        [DataMember] public double Rating;
    }

    [DataContract]
    public class ResumeReset
    {
        [DataMember] public long PlaybackPositionTicks;
    }

    [DataContract]
    public class UserItemData
    {
        [DataMember] public bool Played;
        [DataMember] public bool IsFavorite;
        [DataMember] public long? PlaybackPositionTicks;
        [DataMember] public double? PlayedPercentage;
        [DataMember] public string LastPlayedDate;
        [DataMember] public int? PlayCount;
        [DataMember] public double? Rating; // note personnelle, de 0 à 10
    }

    // Champs ajoutés par AIOStreams.
    [DataContract]
    public class AioExtension
    {
        [DataMember(Name = "genreRequired")] public bool? GenreRequired;
        [DataMember(Name = "showWatchedAt")] public string ShowWatchedAt;
    }
}
