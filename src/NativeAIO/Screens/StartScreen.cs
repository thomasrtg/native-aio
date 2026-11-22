// Premier écran au lancement (module 15) : fond des écrans de connexion (thème et halo du web),
// logo d'AIOStreams, « Native AIO » dessous et la barre de chargement du lecteur, tant que la
// connexion au serveur n'est pas établie.
// - pas de serveur gardé : écran Serveur ;
// - pas de connexion gardée : écran de connexion ;
// - sinon la connexion est vérifiée auprès du serveur, puis « Qui regarde ? »
//   s'il y a plusieurs utilisateurs, l'accueil sinon. Clé refusée : connexion.
//   Serveur injoignable : second essai automatique 1 s après ; s'il échoue aussi, carte du web
//   « Impossible de joindre le serveur » (Unreachable de pages/sign-in.tsx), « Réessayer » et
//   « Changer de serveur ».
// La traînée finit toujours son passage avant l'écran suivant, avec au moins deux passages en tout
// si la connexion réussit (l'auteur, 2026-11-20 : une connexion très rapide faisait juste clignoter l'écran).
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Screens
{
    class StartScreen : Screen
    {
        // Logo de 160 px, nom 30 pt dessous, barre de chargement (celle du lecteur) plus bas.
        const int LogoD = 160, LogoY = 360, NameY = 548, StreakY = 650;
        const int StreakW = 220, StreakPeriod = 2200, Tries = 2, RetryDelay = 1000;
        // EndMargin : la traînée est jouée par la TV, partie un peu après Play() ; un peu de marge
        // évite de couper la fin du passage.
        const int MinPasses = 2, EndMargin = 40;

        View splash;
        Streak streak;
        readonly Stopwatch clock = new Stopwatch();
        AuthPage page;
        AuthPage.Item retry;
        bool failed, running;

        protected override View Build()
        {
            View root = AuthPage.Backdrop();
            // Visible d'emblée, sans fondu ni chargement différé du logo : il prend le relais de l'image de
            // lancement identique (shared/res/splash.png), un fondu faisait clignoter le passage.
            splash = Splash(out streak);
            root.Add(splash);

            page = new AuthPage();
            page.AddTitle(() => Lang.T("start.failed"));
            page.AddHelp(() => Lang.T("start.failed_help", Session.Base ?? ""));
            retry = page.AddButton(() => Lang.T("start.retry"), () => { if (failed) Go(); });
            page.AddLinks((() => Lang.T("start.change_server"), () => { if (failed) Nav.Push(new ServerScreen()); }));
            page.Focus(retry);
            page.View.Hide();
            root.Add(page.View);
            return root;
        }

        // Logo, nom et barre de chargement, aussi montrés pendant la connexion par téléphone (PhoneScreen).
        public static View Splash(out Streak streak)
        {
            var splash = new View { Size = new Size(1920, 1080) };
            splash.Add(new ImageView
            {
                ResourceUrl = Tizen.Applications.Application.Current.DirectoryInfo.Resource + "images/aiostreams.png",
                Position = new Position((1920 - LogoD) / 2, LogoY),
                Size = new Size(LogoD, LogoD),
                DesiredWidth = LogoD,
                DesiredHeight = LogoD,
                SynchronosLoading = true, // nom mal orthographié dans l'API 8
            });
            splash.Add(new TextLabel
            {
                Text = App.DisplayName,
                Position = new Position(0, NameY),
                Size = new Size(1920, 50),
                PointSize = Theme.SizeTitle,
                TextColor = Color.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            streak = new Streak(StreakW, 2, StreakPeriod);
            streak.View.Position = new Position((1920 - StreakW) / 2, StreakY);
            splash.Add(streak.View);
            return splash;
        }

        public override void ApplyTexts() => page.ApplyTexts();

        public override void OnShow()
        {
            if (!failed) Go();
        }

        public override bool OnKey(string key) => failed && page.OnKey(key);

        async void Go()
        {
            if (running) return;
            Session.Load();
            Probe.Boot("session relue");
            if (Session.Base == null) { Nav.Reset(new ServerScreen()); return; }
            if (Session.Token == null) { Nav.Reset(new SignInScreen()); return; }

            running = true;
            failed = false;
            page.View.Hide();
            splash.Show();
            streak.Play();
            clock.Restart();

            for (int attempt = 1; attempt <= Tries; attempt++)
            {
                Task<UserDto> me = Session.Client.Get<UserDto>("/Users/Me");
                Task<PickableUser[]> users = Session.Users();
                try
                {
                    UserDto user = await me;
                    Probe.Boot($"serveur : connexion vérifiée (essai {attempt})");
                    // L'accueil de l'utilisateur en cours se prépare pendant la fin de la traînée
                    // (et « Qui regarde ? ») : voir HomeScreen.Prepare.
                    if (!Closed) HomeScreen.Prepare();
                    PickableUser[] list = await users;
                    Probe.Boot($"serveur : utilisateurs reçus (essai {attempt})");
                    if (Closed) { running = false; return; }
                    if (user?.Name != null) Session.SetUserName(user.Name);
                    await EndStreak(MinPasses);
                    Probe.Boot("fin de la traînée");
                    running = false;
                    if (Closed) return;
                    if (list != null && list.Length > 1 && Prefs.AskWho) Nav.Reset(new WhoScreen(list));
                    else Nav.Reset(new HomeScreen());
                    return;
                }
                catch (ApiError e) when (e.Status == 401)
                {
                    if (Closed) { running = false; return; }
                    Session.ClearAuth();
                    await EndStreak(1);
                    running = false;
                    if (Closed) return;
                    Nav.Reset(new SignInScreen());
                    return;
                }
                catch (System.Exception e)
                {
                    Probe.Log("app", $"connexion au serveur, essai {attempt}/{Tries} : échec ({e.GetType().Name} {e.Message})");
                    if (Closed) { running = false; return; }
                }
                if (attempt < Tries)
                {
                    await Task.Delay(RetryDelay);
                    if (Closed) { running = false; return; }
                }
            }

            await EndStreak(1);
            running = false;
            if (Closed) return;
            failed = true;
            splash.Hide();
            page.View.Show();
            page.ApplyTexts();
            page.Focus(retry);
            page.Appear();
        }

        // Attend la fin du passage en cours de la traînée, et au moins minPasses passages depuis son
        // départ, puis l'arrête.
        Task EndStreak(int minPasses) => EndStreak(streak, clock, minPasses);

        public static async Task EndStreak(Streak streak, Stopwatch clock, int minPasses)
        {
            long t = clock.ElapsedMilliseconds;
            long passes = Math.Max(minPasses, (t + StreakPeriod - 1) / StreakPeriod);
            int wait = (int)(passes * StreakPeriod - t) + EndMargin;
            if (wait > 0) await Task.Delay(wait);
            streak.Stop();
        }
    }
}
