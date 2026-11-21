// Écran « Ajouter un serveur », repris de la version web (pages/servers.tsx, module 15) : adresse
// saisie au clavier de la TV, puis vérification que le serveur répond. Serveur trouvé : il est
// gardé et l'écran de connexion le remplace. Native AIO ne garde qu'un serveur : pas de liste.
using System;
using Tizen.Applications;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;

namespace NativeAIO.Screens
{
    class ServerScreen : Screen
    {
        const string PrefInput = "server.input";

        AuthPage page;
        TextField field;
        AuthPage.Item status, links;
        bool checking, shown;

        protected override View Build()
        {
            View root = AuthPage.Backdrop();
            page = new AuthPage();
            page.AddTitle(() => Lang.T("server.title"));
            page.AddHelp(() => Lang.T("server.help"));
            field = page.AddField(() => Lang.T("server.label"), () => Lang.T("server.placeholder"), Saved(), AuthPage.Kind.Url);
            status = page.AddStatus();
            page.AddButton(() => Lang.T("server.connect"), Check);
            links = page.AddLinks((() => Lang.T("auth.back"), () => Nav.Back()));
            page.Focus(field);
            root.Add(page.View);
            return root;
        }

        public override void ApplyTexts() => page.ApplyTexts();

        public override void OnShow()
        {
            // « Retour » seulement s'il y a un écran dessous (ouvert depuis la connexion ou les Paramètres).
            page.ShowLink(links, 0, Nav.Depth > 1);
            if (!shown) { shown = true; page.Appear(); }
        }

        public override bool OnKey(string key) => page.OnKey(key);
        public override bool OnBack() => page.OnBack();
        public override void OnClose() => page.Stop();

        async void Check()
        {
            if (checking) return;
            checking = true;
            page.SetStatus(status, () => Lang.T("server.checking"), false);
            try
            {
                Server.Found found = await Server.Find(field.Text);
                if (Closed) return;
                Store.Set(PrefInput, field.Text.Trim());
                // Même serveur, déjà connecté (Paramètres, Changer de serveur) : on garde la connexion.
                bool same = found.Base == Session.Base && Session.Token != null;
                Session.SetServer(found.Base, found.Name);
                Nav.Reset(same ? (Screen)new StartScreen() : new SignInScreen());
            }
            catch (ServerError e) { if (!Closed) page.SetStatus(status, () => Lang.T(e.Key), true); }
            catch (Exception) { if (!Closed) page.SetStatus(status, () => Lang.T("server.none"), true); }
            finally { checking = false; }
        }

        static string Saved()
        {
            try { return Preference.Contains(PrefInput) ? Preference.Get<string>(PrefInput) : ""; }
            catch (Exception) { return ""; }
        }
    }
}
