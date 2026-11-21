// Recherche du serveur à partir de l'adresse saisie. Même règle que la
// version web d'AIOStreams (lib/servers.ts, findServer) : sans http:// ou
// https://, on essaie http:// ; une adresse sans chemin peut être AIOStreams,
// dont l'API est sous /jellyfin ; un lien de manifeste Stremio est accepté.
// Le serveur est reconnu s'il répond à /System/Info/Public avec un Id.
using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NativeAIO.Core
{
    // Échec de la recherche ; Key est la clé du texte à afficher.
    class ServerError : Exception
    {
        public string Key { get; }
        public ServerError(string key) : base(key) { Key = key; }
    }

    [DataContract]
    public class ServerPublicInfo
    {
        [DataMember] public string Id;
        [DataMember] public string ServerName;
    }

    // Même réponse, lue seulement par À propos (Paramètres) : un champ inattendu n'empêche
    // pas de reconnaître le serveur.
    [DataContract]
    public class ServerAbout
    {
        [DataMember] public string ServerName;
        [DataMember] public string Version;               // API Jellyfin
        [DataMember(Name = "aiostreams")] public AioInfo Aio;

        // Version d'AIOStreams comme le web (server-info.ts, buildVersion) : la balise,
        // ou « nightly <commit> » / « dev <commit> » ; null si inconnue.
        public string AioVersion()
        {
            AioBuild b = Aio?.Version;
            string Known(string v) => !string.IsNullOrEmpty(v) && v != "unknown" ? v : null;
            if (b == null) return null;
            if (b.Channel == "nightly" || b.Channel == "dev") return Known(b.Commit) != null ? b.Channel + " " + b.Commit : b.Channel;
            return Known(b.Tag);
        }
    }

    [DataContract]
    public class AioInfo
    {
        [DataMember(Name = "version")] public AioBuild Version;
        [DataMember(Name = "features")] public AioFeatures Features;
    }

    // Fonctions annoncées par le serveur, avec leur version (0 ou absente : non prise en charge).
    [DataContract]
    public class AioFeatures
    {
        [DataMember(Name = "refreshVersions")] public int? RefreshVersions;
        // Identifiant d'une version stable d'une demande à l'autre : la dernière version lue
        // peut être retrouvée à la reprise (LastVersions).
        [DataMember(Name = "versions")] public int? Versions;
        [DataMember(Name = "playedUpTo")] public int? PlayedUpTo;
    }

    [DataContract]
    public class AioBuild
    {
        [DataMember(Name = "tag")] public string Tag;
        [DataMember(Name = "channel")] public string Channel;
        [DataMember(Name = "commit")] public string Commit;
    }

    static class Server
    {
        public class Found
        {
            public string Base; // adresse de l'API, sans « / » final
            public string Name;
        }

        const RegexOptions I = RegexOptions.IgnoreCase;
        static readonly HttpClient http = Api.Http;

        public static async Task<Found> Find(string input)
        {
            string raw = Regex.Replace((input ?? "").Trim(), "^stremio://", "https://", I);
            if (!Regex.IsMatch(raw, @"^[a-z][a-z\d+.-]*://", I)) raw = "http://" + raw;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri url) || url.Host.Length == 0)
                throw new ServerError("server.invalid");
            if (url.Scheme != "http" && url.Scheme != "https")
                throw new ServerError("server.scheme");

            string path = url.AbsolutePath.TrimEnd('/');
            path = Regex.Replace(path, @"/web(/index\.html)?$", "", I);
            path = Regex.Replace(path, @"/stremio(/[^/]+/[^/]+)(?:/v/[^/]+)?/manifest\.json$", "/jellyfin$1", I);
            string origin = url.GetLeftPart(UriPartial.Authority);

            foreach (string candidate in path.Length > 0 ? new[] { path } : new[] { "/jellyfin", "" })
            {
                string b = origin + candidate;
                ServerPublicInfo info = await PublicInfo(b);
                if (info != null && !string.IsNullOrEmpty(info.Id))
                    return new Found { Base = b, Name = string.IsNullOrEmpty(info.ServerName) ? url.Host : info.ServerName };
            }
            throw new ServerError("server.none");
        }

        // Null si rien ne répond en 10 s, ou si la réponse n'est pas le JSON attendu.
        static async Task<ServerPublicInfo> PublicInfo(string b)
        {
            try
            {
                using (HttpResponseMessage res = await http.GetAsync(b + "/System/Info/Public"))
                {
                    if (!res.IsSuccessStatusCode) return null;
                    byte[] body = await res.Content.ReadAsByteArrayAsync();
                    using (var ms = new MemoryStream(body))
                        return (ServerPublicInfo)new DataContractJsonSerializer(typeof(ServerPublicInfo)).ReadObject(ms);
                }
            }
            catch (Exception) { return null; }
        }
    }
}
