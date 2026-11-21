// Point d'entrée de Native AIO : une seule fenêtre NUI, des écrans empilés par le
// Navigator, la touche Retour pour revenir en arrière (et quitter depuis le
// premier écran), la touche rouge pour afficher ou masquer la mesure.
using System;
using System.Collections.Generic;
using Tizen.Applications;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Screens;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO
{
    class App : NUIApplication
    {
        public const string DisplayName = "Native AIO"; // nom affiché (module 15)
        public const string AppVersion = "1.0.1"; // à garder égale à celle de tizen-manifest.xml
        public static new App Current { get; private set; }

        Navigator nav;
        Measure measure;
        Sidebar sidebar;

        protected override void OnCreate()
        {
            Probe.Boot("OnCreate");
            base.OnCreate();
            Current = this;
            Lang.Init();
            Probe.Boot("langue lue");
            Crash.Init(); // session précédente relue, nouvelle session ouverte (module 18)
            ImageCache.Init(); // dossier des images gardées sur la TV
            Probe.Boot("cache d'images prêt");
            Webp.Check(); // essai : bibliothèque WebP du système ? (panneau de mesures)
            Probe.Boot("essai WebP fait");

            Window win = Window.Instance;
            win.BackgroundColor = Background;

            // Arrière-plan d'après l'image (réglage du thème) : sous tous les écrans.
            ArtLayer.Init(win); // ajoutée avant les écrans : reste dessous
            var screens = new View { Size = new Size(1920, 1080) };
            win.Add(screens);
            nav = new Navigator(screens);
            nav.Changed += () => ArtLayer.Sync(nav.Top);
            nav.Changed += () => Crash.Screen(nav.Top);

            // Barre latérale (module 13) : au-dessus des écrans, montrée selon l'écran visible.
            sidebar = new Sidebar(nav);
            win.Add(sidebar.View);
            nav.Changed += () => sidebar.Sync(nav.Top);
            Probe.Boot("fond et barre latérale posés");

            // Thème changé (autre utilisateur, réglages relus sur le serveur) : fond de la
            // fenêtre et rond du profil repeints. Pas pendant une lecture : la fenêtre y est
            // transparente, le lecteur remet lui-même le fond en sortant.
            int themeSeen = Ui.Theme.Version;
            Prefs.Changed += () =>
            {
                if (Ui.Theme.Version == themeSeen) return;
                themeSeen = Ui.Theme.Version;
                if (win.BackgroundColor.A > 0) win.BackgroundColor = Background;
                sidebar.ApplyTheme();
            };

            measure = new Measure();
            win.Add(measure.View); // ajoutée après : reste au-dessus des écrans
            Probe.Boot("panneau de mesures posé");

            // Pas de cadre de focus du système (liseré bleu autour du champ en saisie) :
            // les écrans dessinent eux-mêmes leur focus.
            FocusManager.Instance.FocusIndicator = new View();

            win.KeyEvent += OnKey;
            Probe.Start(); // sonde de développement (sans effet si PROBE n'est pas défini)
            // Session précédente non fermée normalement : proposer de la signaler d'abord.
            if (Crash.Pending != null && Crash.Ask) nav.Push(new ReportScreen(Crash.Pending, true));
            else nav.Push(new StartScreen());
            Probe.Boot("fin de OnCreate (écran de démarrage posé)");
            Probe.BootFrame("première image de l'application");
        }

        // Arrière-plan et fermeture : transmis à l'écran visible (le lecteur se met en pause
        // et envoie sa position au serveur).
        protected override void OnPause()
        {
            base.OnPause();
            Probe.Log("app", "pause");
            Crash.Pause();
            pausedAt = DateTime.UtcNow;
            nav?.Top?.OnAppPause();
        }

        protected override void OnResume()
        {
            base.OnResume();
            Probe.Log("app", "resume");
            Crash.Resume();
            nav?.Top?.OnAppResume();
            if (pausedAt != null && DateTime.UtcNow - pausedAt.Value >= TimeSpan.FromMinutes(30)) AskWhoAgain();
            pausedAt = null;
        }

        DateTime? pausedAt;

        // Retour après une demi-heure (Paramètres, Compte, « Qui regarde ») : « Qui regarde ? »
        // par-dessus l'écran visible ; l'utilisateur actif continue sans son code PIN.
        // Pas pendant une lecture (lecteur, bande-annonce) ni avant d'être connecté.
        async void AskWhoAgain()
        {
            Screen top = nav?.Top;
            if (!Prefs.AskWho || Session.Token == null || top == null) return;
            if (top is PlayerScreen || top is TrailerScreen || top is WhoScreen || top is StartScreen
                || top is ServerScreen || top is SignInScreen || top is QuickConnectScreen) return;
            PickableUser[] list;
            try { list = await Accounts.Users(); }
            catch (Exception) { return; }
            if (list == null || list.Length < 2 || top.Closed || nav.Top != top) return;
            Probe.Log("app", "qui regarde au retour");
            nav.Push(new WhoScreen(list, true));
        }

        protected override void OnTerminate()
        {
            Probe.Log("app", "terminate");
            try { nav?.Top?.OnAppTerminate(); } catch (Exception) { }
            Store.Flush(1000); // dernières préférences écrites avant la fin du processus
            Crash.End();
            base.OnTerminate();
        }

        // Fond de la fenêtre : celui du thème choisi (charte, module 14) ; transparent pendant
        // la lecture, la vidéo étant dessous.
        public static Color Background => Ui.Theme.Background;

        // Affiche dans la mesure une touche reçue ailleurs que par la fenêtre (ex. un champ de saisie).
        public void NoteKey(string key)
        {
            Probe.Log("key", key + " (champ)");
            measure.KeyDown(key);
        }

        // ---- Sonde (Core/Probe.cs), appelée sur le fil d'interface ----

        // Touche simulée depuis le PC : même traitement qu'une touche de la télécommande.
        // « up:Return » : relâchement simulé (touche maintenue : « Return,wait:1500,up:Return »).
        public void ProbeKey(string key)
        {
            if (key.StartsWith("up:")) HandleKeyUp(key.Substring(3), " (sonde)");
            else HandleKey(key, " (sonde)");
        }

        // Relâchement : noté dans le journal (pour voir ce qu'envoie la télécommande quand on
        // garde le doigt sur une touche), puis transmis à l'écran visible hors barre latérale.
        void HandleKeyUp(string key, string origin)
        {
            Probe.Log("keyup", key + origin);
            if (sidebar.Focused) return;
            nav.Top?.OnKeyUp(key);
        }

        public string ProbeMetrics()
        {
            Screen top = nav.Top;
            return measure.Json(top?.GetType().Name, nav.Depth, sidebar.Focused);
        }

        // ---- Test de stabilité (module 17, tools/stress.mjs) ----

        // Page d'un titre ; un épisode ouvre sa série, sur sa saison et sur lui (comme une carte).
        public async System.Threading.Tasks.Task<string> ProbeOpen(string id)
        {
            Item it = await Library.Full(id);
            if (it == null) return "titre introuvable";
            nav.Push(ItemScreen.For(it));
            return "ok";
        }

        public string ProbeHome()
        {
            nav.Section(null);
            return "ok";
        }

        // Lecture lancée sans passer par la liste des liens, depuis le début : premier lien jouable
        // dont le débit annoncé ne dépasse pas max (bit/s), pour que le réseau ne coupe pas la
        // lecture. Rend un compte rendu court (jamais l'adresse du fichier).
        public async System.Threading.Tasks.Task<string> ProbePlay(string id, long max)
        {
            Item target = await Library.Full(id);
            if (target == null) return "titre introuvable";
            Item show = target.SeriesId != null ? await Library.Full(target.SeriesId) ?? target : target;
            PlaybackInfo info = await Library.Versions(id);
            Source[] all = info?.MediaSources ?? new Source[0];
            var playable = new List<Source>();
            int known = 0;
            Source pick = null;
            foreach (Source s in all)
            {
                if (s.Type == "Placeholder") continue;
                playable.Add(s);
                if (s.Bitrate == null) continue;
                known++;
                if (pick == null && s.Bitrate <= max) pick = s;
            }
            string report = $"{playable.Count} liens, {known} avec débit annoncé";
            if (pick == null) return report + $", aucun d'au plus {max / 1000000} Mb/s";
            report += $", n° {playable.IndexOf(pick) + 1} choisi : {pick.Bitrate / 1000} kb/s, {pick.Name}";
            Probe.Log("probe", "lecture : " + report);
            nav.Push(new PlayerScreen(target, show, pick, playable, info.PlaySessionId, 0, all: all));
            return report;
        }

        public void ProbeReset() => measure.ResetStats();

        // Touche reçue mais sans effet : à ne pas compter dans la mesure.
        public void KeyIgnored() => measure.KeyIgnored();

        // Gauche depuis le bord gauche d'un écran : la barre latérale prend le focus,
        // si l'écran la montre (sinon la touche est sans effet).
        public void ToSidebar()
        {
            if (!sidebar.Enter()) KeyIgnored();
        }

        // Après un changement de langue : tous les textes affichés sont relus.
        public void ApplyTexts()
        {
            nav.ApplyTexts();
            sidebar.ApplyTexts();
            measure.ApplyTexts();
        }

        void OnKey(object sender, Window.KeyEventArgs e)
        {
            string name = e.Key.KeyPressedName;
            if (e.Key.State == Key.StateType.Up) { HandleKeyUp(name, ""); return; }
            if (e.Key.State != Key.StateType.Down) return;
            HandleKey(name, "");
        }

        void HandleKey(string key, string origin)
        {
            Probe.Log("key", key + origin);
            Crash.Note("key " + key);
            measure.KeyDown(key);

            if (key == "XF86Back" || key == "Escape")
            {
                if (sidebar.Focused) { sidebar.Leave(); return; }
                if (nav.Top != null && nav.Top.OnBack()) return;
                if (!nav.Back()) Exit();
                return;
            }
            if (key == "XF86Red")
            {
                measure.Toggle();
                return;
            }
            if (sidebar.Focused)
            {
                if (!sidebar.OnKey(key)) KeyIgnored();
                return;
            }
            if (!nav.OnKey(key) && key == "Left") ToSidebar();
        }

        static void Main(string[] args)
        {
            Probe.Boot("Main (début du code .NET)");
            Crash.Hook();
            new App().Run(args);
        }
    }
}
