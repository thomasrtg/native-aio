// Connexion avec un téléphone (2026-11-22, demande de l'auteur) : QR code vers une page servie par
// la TV (Core/PhoneLink.cs, res/phone.html) qui reprend les deux cartes de la TV, « Ajouter un
// serveur » puis « Se connecter », avec la connexion rapide si le serveur la propose (demande de
// l'auteur, 2026-11-22). La TV fait elle-même la recherche du serveur et la connexion,
// avec le code des écrans à la télécommande ; elle montre où en est le téléphone.
// Le serveur local s'arrête une fois connecté, en quittant l'écran ou après 10 min sans demande
// (« Nouveau code » en rouvre un, avec un autre code secret).
// Connexion en cours (identifiants reçus ou connexion rapide approuvée) : la carte laisse la place
// à l'écran de chargement du démarrage (demande de l'auteur, 2026-11-22), jusqu'à l'écran suivant ;
// en cas d'échec, la carte revient avec l'erreur.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Screens
{
    class PhoneScreen : Screen
    {
        const int QrMax = 300, LifeMs = 10 * 60 * 1000, QuickMs = 3000, MinPasses = 2;

        AuthPage page;
        View qrBox;
        AuthPage.Item address, status, error, retry, links;
        Timer life; // gardé dans un champ, sinon le ramasse-miettes l'arrête
        PhoneLink link;
        string goodPage, badPage;
        Server.Found found;
        string quickSecret, quickError; // connexion rapide demandée par le téléphone
        Timer quickPoll;
        bool quickDone, loading, leaving;
        View splash;
        Streak streak;
        Animation fade; // gardée dans un champ
        readonly Stopwatch clock = new Stopwatch();
        bool starting, busy, shown;

        protected override View Build()
        {
            View root = AuthPage.Backdrop();
            page = new AuthPage();
            page.AddTitle(() => Lang.T("phone.title"));
            page.AddHelp(() => Lang.T("phone.help"));
            qrBox = new View { Size = new Size(AuthPage.Inner, QrMax) };
            page.AddCustom(qrBox, AuthPage.Inner, QrMax);
            address = page.AddHelp(() => link == null ? "" : Lang.T("phone.address", link.Url));
            address.GapBefore = -1;
            status = page.AddStatus();
            error = page.AddStatus();
            retry = page.AddButton(() => Lang.T("quick.new_code"), Start);
            page.Show(retry, false);
            links = page.AddLinks((() => Lang.T("auth.back"), () => Nav.Back()));
            page.Focus(links);
            root.Add(page.View);
            splash = StartScreen.Splash(out streak);
            splash.Hide();
            root.Add(splash);

            life = new Timer(LifeMs);
            life.Tick += (s, e) => { Expire(); return false; };
            quickPoll = new Timer(QuickMs);
            quickPoll.Tick += (s, e) => { CheckQuick(); return false; };
            return root;
        }

        public override void ApplyTexts() => page.ApplyTexts();

        public override void OnShow()
        {
            if (shown) return;
            shown = true;
            page.Appear();
            Start();
        }

        // Pendant la connexion, les touches sont ignorées (Retour compris).
        public override bool OnKey(string key) => loading || page.OnKey(key);
        public override bool OnBack() => loading;

        public override void OnClose()
        {
            life.Stop();
            quickPoll.Stop();
            streak.Stop();
            link?.Stop();
            link = null;
        }

        async void Start()
        {
            if (starting) return;
            starting = true;
            link?.Stop();
            link = null;
            found = null;
            StopQuick();
            busy = false;
            ClearQr();
            page.Show(retry, false);
            page.Focus(links);
            page.SetStatus(error, null, true);
            page.SetStatus(status, null, false);
            try
            {
                MakePages();
                link = PhoneLink.Start(OnPhone, good => good ? goodPage : badPage);
                string url = link.Url;
                (string file, int side) = await Task.Run(() => QrImage.Write(url, QrMax, "phone"));
                if (Closed) return;
                // Fichier local à sa taille : sans DesiredWidth/Height (LECONS.md) ; arrondi au décodage.
                qrBox.Add(new ImageView
                {
                    ResourceUrl = file,
                    Size = new Size(side, side),
                    Position = new Position((AuthPage.Inner - side) / 2, (QrMax - side) / 2),
                }.Rounded(12));
                page.Show(address, true);
                page.ApplyTexts();
                page.SetStatus(status, () => Lang.T("phone.waiting"), false);
                life.Start();
            }
            catch (PhoneLinkError e)
            {
                if (Closed) return;
                string why = e.Key == "phone.failed" ? Lang.T("phone.failed", e.Message) : Lang.T(e.Key);
                Fail(() => why);
            }
            catch (Exception e)
            {
                if (Closed) return;
                Probe.Log("phone", "échec : " + e.GetType().Name + " " + e.Message);
                Fail(() => Lang.T("phone.failed", e.GetType().Name));
            }
            finally { starting = false; }
        }

        // Plus de demande du téléphone depuis 10 min : serveur arrêté, « Nouveau code » proposé.
        void Expire()
        {
            if (Closed || link == null) return;
            link.Stop();
            link = null;
            page.SetStatus(status, () => Lang.T("quick.expired"), false);
            Fail(null);
        }

        void Fail(Func<string> why)
        {
            StopQuick();
            link?.Stop();
            link = null;
            life.Stop();
            ClearQr();
            page.Show(address, false);
            if (why != null) page.SetStatus(error, why, true);
            page.Show(retry, true);
            page.Focus(retry);
        }

        void ClearQr()
        {
            while (qrBox.ChildCount > 0)
            {
                View v = qrBox.GetChildAt(0);
                qrBox.Remove(v);
                v.Dispose();
            }
        }

        // Demandes du téléphone, sur le fil d'interface : « hello » (page ouverte), « server »
        // (adresse), « signin » (identifiant et mot de passe). Réponse { ok } ou { error }.
        async Task<PhoneLink.Reply> OnPhone(string step, Dictionary<string, string> form)
        {
            if (Closed || link == null) return Reply(PhoneLink.Json.Error(Lang.T("phone.invalid")));
            if ((loading || leaving) && step != "quickcheck") return Reply(PhoneLink.Json.Error(Lang.T("phone.busy")));
            life.Stop();
            life.Start();
            switch (step)
            {
                case "hello":
                    if (!busy && found == null) page.SetStatus(status, () => Lang.T("phone.opened"), false);
                    return Reply(PhoneLink.Json.Ok());
                case "server":
                    return await OnServer(Field(form, "address"));
                case "signin":
                    return await OnSignIn(Field(form, "username"), Field(form, "password"));
                case "quick":
                    return await OnQuick();
                case "quickcheck":
                    return await OnQuickCheck();
            }
            return Reply(PhoneLink.Json.Error(Lang.T("error.unknown")));
        }

        async Task<PhoneLink.Reply> OnServer(string input)
        {
            if (busy) return Reply(PhoneLink.Json.Error(Lang.T("phone.busy")));
            busy = true;
            found = null;
            StopQuick();
            page.SetStatus(error, null, true);
            page.SetStatus(status, () => Lang.T("server.checking"), false);
            try
            {
                Server.Found f = await Server.Find(input);
                if (Closed) return Reply(PhoneLink.Json.Error(Lang.T("phone.invalid")));
                // « Connexion rapide » n'est proposée que si le serveur l'accepte (comme SignInScreen).
                bool quick;
                try { quick = await new Api(f.Base).Get<bool>("/QuickConnect/Enabled"); }
                catch (Exception) { quick = false; }
                if (Closed) return Reply(PhoneLink.Json.Error(Lang.T("phone.invalid")));
                found = f;
                Store.Set(ServerScreen.PrefInput, input.Trim());
                page.SetStatus(status, () => Lang.T("phone.server_found", f.Name), false);
                return Reply("{\"ok\":true,\"quick\":" + (quick ? "true" : "false") + "}");
            }
            catch (ServerError e) { return Refuse(Lang.T(e.Key)); }
            catch (Exception) { return Refuse(Lang.T("server.none")); }
            finally { busy = false; }
        }

        async Task<PhoneLink.Reply> OnSignIn(string name, string password)
        {
            if (busy) return Reply(PhoneLink.Json.Error(Lang.T("phone.busy")));
            if (found == null) return Reply(PhoneLink.Json.Error(Lang.T("server.none")));
            name = name.Trim();
            if (name.Length == 0) return Reply(PhoneLink.Json.Error(Lang.T("signin.missing")));
            busy = true;
            page.SetStatus(error, null, true);
            page.SetStatus(status, () => Lang.T("signin.busy"), false);
            ShowLoading();
            try
            {
                Screen next = await SignInScreen.Login(name, password, found);
                if (Closed) return Reply(PhoneLink.Json.Error(Lang.T("phone.invalid")));
                // Réponse envoyée au téléphone d'abord, puis l'écran suivant (qui arrête le serveur).
                return new PhoneLink.Reply { Json = PhoneLink.Json.Ok(), After = () => Finish(next) };
            }
            catch (ApiError e) { HideLoading(); return Refuse(e.Status == 401 ? Lang.T("signin.wrong") : SignInScreen.ErrorText(e)); }
            catch (Exception) { HideLoading(); return Refuse(Lang.T("error.unknown")); }
            finally { busy = false; }
        }

        // Connexion rapide demandée depuis le téléphone : la TV demande un code au serveur trouvé
        // et le renvoie au téléphone, qui l'affiche comme QuickConnectScreen. C'est la TV qui demande
        // ensuite toutes les 3 s si le code est approuvé : la page du téléphone est mise en pause par
        // son navigateur quand on va approuver le code dans un autre onglet ou une autre application.
        async Task<PhoneLink.Reply> OnQuick()
        {
            if (busy) return Reply(PhoneLink.Json.Error(Lang.T("phone.busy")));
            if (found == null) return Reply(PhoneLink.Json.Error(Lang.T("server.none")));
            busy = true;
            StopQuick();
            page.SetStatus(error, null, true);
            try
            {
                QuickConnectResult r = await new Api(found.Base).Post<QuickConnectResult>("/QuickConnect/Initiate");
                if (Closed) return Reply(PhoneLink.Json.Error(Lang.T("phone.invalid")));
                quickSecret = r.Secret;
                string code = r.Code;
                page.SetStatus(status, () => Lang.T("phone.quick", code), false);
                quickPoll.Start();
                return Reply("{\"ok\":true,\"code\":" + PhoneLink.Json.Str(code) + "}");
            }
            catch (Exception e)
            {
                string why = e is ApiError a ? SignInScreen.ErrorText(a) : Lang.T("error.unknown");
                return Refuse(Lang.T("quick.unavailable", why));
            }
            finally { busy = false; }
        }

        void StopQuick()
        {
            quickPoll.Stop();
            quickSecret = null;
            quickError = null;
        }

        // La TV demande au serveur si le code est approuvé ; approuvé : connexion, puis l'accueil
        // (comme QuickConnectScreen), après avoir laissé au téléphone le temps de l'apprendre.
        async void CheckQuick()
        {
            if (Closed || quickSecret == null || quickDone) return;
            if (busy) { quickPoll.Start(); return; }
            busy = true;
            Server.Found server = found;
            string secret = quickSecret;
            var api = new Api(server.Base);
            try
            {
                QuickConnectResult r;
                try { r = await api.Get<QuickConnectResult>("/QuickConnect/Connect?secret=" + Uri.EscapeDataString(secret)); }
                catch (Exception)
                {
                    // Le serveur oublie un code au bout d'un moment : la question échoue.
                    if (!Closed && secret == quickSecret) QuickFailed(Lang.T("quick.expired"));
                    return;
                }
                if (Closed || secret != quickSecret) return;
                if (!r.Authenticated) { quickPoll.Start(); return; }

                page.SetStatus(status, () => Lang.T("quick.approved"), false);
                Probe.Log("phone", "connexion rapide approuvée");
                ShowLoading();
                AuthResult auth = await api.Post<AuthResult>("/Users/AuthenticateWithQuickConnect", new SecretBody { Secret = secret });
                if (Closed) return;
                quickSecret = null;
                quickDone = true;
                Session.SetServer(server.Base, server.Name);
                Session.Adopt(auth);
                // Le téléphone l'apprend à sa prochaine question, pendant la fin de la barre (≥ 4,4 s).
                Finish(new HomeScreen());
            }
            catch (Exception e)
            {
                if (Closed) return;
                HideLoading();
                QuickFailed(e is ApiError a ? SignInScreen.ErrorText(a) : Lang.T("error.unknown"));
            }
            finally { busy = false; }
        }

        void QuickFailed(string why)
        {
            quickPoll.Stop();
            quickSecret = null;
            quickError = why;
            page.SetStatus(status, () => Lang.T("phone.opened"), false);
            page.SetStatus(error, () => why, true);
        }

        // Question du téléphone toutes les 3 s : où en est la connexion rapide ? { ok, wait } tant
        // que le code n'est pas approuvé ou que la connexion est en cours ; connecté : { ok }.
        Task<PhoneLink.Reply> OnQuickCheck()
        {
            PhoneLink.Reply reply;
            if (quickDone) reply = Reply(PhoneLink.Json.Ok());
            else if (quickSecret != null) reply = Reply("{\"ok\":true,\"wait\":true}");
            else reply = Reply(PhoneLink.Json.Error(quickError ?? Lang.T("quick.expired")));
            return Task.FromResult(reply);
        }

        // Écran de chargement du démarrage à la place de la carte, en fondu de 0,3 s.
        void ShowLoading()
        {
            if (loading) return;
            loading = true;
            page.Stop();
            page.View.Hide();
            splash.Opacity = 0f;
            splash.Show();
            fade?.Stop();
            fade = new Animation(Theme.Fade);
            fade.AnimateTo(splash, "Opacity", 1f);
            fade.Play();
            streak.Play();
            clock.Restart();
        }

        // Connexion refusée : la carte revient (avec l'erreur, posée par l'appelant).
        void HideLoading()
        {
            if (!loading || leaving) return;
            loading = false;
            fade?.Stop();
            streak.Stop();
            splash.Hide();
            page.View.Show();
            page.Appear();
        }

        // Connecté : l'accueil se prépare pendant la fin de la barre (comme au démarrage), puis
        // l'écran suivant, qui ferme celui-ci et arrête le serveur.
        async void Finish(Screen next)
        {
            if (leaving || Closed) return;
            leaving = true;
            quickPoll.Stop();
            life.Stop();
            HomeScreen.Prepare();
            await StartScreen.EndStreak(streak, clock, MinPasses);
            if (!Closed) Nav.Reset(next);
        }

        // Erreur montrée sur la TV et renvoyée au téléphone.
        PhoneLink.Reply Refuse(string why)
        {
            if (!Closed)
            {
                page.SetStatus(status, () => Lang.T("phone.opened"), false);
                page.SetStatus(error, () => why, true);
            }
            return Reply(PhoneLink.Json.Error(why));
        }

        static PhoneLink.Reply Reply(string json) => new PhoneLink.Reply { Json = json };

        static string Field(Dictionary<string, string> form, string name) =>
            form.TryGetValue(name, out string v) ? v ?? "" : "";

        // ---- Page du téléphone : res/phone.html, avec les textes, la langue et le thème de la TV ----

        static readonly string[] PageTexts =
        {
            "server.title", "server.help", "server.label", "server.placeholder", "server.connect", "server.checking",
            "signin.title", "signin.help", "signin.username", "signin.password", "signin.submit",
            "signin.change_server", "signin.missing", "signin.busy",
            "phone.done_title", "phone.done_help", "phone.invalid_title", "phone.invalid", "phone.lost",
            "signin.quick_connect", "quick.title", "quick.hint", "quick.waiting", "quick.approved",
            "quick.expired", "quick.new_code", "auth.back", "phone.quick_gone",
        };

        static string template;

        void MakePages()
        {
            if (template == null)
                template = File.ReadAllText(Tizen.Applications.Application.Current.DirectoryInfo.Resource + "phone.html", Encoding.UTF8);
            goodPage = template.Replace("/*STATE*/null", State(true));
            badPage = template.Replace("/*STATE*/null", State(false));
        }

        static string State(bool good)
        {
            var b = new StringBuilder("{\"ok\":").Append(good ? "true" : "false");
            b.Append(",\"lang\":").Append(PhoneLink.Json.Str(Lang.Current));
            b.Append(",\"bg\":").Append(PhoneLink.Json.Str(Css(Theme.Background)));
            b.Append(",\"accent\":").Append(PhoneLink.Json.Str(Css(Theme.Accent)));
            // L'adresse gardée peut contenir l'identifiant chiffré : seulement avec le bon code.
            if (good) b.Append(",\"address\":").Append(PhoneLink.Json.Str(ServerScreen.Saved()));
            b.Append(",\"t\":{");
            for (int i = 0; i < PageTexts.Length; i++)
            {
                if (i > 0) b.Append(',');
                b.Append(PhoneLink.Json.Str(PageTexts[i])).Append(':').Append(PhoneLink.Json.Str(Lang.T(PageTexts[i])));
            }
            return b.Append("}}").ToString();
        }

        static string Css(Color c) =>
            "#" + ((int)Math.Round(c.R * 255)).ToString("X2") + ((int)Math.Round(c.G * 255)).ToString("X2") + ((int)Math.Round(c.B * 255)).ToString("X2");
    }
}
