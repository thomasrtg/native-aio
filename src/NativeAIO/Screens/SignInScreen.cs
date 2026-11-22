// Connexion à la configuration AIOStreams, reprise de la version web (pages/sign-in.tsx,
// lib/session.tsx ; écran du module 15) : UUID ou alias et mot de passe, puis les liens
// « Connexion rapide » (si le serveur la propose), « Utiliser un téléphone » et « Changer de serveur ».
using System;
using System.Threading.Tasks;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;

namespace NativeAIO.Screens
{
    class SignInScreen : Screen
    {
        AuthPage page;
        TextField username, password;
        AuthPage.Item status, links;
        bool busy, shown;

        protected override View Build()
        {
            View root = AuthPage.Backdrop();
            page = new AuthPage();
            page.AddTitle(() => Lang.T("signin.title"));
            page.AddHelp(() => Lang.T("signin.help"));
            username = page.AddField(() => Lang.T("signin.username"), null, "", AuthPage.Kind.Text);
            password = page.AddField(() => Lang.T("signin.password"), null, "", AuthPage.Kind.Password);
            status = page.AddStatus();
            page.AddButton(() => Lang.T("signin.submit"), SignIn);
            links = page.AddLinks(
                (() => Lang.T("signin.quick_connect"), () => Nav.Push(new QuickConnectScreen())),
                (() => Lang.T("phone.link"), () => Nav.Push(new PhoneScreen())),
                (() => Lang.T("signin.change_server"), () => Nav.Push(new ServerScreen())));
            page.ShowLink(links, 0, false);
            page.Focus(username);
            root.Add(page.View);
            CheckQuickConnect();
            return root;
        }

        public override void ApplyTexts() => page.ApplyTexts();

        public override void OnShow()
        {
            if (!shown) { shown = true; page.Appear(); }
        }

        public override bool OnKey(string key) => page.OnKey(key);
        public override bool OnBack() => page.OnBack();
        public override void OnClose() => page.Stop();

        // « Connexion rapide » n'est proposée que si le serveur l'accepte.
        async void CheckQuickConnect()
        {
            bool enabled;
            try { enabled = await Session.Anonymous.Get<bool>("/QuickConnect/Enabled"); }
            catch (Exception) { enabled = false; }
            if (Closed || !enabled) return;
            page.ShowLink(links, 0, true);
        }

        async void SignIn()
        {
            if (busy) return;
            string name = username.Text.Trim();
            if (name.Length == 0) { page.SetStatus(status, () => Lang.T("signin.missing"), true); return; }
            busy = true;
            page.SetStatus(status, () => Lang.T("signin.busy"), false);
            try
            {
                Screen next = await Login(name, password.Text);
                if (!Closed) Nav.Reset(next);
            }
            catch (ApiError e)
            {
                if (Closed) return;
                if (e.Status == 401) page.SetStatus(status, () => Lang.T("signin.wrong"), true);
                else page.SetStatus(status, () => ErrorText(e), true);
            }
            catch (Exception)
            {
                if (!Closed) page.SetStatus(status, () => Lang.T("error.unknown"), true);
            }
            finally { busy = false; }
        }

        // Connexion commune à cet écran et au téléphone (PhoneScreen) : rend l'écran à ouvrir ensuite.
        // server : serveur trouvé par le téléphone, gardé seulement si la connexion réussit.
        public static async Task<Screen> Login(string name, string password, Server.Found server = null)
        {
            Api api = server != null ? new Api(server.Base) : Session.Anonymous;
            AuthResult auth = await api.Post<AuthResult>("/Users/AuthenticateByName",
                new SignInBody { Username = name, Pw = password });
            if (server != null) Session.SetServer(server.Base, server.Name);
            // Un nom qui désigne un utilisateur précis entre directement (comme la version web).
            bool named = string.Equals(name, auth.User?.Name, StringComparison.OrdinalIgnoreCase);
            PickableUser[] users = null;
            if (name.Contains("/") || named) Session.Adopt(auth);
            else users = await Session.Enter(auth);
            return users != null ? new WhoScreen(users) : (Screen)new HomeScreen();
        }

        // Texte d'une erreur du serveur, pour tous les écrans.
        public static string ErrorText(ApiError e)
        {
            if (e.Status == 0) return Lang.T("error.unreachable");
            return e.Message.Length > 0 ? Lang.T("error.server", e.Status, e.Message) : Lang.T("error.status", e.Status);
        }
    }
}
