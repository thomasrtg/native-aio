// Connexion rapide (Quick Connect), reprise de la version web (pages/sign-in.tsx, écran du
// module 15) : le serveur donne un code, à approuver depuis une application déjà connectée ou
// depuis la page de configuration. L'écran demande toutes les 3 s si le code est approuvé (comme
// le web), puis se connecte avec l'utilisateur qui l'a approuvé. Code en lettres à largeur fixe
// (Cousine, police fournie par l'application), barre de chargement pendant la demande.
using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Screens
{
    class QuickConnectScreen : Screen
    {
        const int PollMs = 3000, CodeH = 84, StreakW = 160;

        AuthPage page;
        TextLabel code;
        Streak streak;
        AuthPage.Item status, error, retry, links;
        Timer poll; // gardé dans un champ, sinon le ramasse-miettes l'arrête
        string secret;
        bool asking, ended, shown;

        protected override View Build()
        {
            View root = AuthPage.Backdrop();
            page = new AuthPage();
            page.AddTitle(() => Lang.T("quick.title"));
            page.AddHelp(() => Lang.T("quick.hint"));

            SubtitleLook.AddFonts();
            var box = new View { Size = new Size(AuthPage.Inner, CodeH) };
            code = new TextLabel
            {
                Size = new Size(AuthPage.Inner, CodeH),
                PointSize = Theme.SizeHuge,
                FontFamily = "Cousine",
                TextColor = Color.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            streak = new Streak(StreakW, 2, 2200);
            streak.View.Position = new Position((AuthPage.Inner - StreakW) / 2, CodeH / 2);
            box.Add(code);
            box.Add(streak.View);
            page.AddCustom(box, AuthPage.Inner, CodeH);

            status = page.AddStatus();
            error = page.AddStatus();
            retry = page.AddButton(() => Lang.T("quick.new_code"), Start);
            page.Show(retry, false);
            links = page.AddLinks((() => Lang.T("auth.back"), () => Nav.Back()));
            page.Focus(links);
            root.Add(page.View);

            poll = new Timer(PollMs);
            poll.Tick += (s, e) => { Ask(); return false; };
            Start();
            return root;
        }

        public override void ApplyTexts() => page.ApplyTexts();

        public override void OnShow()
        {
            if (!shown) { shown = true; page.Appear(); }
        }

        public override bool OnKey(string key) => page.OnKey(key);

        public override void OnClose() => poll.Stop();

        async void Start()
        {
            if (asking) return;
            ended = false;
            secret = null;
            page.Show(retry, false);
            page.Focus(links);
            page.SetStatus(status, null, false);
            page.SetStatus(error, null, true);
            code.Text = "";
            streak.Play();
            asking = true;
            try
            {
                QuickConnectResult r = await Session.Anonymous.Post<QuickConnectResult>("/QuickConnect/Initiate");
                if (Closed) return;
                secret = r.Secret;
                streak.Stop();
                code.Text = r.Code;
                page.SetStatus(status, () => Lang.T("quick.waiting"), false);
                poll.Start();
            }
            catch (Exception e)
            {
                if (Closed) return;
                streak.Stop();
                string why = e is ApiError a ? SignInScreen.ErrorText(a) : Lang.T("error.unknown");
                Fail(() => Lang.T("quick.unavailable", why));
            }
            finally { asking = false; }
        }

        async void Ask()
        {
            if (Closed || ended || secret == null) return;
            QuickConnectResult r;
            try { r = await Session.Anonymous.Get<QuickConnectResult>("/QuickConnect/Connect?secret=" + Uri.EscapeDataString(secret)); }
            catch (Exception)
            {
                // Le serveur oublie un code au bout d'un moment : la question échoue.
                if (!Closed) { page.SetStatus(status, () => Lang.T("quick.expired"), false); Fail(null); }
                return;
            }
            if (Closed || ended) return;
            if (!r.Authenticated) { poll.Start(); return; }

            ended = true;
            page.SetStatus(status, () => Lang.T("quick.approved"), false);
            try
            {
                AuthResult auth = await Session.Anonymous.Post<AuthResult>("/Users/AuthenticateWithQuickConnect",
                    new SecretBody { Secret = secret });
                if (Closed) return;
                Session.Adopt(auth);
                Nav.Reset(new HomeScreen());
            }
            catch (Exception e)
            {
                if (Closed) return;
                string why = e is ApiError a ? SignInScreen.ErrorText(a) : Lang.T("error.unknown");
                page.SetStatus(status, null, false);
                Fail(() => why);
            }
        }

        // Échec ou code expiré : message éventuel en rouge, bouton « Nouveau code » regardé.
        void Fail(Func<string> why)
        {
            ended = true;
            poll.Stop();
            if (why != null) page.SetStatus(error, why, true);
            page.Show(retry, true);
            page.Focus(retry);
        }
    }
}
