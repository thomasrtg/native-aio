// Le serveur et l'utilisateur en cours, gardés d'un lancement à l'autre
// (Preference). La clé d'accès n'est jamais affichée.
// Après une connexion, même règle que la version web (lib/session.tsx) : une
// configuration à plusieurs utilisateurs fait choisir l'utilisateur ; on passe
// de l'un à l'autre avec /AIOStreams/Token, sans redemander le mot de passe.
using System;
using System.Threading.Tasks;
using Tizen.Applications;

namespace NativeAIO.Core
{
    static class Session
    {
        const string KBase = "server.base", KName = "server.name";
        const string KToken = "auth.token", KUser = "auth.user", KUserName = "auth.name";

        public static string Base { get; private set; }
        public static string ServerName { get; private set; }
        public static string Token { get; private set; }
        public static string UserId { get; private set; }
        public static string UserName { get; private set; }

        public static Api Client => new Api(Base, Token);
        public static Api Anonymous => new Api(Base);

        public static void Load()
        {
            Base = Read(KBase);
            ServerName = Read(KName);
            Token = Read(KToken);
            UserId = Read(KUser);
            UserName = Read(KUserName);
            Prefs.Use(Token != null ? UserId : null);
        }

        // Nouveau serveur : la connexion à l'ancien est oubliée.
        public static void SetServer(string b, string name)
        {
            if (b != Base) ClearAuth();
            Base = b;
            ServerName = name;
            Write(KBase, b);
            Write(KName, name);
        }

        public static void Adopt(AuthResult auth)
        {
            Token = auth.AccessToken;
            UserId = auth.User?.Id;
            UserName = auth.User?.Name;
            Write(KToken, Token);
            Write(KUser, UserId);
            Write(KUserName, UserName);
            Prefs.Use(UserId);
        }

        public static void SetUserName(string name)
        {
            UserName = name;
            Write(KUserName, name);
        }

        // Termine la clé sur le serveur (sans attendre sa réponse) et l'oublie ici.
        public static void SignOut()
        {
            if (Token != null)
                Client.Post<ErrorBody>("/Sessions/Logout").ContinueWith(t => { var _ = t.Exception; });
            ClearAuth();
        }

        public static void ClearAuth()
        {
            Token = UserId = UserName = null;
            Prefs.Use(null);
            foreach (string k in new[] { KToken, KUser, KUserName }) Store.Remove(k);
        }

        // Les utilisateurs qu'on peut choisir, ou null si le serveur n'en propose pas.
        public static async Task<PickableUser[]> Users()
        {
            try { return await Client.Get<PickableUser[]>("/AIOStreams/Users"); }
            catch (ApiError e) when (e.Status > 0) { return null; }
        }

        // Passe à un autre utilisateur de la configuration.
        public static async Task SwitchTo(string userId)
        {
            Adopt(await Client.Post<AuthResult>("/AIOStreams/Token", new TokenBody { UserId = userId }));
        }

        // Après une connexion par identifiant et mot de passe. Renvoie la liste
        // des utilisateurs à proposer, ou null si on peut aller directement à l'accueil.
        public static async Task<PickableUser[]> Enter(AuthResult signedIn)
        {
            Adopt(signedIn);
            PickableUser[] users = await Users();
            if (users == null || users.Length == 0) return null;
            if (users.Length > 1) return users;
            if (users[0].User?.Id != UserId) await SwitchTo(users[0].User.Id);
            return null;
        }

        static string Read(string key)
        {
            try { return Preference.Contains(key) ? Preference.Get<string>(key) : null; }
            catch (Exception) { return null; }
        }

        // Hors du fil d'interface (file commune, Store) : les valeurs restent en mémoire ici,
        // relues de la TV seulement au lancement (Load).
        static void Write(string key, string value)
        {
            if (value == null) Store.Remove(key);
            else Store.Set(key, value);
        }
    }
}
