// Dialogue avec le serveur AIOStreams, qui répond à la manière de Jellyfin.
// Repris de la version web (lib/client.ts) : en-tête « Authorization:
// MediaBrowser … » avec le nom du client, l'appareil et la clé d'accès.
// JSON lu et écrit avec DataContractJsonSerializer (aucune bibliothèque ajoutée).
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

using System.Threading.Tasks;
using Tizen.Applications;

namespace NativeAIO.Core
{
    // Échec d'une requête. Status vaut 0 quand le serveur n'a pas répondu.
    class ApiError : Exception
    {
        public int Status { get; }
        public ApiError(int status, string message) : base(message ?? "") { Status = status; }
    }

    class Api
    {
        const string ClientName = App.DisplayName, ClientVersion = "0.1.0", DeviceName = "Samsung TV";
        public static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Pour les requêtes qui font travailler les addons du serveur (liste des versions).
        public static readonly HttpClient Slow = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

        public string Base { get; }
        public string Token { get; }

        public Api(string b, string token = null) { Base = b; Token = token; }

        public Api WithToken(string token) => new Api(Base, token);

        string Authorization
        {
            get
            {
                string s = $"MediaBrowser Client=\"{ClientName}\", Device=\"{DeviceName}\", DeviceId=\"{DeviceId()}\", Version=\"{ClientVersion}\"";
                return Token == null ? s : s + $", Token=\"{Token}\"";
            }
        }

        public Task<T> Get<T>(string path) => Send<T>(HttpMethod.Get, path, null);
        public Task<T> Post<T>(string path, object body = null) => Send<T>(HttpMethod.Post, path, body);
        public Task<T> PostSlow<T>(string path, object body) => Send<T>(HttpMethod.Post, path, body, Slow);
        public Task<T> GetSlow<T>(string path) => Send<T>(HttpMethod.Get, path, null, Slow);
        public Task<T> Delete<T>(string path) => Send<T>(HttpMethod.Delete, path, null);

        // Réponse brute d'une requête authentifiée au serveur (chemin « /Users/… ») ; 10 Mo au plus.
        public Task<byte[]> GetBytes(string path) => Download(Base + path, true);

        // Fichier de sous-titres : comme la version web, adresse du serveur + chemin
        // donné par le serveur, téléchargé sans jeton (le serveur le sert sans).
        public Task<byte[]> GetFile(string path) => Download(Base + path, false);

        async Task<byte[]> Download(string url, bool auth)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (auth) req.Headers.TryAddWithoutValidation("Authorization", Authorization);
            HttpResponseMessage res;
            try { res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead); }
            catch (Exception) { throw new ApiError(0, null); }
            using (res)
            {
                if (!res.IsSuccessStatusCode) throw new ApiError((int)res.StatusCode, null);
                if (res.Content.Headers.ContentLength > 10 * 1024 * 1024) throw new ApiError(413, null);
                return await res.Content.ReadAsByteArrayAsync();
            }
        }

        // JSON déjà écrit (Core/JsonTree.cs), réponse ignorée.
        public async Task PostJson(string path, byte[] json)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Base + path);
            req.Headers.TryAddWithoutValidation("Authorization", Authorization);
            req.Content = new ByteArrayContent(json);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            HttpResponseMessage res;
            try { res = await Http.SendAsync(req); }
            catch (Exception) { throw new ApiError(0, null); }
            using (res)
                if (!res.IsSuccessStatusCode) throw new ApiError((int)res.StatusCode, Message(await res.Content.ReadAsByteArrayAsync()));
        }

        async Task<T> Send<T>(HttpMethod method, string path, object body, HttpClient client = null)
        {
            var req = new HttpRequestMessage(method, Base + path);
            req.Headers.TryAddWithoutValidation("Authorization", Authorization);
            if (body != null)
            {
                req.Content = new ByteArrayContent(Json.Write(body));
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            HttpResponseMessage res;
            try { res = await (client ?? Http).SendAsync(req); }
            catch (Exception) { throw new ApiError(0, null); }
            using (res)
            {
                byte[] data = await res.Content.ReadAsByteArrayAsync();
                if (!res.IsSuccessStatusCode) throw new ApiError((int)res.StatusCode, Message(data));
                if (data.Length == 0) return default;
                // Lu hors du fil d'interface : une longue liste ne bloque pas l'écran.
                return await Task.Run(() => Json.Read<T>(data));
            }
        }

        // Le message d'erreur que le serveur donne parfois (« Message »).
        static string Message(byte[] data)
        {
            try { return Json.Read<ErrorBody>(data)?.Message; }
            catch (Exception) { return null; }
        }

        // Identifiant de l'appareil, tiré au hasard une fois puis gardé.
        static string deviceId;
        static string DeviceId()
        {
            if (deviceId != null) return deviceId;
            try { if (Preference.Contains("device.id")) deviceId = Preference.Get<string>("device.id"); }
            catch (Exception) { }
            if (string.IsNullOrEmpty(deviceId))
            {
                deviceId = Guid.NewGuid().ToString("N");
                Store.Set("device.id", deviceId);
            }
            return deviceId;
        }
    }

    static class Json
    {
        // Un lecteur par type, créé une fois : sa création est coûteuse.
        static readonly ConcurrentDictionary<Type, DataContractJsonSerializer> serializers =
            new ConcurrentDictionary<Type, DataContractJsonSerializer>();

        static DataContractJsonSerializer For(Type t) =>
            serializers.GetOrAdd(t, x => new DataContractJsonSerializer(x));

        public static T Read<T>(byte[] data)
        {
            using (var ms = new MemoryStream(data))
                return (T)For(typeof(T)).ReadObject(ms);
        }

        public static byte[] Write(object o)
        {
            using (var ms = new MemoryStream())
            {
                For(o.GetType()).WriteObject(ms, o);
                return ms.ToArray();
            }
        }
    }

    // Fiches échangées avec le serveur : seuls les champs utilisés sont lus.

    [DataContract] public class ErrorBody { [DataMember] public string Message; }

    [DataContract]
    public class UserDto
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
    }

    [DataContract]
    public class AuthResult
    {
        [DataMember] public UserDto User;
        [DataMember] public string AccessToken;
    }

    [DataContract]
    public class QuickConnectResult
    {
        [DataMember] public bool Authenticated;
        [DataMember] public string Secret;
        [DataMember] public string Code;
    }

    // Un utilisateur de la configuration, dans la liste de /AIOStreams/Users.
    // Needs : null, « pin », « password » ou « password-pin ».
    [DataContract]
    public class PickableUser
    {
        [DataMember(Name = "user")] public UserDto User;
        [DataMember(Name = "avatar")] public string Avatar;
        [DataMember(Name = "needs")] public string Needs;
    }

    [DataContract]
    public class SignInBody
    {
        [DataMember] public string Username;
        [DataMember] public string Pw;
    }

    [DataContract]
    public class TokenBody
    {
        [DataMember] public string UserId;
    }

    [DataContract]
    public class SecretBody
    {
        [DataMember] public string Secret;
    }
}
