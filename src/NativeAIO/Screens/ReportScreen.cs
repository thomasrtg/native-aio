// Signaler un problème (module 18). Deux usages :
// - au lancement, après une session qui ne s'est pas fermée normalement (Core/Crash.cs) :
//   carte « Fermeture inattendue » des écrans de connexion, « Signaler le problème » ou
//   « Ignorer » (Retour aussi), puis l'écran de démarrage prend la place ;
// - depuis Paramètres → À propos : directement le QR code, Retour revient aux Paramètres.
// QR code : ticket GitHub prérempli, ouvert sur le téléphone, relu et envoyé par la personne.
// Rien n'est envoyé par la TV. Image PNG calculée hors du fil d'interface, à sa taille
// d'affichage (carrés de 4 à 10 px), pour rester nette.
using System;
using System.IO;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Path = System.IO.Path;

namespace NativeAIO.Screens
{
    class ReportScreen : Screen
    {
        const int QrMax = 900, QrX = 140;
        const int ButtonW = 280, ButtonH = 56;

        readonly Crash.Report report;
        readonly bool atStart; // lancement après un plantage : continue vers l'écran de démarrage

        AuthPage ask;
        View qrPage, qrBox;
        TextLabel title, text, button, status;
        bool showingQr;

        public ReportScreen(Crash.Report report, bool atStart)
        {
            this.report = report;
            this.atStart = atStart;
        }

        protected override View Build()
        {
            View root = AuthPage.Backdrop();

            if (atStart)
            {
                ask = new AuthPage();
                ask.AddTitle(() => Lang.T("report.crash_title"));
                ask.AddHelp(() => Lang.T("report.crash_help"));
                AuthPage.Item send = ask.AddButton(() => Lang.T("report.send"), ShowQr);
                ask.AddLinks((() => Lang.T("report.ignore"), Continue));
                ask.Focus(send);
                root.Add(ask.View);
            }

            qrPage = new View { Size = new Size(1920, 1080) };
            qrBox = new View { BackgroundColor = Color.White }.Rounded(Theme.RadiusLarge);
            qrPage.Add(qrBox);
            status = new TextLabel
            {
                PointSize = Theme.SizeBody,
                TextColor = Theme.Grey,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Position = new Position(QrX, (1080 - 60) / 2),
                Size = new Size(QrMax, 60),
            };
            qrPage.Add(status);

            int textX = QrX + QrMax + 80, textW = 1920 - Theme.Margin - textX;
            title = new TextLabel
            {
                PointSize = Theme.SizeTitle,
                TextColor = Color.White,
                FontStyle = Theme.SemiBold(),
                Position = new Position(textX, 200),
                Size = new Size(textW, 50),
                VerticalAlignment = VerticalAlignment.Center,
            };
            text = new TextLabel
            {
                PointSize = Theme.SizeBody,
                TextColor = Theme.Grey,
                MultiLine = true,
                Position = new Position(textX, 280),
                Size = new Size(textW, 500),
                VerticalAlignment = VerticalAlignment.Top,
            };
            // Seul élément : toujours regardé (fond blanc), OK ou Retour ferment.
            var box = new View
            {
                Position = new Position(textX, 820),
                Size = new Size(ButtonW, ButtonH),
                BackgroundColor = Color.White,
            }.Rounded(ButtonH / 2);
            button = new TextLabel
            {
                PointSize = Theme.SizeBody,
                TextColor = Theme.Ink,
                Size = new Size(ButtonW, ButtonH),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            box.Add(button);
            qrPage.Add(title);
            qrPage.Add(text);
            qrPage.Add(box);
            root.Add(qrPage);

            if (atStart) qrPage.Hide();
            else ShowQr();
            return root;
        }

        public override void ApplyTexts()
        {
            ask?.ApplyTexts();
            title.Text = Lang.T("report.qr_title");
            text.Text = Lang.T("report.qr_help");
            button.Text = Lang.T("report.close");
            if (status.Text.Length > 0 && qrBox.ChildCount == 0) status.Text = Lang.T("report.making");
        }

        public override bool OnKey(string key)
        {
            if (!showingQr) return ask.OnKey(key);
            if (key == "Return" || key == "KP_Enter" || key == "Select") { Close(); return true; }
            return true; // rien d'autre à regarder
        }

        public override bool OnBack()
        {
            if (atStart) { if (!showingQr && ask.OnBack()) return true; Continue(); return true; }
            return false; // Paramètres : le Navigator revient en arrière
        }

        void Close()
        {
            if (atStart) Continue();
            else Nav.Back();
        }

        void Continue()
        {
            if (Closed) return;
            Nav.Replace(new StartScreen());
        }

        async void ShowQr()
        {
            if (showingQr) return;
            showingQr = true;
            ask?.View.Hide();
            qrPage.Show();
            status.Text = Lang.T("report.making");
            string file = null;
            int side = 0;
            try
            {
                string url = report.IssueUrl();
                (file, side) = await Task.Run(() => QrImage.Write(url, QrMax, "report"));
            }
            catch (Exception e)
            {
                Probe.Log("error", "QR code : " + e.GetType().Name + " " + e.Message);
            }
            if (Closed) return;
            if (file == null) { status.Text = Lang.T("report.failed"); return; }
            status.Text = "";
            status.Hide();
            // La marge blanche fait partie de l'image ; le cadre arrondi blanc l'entoure.
            qrBox.Size = new Size(side, side);
            qrBox.Position = new Position(QrX + (QrMax - side) / 2, (1080 - side) / 2);
            // Fichier local à sa taille : sans DesiredWidth/Height (LECONS.md).
            qrBox.Add(new ImageView { ResourceUrl = file, Size = new Size(side, side) });
        }
    }
}
