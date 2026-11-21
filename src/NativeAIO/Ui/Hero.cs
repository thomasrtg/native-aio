// Bannière à la une de l'accueil (module 14b), d'après components/hero.tsx de la version web,
// mode « Fait défiler les titres à la une » :
// - image de fond calée à droite (gardée dans 2,6 fois sa hauteur, comme BackdropFrame), qui
//   s'efface vers la gauche et vers le bas par un masque (mêmes courbes que les voiles du web ;
//   un voile noir posé par-dessus restait invisible sur la TV, voir LECONS.md) : elle se fond
//   dans le fond de l'écran, ou dans l'arrière-plan d'après l'image ;
// - logo (ou titre en 48 pt), ligne d'informations (année ou épisode, note, 3 genres), résumé
//   sur 3 lignes, boutons Lecture et Plus d'infos (Ouvrir seul si le titre ne se lance pas
//   directement), points de pagination en bas à droite ;
// - titre suivant toutes les 9 s (réglage « Durée de chaque titre », heroSeconds). Le titre suivant est préparé caché (textes, logo, image,
//   arrière-plan d'après l'image) ; quand tout est prêt, l'ancien sort en fondu (0,3 s, charte)
//   puis le nouveau entre en fondu (0,3 s), avec l'arrière-plan d'après l'image : tout change
//   ensemble, quitte à attendre (demandé par l'auteur le 2026-11-15).
//   Contrairement au web, le défilement continue quand le focus est dans la bannière (l'accueil
//   s'ouvre sur elle : sinon il ne défilerait jamais) ; chaque appui relance le décompte.
// - Gauche et Droite passent d'un bouton à l'autre, puis au titre voisin (comme le web).
// Modes « Suit la carte sélectionnée » et « dans le coin » (FollowHero du web, étape 2) : la
// bannière, fixe en haut (55 % ou 45 % de l'écran), montre le titre de la carte regardée dans les
// rangées, 0,3 s après que le focus s'y est posé ; ni boutons, ni points, ni défilement (sur TV,
// le web n'en met pas). Image du titre sur tout l'écran, ou dans le coin en haut à droite.
// Même changement synchronisé ; les images des cartes voisines sont décodées d'avance.
// Les textes et l'image d'un nouveau titre sont posés au tour de boucle suivant, jamais dans
// le traitement d'une touche (LECONS.md).
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using Size = Tizen.NUI.Size;
using Path = System.IO.Path;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Ui
{
    class Hero
    {
        public const int Height = 576; // « Fait défiler » ; web, grand écran : max(36rem, 53vh) à 1080 px
        const int EdgeW = 192; // bord gauche de l'image adouci sur 12rem (web)
        const int FollowMs = 300; // FOLLOW_DELAY_MS du web
        const int TextX = Theme.Margin, TextW = 900, LogoW = 480, LogoH = 140;
        const int OverviewLines = 3, LineH = 34, MetaH = 40;

        public enum Kind { Rotate, Follow, Corner }
        readonly Kind kind;
        bool Following => kind != Kind.Rotate;
        // Hauteur : 576 px (défile), 55 % de l'écran (suit la carte), 45 % (dans le coin).
        public readonly int H;
        // Image : largeur, bord gauche, hauteur de sa zone (centrée dedans, comme « object-cover »).
        readonly int frameW, frameX, boxH;
        readonly int buttonsY, overviewY, metaY, logoY;
        // Même exception que les textes sous la carte ouverte de l'accueil (22 et 20 pt).
        const float MetaSize = 22, OverviewSize = 20;
        const int IconSize = 26;

        class Btn
        {
            public View Box;
            public ImageView Icon;
            public TextLabel Label;
        }

        // Logo (ou titre écrit), informations et résumé d'un titre. Deux faces : celle affichée,
        // et celle où le titre suivant est préparé, cachée.
        class Face
        {
            public View Box;
            public TextLabel Title, Meta, Overview;
            public ImageView Logo;
            public Item Item;
        }

        readonly Action<string, Action> guard;
        readonly List<Item> items = new List<Item>();
        readonly List<ImageView> layers = new List<ImageView>(); // images de la bannière, la dernière dessus
        readonly List<View> dotViews = new List<View>();
        readonly Dictionary<string, Task<Item>> details = new Dictionary<string, Task<Item>>();
        readonly View images, shades, faces, buttons, dots;
        readonly Btn play, info;
        Face front, back; // affichée ; préparée (cachée)
        Timer rotate, apply, gate; // gardés dans des champs, sinon ils s'arrêtent
        Animation fade; // sortie ou entrée en cours
        readonly List<Animation> keep = new List<Animation>(); // fondus isolés en cours, gardés
        int index, button;
        bool toLast; // en reculant : focus sur le dernier bouton du titre suivant
        bool focused, running, closed;
        // Changement de titre : prêt (image, logo, arrière-plan) ou GateMs écoulé → sortie → entrée.
        const int GateMs = 2500;
        int step; // numéro du changement en cours
        int appliedAt, imageMs, markMs, washMs; // mesures pour la sonde
        int fileMs = -1; bool filePre;           // fichier de l'image reçu (ms), ou image préparée d'avance
        bool imageIn, washIn, markIn, leaving, entered;
        ImageView waitingImage; // image du titre préparé, encore cachée
        Action washPlay; // fondu de l'arrière-plan d'après l'image préparé

        public View Root { get; }
        public int Count => items.Count;
        public Item Current => index < items.Count ? items[index] : null;
        Item Showing => front?.Item ?? Current; // titre à l'écran (boutons, Entrée)
        // Titre préparé : l'arrière-plan d'après l'image est préparé, puis ready(fondu) appelé
        // (ready(null) : rien à changer).
        public Action<Item, Action<Action>> Shown;
        public Action<Item> PlayIt;  // Lecture
        public Action<Item> OpenIt;  // Plus d'infos, Ouvrir

        public Hero(Action<string, Action> guard, Kind kind = Kind.Rotate)
        {
            this.guard = guard;
            this.kind = kind;
            switch (kind)
            {
                case Kind.Follow: // image sur tout l'écran (BackdropFrame et Shade du web)
                    H = 594; frameW = 1920; frameX = 0; boxH = 1080;
                    break;
                case Kind.Corner: // coin : 70 % de la largeur, au plus 1,4 fois la bannière de haut
                    H = 486; frameW = 1344; frameX = 1920 - frameW; boxH = Math.Min(frameW * 9 / 16, H * 14 / 10);
                    break;
                default: // calée à droite, dans 2,6 fois sa hauteur
                    H = Height; frameW = Height * 26 / 10; frameX = 1920 - frameW; boxH = Height;
                    break;
            }
            // Textes au-dessus des boutons ou, sans boutons, à 16 px du bas (pb-4 du web).
            buttonsY = H - 56 - Theme.ButtonHeight;
            overviewY = Following ? H - 16 - OverviewLines * LineH : buttonsY - 24 - OverviewLines * LineH;
            metaY = overviewY - 12 - MetaH;
            logoY = metaY - 16 - LogoH;
            Root = new View { Size = new Size(1920, H) };
            // Pendant la bande-annonce (bannière qui suit la carte) : voile et remplissage, sous l'image.
            shades = new View { Size = new Size(1920, H), Opacity = 0 };
            Root.Add(shades);
            images = new View { Size = new Size(1920, H) };
            Root.Add(images);
            faces = new View { Size = new Size(1920, H) };
            Root.Add(faces);
            front = MakeFace();
            back = MakeFace();
            buttons = new View { Size = new Size(1920, H), Opacity = 0 };
            Root.Add(buttons);
            play = MakeButton("play_lu");
            info = MakeButton("info");
            dots = new View();
            Root.Add(dots);
        }

        Face MakeFace()
        {
            var f = new Face { Box = new View { Size = new Size(1920, H), Opacity = 0 } };
#pragma warning disable CS0618 // ShadowOffset/ShadowColor : dépréciés en API 8, toujours présents (comme l'accueil)
            f.Title = new TextLabel
            {
                Position = new Position(TextX, logoY),
                Size = new Size(TextW, LogoH),
                PointSize = Theme.SizeHuge,
                TextColor = Color.White,
                VerticalAlignment = VerticalAlignment.Bottom,
                ShadowOffset = new Vector2(2, 2),
                ShadowColor = new Color(0f, 0f, 0f, 0.9f),
            };
#pragma warning restore CS0618
            f.Title.Hide();
            f.Meta = new TextLabel { Position = new Position(TextX, metaY), Size = new Size(TextW, MetaH), PointSize = MetaSize, TextColor = Theme.Text, VerticalAlignment = VerticalAlignment.Center };
            f.Overview = new TextLabel { Position = new Position(TextX, overviewY), Size = new Size(TextW, OverviewLines * LineH), PointSize = OverviewSize, MultiLine = true, TextColor = Color.White };
            f.Box.Add(f.Title);
            f.Box.Add(f.Meta);
            f.Box.Add(f.Overview);
            faces.Add(f.Box);
            return f;
        }

        Btn MakeButton(string icon)
        {
            var b = new Btn
            {
                Box = new View { PositionY = buttonsY, Size = new Size(200, Theme.ButtonHeight), CornerRadius = Theme.ButtonHeight / 2 },
                Icon = new ImageView(Theme.IconPath(icon)) { Position = new Position(24, (Theme.ButtonHeight - IconSize) / 2), Size = new Size(IconSize, IconSize) },
                Label = new TextLabel { Position = new Position(24 + IconSize + 12, 0), Size = new Size(200, Theme.ButtonHeight), PointSize = Theme.SizeBody, VerticalAlignment = VerticalAlignment.Center, TextColor = Theme.Text },
            };
            b.Box.Add(b.Icon);
            b.Box.Add(b.Label);
            b.Box.Hide();
            buttons.Add(b.Box);
            return b;
        }

        // ---- Titres ----

        // Titres présentés : ceux qui ont une image de fond ou en largeur (backdropSrc du web).
        public void SetItems(List<Item> list)
        {
            items.Clear();
            foreach (Item it in list) if (BackdropUrl(it) != null) items.Add(it);
            index = 0;
            button = 0;
            foreach (View d in dotViews) { d.Unparent(); d.Dispose(); }
            dotViews.Clear();
            if (items.Count > 1)
                for (int i = 0; i < items.Count; i++)
                {
                    var d = new View { Size = new Size(6, 6), CornerRadius = 3 };
                    dots.Add(d);
                    dotViews.Add(d);
                }
            if (items.Count == 0) return;
            PaintDots();
            Apply();
            Restart();
        }

        public static string BackdropUrl(Item it) => Library.Backdrop(it, 1920) ?? Library.Landscape(it, 1920);

        static bool Playable(Item it) => (it.Type == "Movie" || it.Type == "Episode") && it.LocationType != "Virtual";

        // Passe au titre voisin ; le focus se pose sur le premier bouton (en avançant) ou le
        // dernier (en reculant), comme le web, quand le titre paraît. Les points changent tout de suite.
        void Go(int step, bool keepButton = false)
        {
            if (items.Count < 2) return;
            index = (index + step + items.Count) % items.Count;
            if (!keepButton) { button = 0; toLast = step < 0; }
            PaintDots();
            // Préparation au tour de boucle suivant.
            apply?.Stop();
            apply = new Timer(1);
            apply.Tick += (s, e) => { if (!closed) guard("HeroApply", Apply); return false; };
            apply.Start();
        }

        // Prépare le titre en cours, caché : textes et logo sur la face cachée, image, fiche,
        // arrière-plan d'après l'image ; puis le titre suivant, d'avance.
        void Apply()
        {
            Item it = Current;
            if (it == null) return;
            int s = ++step;
            appliedAt = Environment.TickCount;
            imageIn = washIn = markIn = leaving = entered = false;
            imageShown = completeFired = false;
            fileMs = -1;
            filePre = false;
            imageWait?.Stop();
            gate?.Stop();
            // Changement interrompu : les fondus restent où ils en sont, la sortie suivante part de là.
            fade?.Stop();
            if (waitingImage != null) { Drop(waitingImage); waitingImage = null; }
            washPlay = null;
            Fill(back, it, s);
            gate = new Timer(GateMs);
            gate.Tick += (o, e) => { if (!closed && s == step) guard("HeroGate", () => Leave(s)); return false; };
            gate.Start();
            if (Shown != null) Shown(it, play => guard("HeroWash", () => WashReady(s, play)));
            else WashReady(s, null);
            ImageCache.Request(BackdropUrl(it), () => closed ? int.MaxValue : 0, path => guard("HeroImage", () => ShowImage(s, path)));
            // Le titre suivant préparé d'avance (image, fiche, logo, arrière-plan) : sans attente.
            // (Suit la carte : les cartes voisines seulement une fois ce titre complet, Complete.)
            if (items.Count > 1) Prepare(items[(index + 1) % items.Count]);
            ImageCache.Pump();
        }

        // Textes du titre sur une face cachée ; logo ou titre écrit demandé.
        void Fill(Face f, Item it, int s)
        {
            f.Item = it;
            if (f.Logo != null) { f.Logo.Unparent(); f.Logo.Dispose(); f.Logo = null; }
            f.Title.Hide();
            // Les catalogues ne donnent ni logo ni genres (vu le 2026-11-15) : fiche du titre
            // demandée (une fois), puis logo ou titre écrit. Déjà reçue : tout de suite.
            Task<Item> t = DetailsOf(it);
            if (t != null && t.IsCompleted) Merge(it, t.Status == TaskStatus.RanToCompletion ? t.Result : null);
            FillTexts(f, it);
            if (t == null || t.IsCompleted) ShowMark(f, it, s);
            else WaitDetails(f, it, t, s);
        }

        Task<Item> DetailsOf(Item it)
        {
            if (it.Id == null) return null;
            if (!details.TryGetValue(it.Id, out Task<Item> t) || t.IsFaulted) { t = Library.Details(it.Id); details[it.Id] = t; }
            return t;
        }

        // Ce que la fiche apporte : logo, genres, résumé.
        static void Merge(Item it, Item d)
        {
            if (d == null) return;
            if (string.IsNullOrEmpty(it.Overview)) it.Overview = d.Overview;
            if (it.Genres == null || it.Genres.Length == 0) it.Genres = d.Genres;
            if (d.ImageTags?.Logo != null)
            {
                if (it.ImageTags == null) it.ImageTags = d.ImageTags;
                else if (it.ImageTags.Logo == null) it.ImageTags.Logo = d.ImageTags.Logo;
            }
            if (it.ParentLogoImageTag == null && d.ParentLogoImageTag != null)
            {
                it.ParentLogoItemId = d.ParentLogoItemId;
                it.ParentLogoImageTag = d.ParentLogoImageTag;
            }
        }

        async void WaitDetails(Face f, Item it, Task<Item> t, int s)
        {
            Item d = null;
            try { d = await t; } catch (Exception) { }
            if (closed) return;
            Merge(it, d);
            if (s == step) guard("HeroDetails", () => { FillTexts(f, it); ShowMark(f, it, s); });
        }

        // Logo, ou titre écrit s'il n'y en a pas.
        void ShowMark(Face f, Item it, int s)
        {
            string url = Library.Logo(it, LogoW);
            if (url == null) ShowTitle(f, it, s);
            else ImageCache.Request(url, () => closed ? int.MaxValue : 0, path => guard("HeroLogo", () => ShowLogo(f, it, s, path)));
        }

        void MarkReady(int s)
        {
            if (closed || s != step || markIn) return;
            markIn = true;
            markMs = Environment.TickCount - appliedAt;
            TryLeave(s);
        }

        // Arrière-plan d'après l'image prêt (play : son fondu ; null : rien à changer).
        void WashReady(int s, Action play)
        {
            if (closed || s != step) return; // un autre titre est demandé depuis
            if (entered) { play?.Invoke(); return; }
            washPlay = play;
            washIn = true;
            washMs = Environment.TickCount - appliedAt;
            TryLeave(s);
        }

        // Image de la bannière prête (null : pas d'image).
        void ImageReady(int s, ImageView img)
        {
            if (closed) return;
            if (s != step) { if (img != null) Drop(img); return; }
            if (entered)
            {
                // Arrivée après l'entrée du titre (plus de GateMs) : le titre est complet maintenant.
                Probe.Log("hero", $"image arrivée après l'entrée : {Environment.TickCount - appliedAt} ms après le changement{FileNote()}");
                if (img != null) FadeIn(img);
                imageShown = true;
                CheckComplete();
                return;
            }
            waitingImage = img;
            imageIn = true;
            imageMs = Environment.TickCount - appliedAt;
            TryLeave(s);
        }

        void TryLeave(int s)
        {
            if (imageIn && washIn && markIn) Leave(s);
        }

        // Tout est prêt (ou GateMs écoulé) : l'ancien titre sort en fondu (textes, logo, boutons,
        // image), puis le nouveau entre.
        void Leave(int s)
        {
            if (closed || s != step || leaving) return;
            leaving = true;
            gate?.Stop();
            Probe.Log("hero", $"titre prêt {Environment.TickCount - appliedAt} ms après le changement (image {(imageIn ? imageMs + " ms" + FileNote() : "en retard" + FileNote())}, logo {(markIn ? markMs + " ms" : "en retard")}, arrière-plan {(washIn ? washMs + (washPlay != null ? " ms" : " ms, inchangé") : "en retard")})");
            if (front.Item == null) { Enter(s); return; } // premier titre : rien à faire sortir
            fade?.Stop();
            fade = new Animation(Theme.Fade);
            fade.AnimateTo(front.Box, "Opacity", 0f);
            fade.AnimateTo(buttons, "Opacity", 0f);
            foreach (ImageView l in layers) if (l != waitingImage) fade.AnimateTo(l, "Opacity", 0f);
            fade.Finished += (o, e) => { if (!closed && s == step) guard("HeroEnter", () => Enter(s)); };
            fade.Play();
        }

        // Le nouveau titre entre en fondu : textes, logo, boutons, image et arrière-plan d'après
        // l'image, au même moment et sur la même durée (0,3 s).
        void Enter(int s)
        {
            if (closed || s != step || entered) return;
            entered = true;
            Probe.Boot("bannière : premier titre affiché");
            Face f = front;
            front = back;
            back = f;
            back.Box.Opacity = 0;
            foreach (ImageView l in layers.ToArray()) if (l != waitingImage) Drop(l);
            PlaceButtons();
            fade?.Stop();
            fade = new Animation(Theme.Fade);
            fade.AnimateTo(front.Box, "Opacity", 1f);
            fade.AnimateTo(buttons, "Opacity", 1f);
            if (waitingImage != null) fade.AnimateTo(waitingImage, "Opacity", 1f);
            // Image cachée par la bande-annonce de la carte d'avant : elle revient avec ce titre.
            if (imagesHidden) { fade.AnimateTo(images, "Opacity", 1f); imagesHidden = false; }
            fade.Play();
            // Image entrée avec le titre, ou pas d'image du tout (imageIn sans image) ; sinon
            // elle arrive plus tard (ImageReady).
            imageShown = waitingImage != null || imageIn;
            waitingImage = null;
            washPlay?.Invoke();
            washPlay = null;
            if (!imageShown && Following) WaitImage(s);
            CheckComplete();
        }

        // Image toujours absente 2,5 s après l'entrée du titre (vu deux fois le 2026-11-17, image
        // jamais arrivée) : le titre est dit complet sans elle, pour que la bande-annonce et les
        // cartes voisines ne restent pas bloquées. Elle entre en fondu si elle arrive ensuite.
        const int ImageWaitMs = 2500;
        Timer imageWait;

        void WaitImage(int s)
        {
            imageWait?.Stop();
            imageWait = new Timer(ImageWaitMs);
            imageWait.Tick += (o, e) =>
            {
                guard("HeroImageWait", () =>
                {
                    if (closed || s != step || imageShown) return;
                    Probe.Log("hero", $"image toujours absente {ImageWaitMs} ms après l'entrée{FileNote()} : titre complet sans elle");
                    imageShown = true;
                    CheckComplete();
                });
                return false;
            };
            imageWait.Start();
        }

        // Origine de l'image pour la sonde : préparée d'avance, ou temps de réception du fichier.
        string FileNote() => filePre ? ", préparée d'avance" : fileMs >= 0 ? $", dont fichier {fileMs} ms" : ", fichier pas reçu";

        // ---- Ordre, quand la bannière suit la carte (demande de l'auteur, 2026-11-17) ----
        // 1. appui : la carte d'avant est abandonnée (bande-annonce, préparations) ;
        // 2. 0,3 s de repos : seul ce titre se prépare (textes, logo, image, arrière-plan) ;
        // 3. tout prêt : l'ancien sort, le nouveau entre ;
        // 4. titre complet (image affichée) : voile de la bande-annonce, puis cartes voisines ;
        // 5. bande-annonce minutée à partir d'ici (Complete), montrée après l'image et le voile.

        bool imageShown, completeFired;
        public Action<Item> Complete; // le titre montré est complet : sa bande-annonce peut se préparer

        // Le titre montré est complet pour cette carte (entré, image affichée).
        public bool IsComplete(Item it) => completeFired && it != null && front.Item != null && front.Item.Id == it.Id;

        void CheckComplete()
        {
            if (!entered || !imageShown || completeFired || closed) return;
            completeFired = true;
            if (!Following) return;
            int s = step;
            Probe.Log("hero", $"titre complet {Environment.TickCount - appliedAt} ms après le changement");
            PrepareVideo();
            OnVeil(ok =>
            {
                if (closed || s != step) return;
                foreach (Item n in neighbours) if (n != null && n.Id != Current?.Id) Prepare(n);
                ImageCache.Pump();
            }, 1000);
            Complete?.Invoke(front.Item);
        }

        void Drop(ImageView img)
        {
            layers.Remove(img);
            img.Unparent();
            img.Dispose();
        }

        async void Prepare(Item next)
        {
            ImageCache.Request(BackdropUrl(next), () => closed ? int.MaxValue : 50, path => guard("HeroPre", () => PreImage(path)));
            // Son arrière-plan d'après l'image calculé d'avance : prêt en même temps que l'image.
            if (ArtWash.On) ArtWash.Request(next, () => closed ? int.MaxValue : 50, (p, d) => { });
            Task<Item> t = DetailsOf(next);
            Item d = null;
            try { if (t != null) d = await t; } catch (Exception) { }
            if (closed) return;
            Merge(next, d);
            string url = Library.Logo(next, LogoW);
            if (url != null) ImageCache.Request(url, () => closed ? int.MaxValue : 50, _ => { });
        }

        static void FillTexts(Face f, Item it)
        {
            f.Meta.Text = MetaOf(it);
            SetOverview(f.Overview, it.Overview ?? "");
        }

        // Épisode (code et nom) ou année, note, trois genres (HeroDetails du web).
        static string MetaOf(Item it)
        {
            var parts = new List<string>();
            if (it.Type == "Episode")
            {
                string code = it.ParentIndexNumber != null && it.IndexNumber != null ? Lang.T("episode.code", it.ParentIndexNumber, it.IndexNumber) : null;
                string ep = string.Join(" ", new[] { code, it.Name }).Trim();
                if (ep.Length > 0) parts.Add(ep);
            }
            else if (it.ProductionYear != null) parts.Add(it.ProductionYear.Value.ToString());
            string score = Library.Score(it);
            if (score != null) parts.Add(score);
            if (it.Genres != null && it.Genres.Length > 0)
                parts.Add(string.Join(", ", it.Genres, 0, Math.Min(3, it.Genres.Length)));
            return string.Join("  ·  ", parts);
        }

        // Résumé sur 3 lignes au plus, coupé à la fin d'un mot par « … » (comme l'accueil).
        static void SetOverview(TextLabel overview, string text)
        {
            overview.Text = text;
            if (text.Length == 0) return;
            int lines = overview.LineCount;
            if (lines <= OverviewLines) return;
            int len = (int)(text.Length * (double)OverviewLines / lines);
            for (int tries = 0; tries < 6 && len > 1; tries++)
            {
                overview.Text = Pictures.Cut(text, len);
                if (overview.LineCount <= OverviewLines) return;
                len = (int)(len * 0.92);
            }
        }

        void ShowTitle(Face f, Item it, int s)
        {
            if (closed || s != step) return;
            f.Title.Text = Library.Title(it);
            f.Title.Show();
            MarkReady(s);
        }

        // Logo dans LogoW × LogoH, posé sur le bas de sa place (au-dessus des informations).
        void ShowLogo(Face f, Item it, int s, string path)
        {
            if (closed || s != step || f.Logo != null) return;
            if (path == null) { ShowTitle(f, it, s); return; }
            Pictures.ImageSize(path, out int nw, out int nh);
            if (nw <= 0 || nh <= 0) { ShowTitle(f, it, s); return; }
            float k = Math.Min(1f, Math.Min((float)LogoW / nw, (float)LogoH / nh));
            int w = Math.Max(1, (int)(nw * k)), h = Math.Max(1, (int)(nh * k));
            ImageView v = Pictures.Decoded(path, w, h);
            if (v == null) { ShowTitle(f, it, s); return; }
            v.Position = new Position(TextX, logoY + LogoH - h);
            f.Title.Hide();
            f.Logo = v;
            if (entered) Alone(v); // arrivé après les textes (plus de GateMs)
            f.Box.Add(v);
            if (v.LoadingStatus == ImageView.LoadingStatusType.Ready) MarkReady(s);
            else v.ResourceReady += (o, e) => guard("HeroLogoReady", () => MarkReady(s));
        }

        // Fondu d'entrée d'un élément arrivé en retard.
        void Alone(View v)
        {
            v.Opacity = 0;
            var a = new Animation(Theme.Fade);
            a.AnimateTo(v, "Opacity", 1f);
            a.Play();
            keep.Add(a);
            if (keep.Count > 4) keep.RemoveAt(0);
        }

        // Nouvelle image en fondu par-dessus la précédente, retirée ensuite.
        void ShowImage(int s, string path)
        {
            if (closed || s != step) return;
            fileMs = Environment.TickCount - appliedAt;
            if (path == null) { ImageReady(s, null); return; }
            // Préparée d'avance (titre suivant) : déjà décodée ou en cours.
            ImageView img;
            if (pre.TryGetValue(path, out img)) { pre.Remove(path); preKeys.Remove(path); img.RaiseToTop(); filePre = true; }
            else img = MakeImage(path);
            if (img == null) { ImageReady(s, null); return; }
            layers.Add(img);
            // Déjà prête (même fichier affiché avant, ou préparée) : le signal est déjà passé (LECONS.md).
            if (img.LoadingStatus == ImageView.LoadingStatusType.Ready) { ImageReady(s, img); return; }
            img.ResourceReady += (o, e) => guard("HeroImageReady", () => ImageReady(s, img));
        }

        // Image de la bannière, cachée (opacité 0), posée dans la bannière : son décodage commence.
        // Le décodage de cette grande image prend 1,3 à 1,7 s sur la TV (mesuré le 2026-11-16).
        ImageView MakeImage(string path)
        {
            // Image entière à la largeur du cadre, centrée en hauteur (comme « object-cover ») :
            // le masque s'applique à l'image avant un recadrage au décodage (ScaleToFill), d'où un
            // bas coupé net au premier essai (2026-11-15). Ce qui dépasse sous la bannière est
            // rendu transparent par le masque, ce qui dépasse en haut sort de l'écran.
            Pictures.ImageSize(path, out int nw, out int nh);
            if (nw <= 0 || nh <= 0) { nw = 16; nh = 9; }
            int h = Math.Max(1, (int)Math.Round((double)frameW * nh / nw));
            // Suit la carte : décodée aux 2/3 de sa largeur (2,25 fois moins de points), puis
            // agrandie à l'écran. Mesuré le 2026-11-17 en déplacement rapide : image en plus de
            // 1,2 s pour 2 titres sur 17 aux 2/3, 12 sur 17 en pleine taille (accord de l'auteur).
            int dw = Following ? frameW * 2 / 3 : frameW;
            int dh = Math.Max(1, (int)Math.Round((double)dw * nh / nw));
            ImageView img = Pictures.Decoded(path, dw, dh);
            if (img == null) return null;
            img.Size = new Size(frameW, h);
            img.CropToMask = false;
            img.AlphaMaskURL = Mask(h);
            img.Position = new Position(frameX, ImageTop(h));
            img.Opacity = 0;
            images.Add(img);
            return img;
        }

        // Image du titre suivant décodée d'avance (deux au plus) : le changement n'attend plus.
        readonly Dictionary<string, ImageView> pre = new Dictionary<string, ImageView>();
        readonly List<string> preKeys = new List<string>();

        void PreImage(string path)
        {
            if (closed || path == null || pre.ContainsKey(path)) return;
            ImageView img = MakeImage(path);
            if (img == null) return;
            pre[path] = img;
            preKeys.Add(path);
            while (preKeys.Count > 2)
            {
                string k = preKeys[0];
                preKeys.RemoveAt(0);
                ImageView old = pre[k];
                pre.Remove(k);
                old.Unparent();
                old.Dispose();
            }
        }

        // Image arrivée après l'entrée du titre (plus de GateMs) : son propre fondu, les images
        // dessous retirées ensuite.
        void FadeIn(ImageView img)
        {
            var a = new Animation(Theme.Fade);
            a.AnimateTo(img, "Opacity", 1f);
            a.Finished += (s, e) => guard("HeroImageFaded", () =>
            {
                int at = layers.IndexOf(img);
                for (int i = at - 1; i >= 0; i--) Drop(layers[i]);
            });
            a.Play();
            keep.Add(a);
            if (keep.Count > 4) keep.RemoveAt(0);
        }

        // ---- Boutons et points ----

        void PlaceButtons()
        {
            Item it = Showing;
            if (it == null || Following) return; // sur TV, le web n'en met pas quand la bannière suit la carte
            bool canPlay = Playable(it);
            int n = canPlay ? 2 : 1;
            if (toLast) { button = n - 1; toLast = false; }
            button = Math.Min(button, n - 1);
            play.Label.Text = Lang.T("item.play");
            info.Label.Text = Lang.T(canPlay ? "hero.info" : "hero.open");
            int x = TextX;
            foreach (Btn b in canPlay ? new[] { play, info } : new[] { info })
            {
                float tw = b.Label.NaturalSize2D.Width;
                if (tw <= 0) tw = b.Label.Text.Length * 13;
                int w = 24 + IconSize + 12 + (int)tw + 28;
                b.Box.Size = new Size(w, Theme.ButtonHeight);
                b.Label.Size = new Size((int)tw + 8, Theme.ButtonHeight);
                b.Box.PositionX = x;
                b.Box.Show();
                x += w + Theme.Gap;
            }
            if (!canPlay) play.Box.Hide();
            Paint();
        }

        void Paint()
        {
            Item it = Showing;
            bool canPlay = it != null && Playable(it);
            Btn[] shown = canPlay ? new[] { play, info } : new[] { info };
            for (int i = 0; i < shown.Length; i++)
            {
                bool on = focused && i == button;
                shown[i].Box.BackgroundColor = on ? Color.White : Theme.Surface;
                shown[i].Label.TextColor = on ? Theme.Ink : Theme.Text;
                shown[i].Icon.Color = on ? Theme.Ink : Color.White; // icône blanche teinte en sombre
            }
        }

        // Point en cours : 24 × 6 blanc ; autres : 6 × 6 blanc à 40 % (comme le web).
        void PaintDots()
        {
            int x = 0;
            for (int i = 0; i < dotViews.Count; i++)
            {
                bool on = i == index;
                View d = dotViews[i];
                d.Size = new Size(on ? 24 : 6, 6);
                d.BackgroundColor = on ? Color.White : Theme.White(0.4f);
                d.Position = new Position(x, 0);
                x += (on ? 24 : 6) + 6;
            }
            int w = Math.Max(0, x - 6);
            dots.Position = new Position(1920 - Theme.Margin - w, buttonsY +Theme.ButtonHeight / 2 - 3);
        }

        // ---- Focus et touches ----

        public void Focus(bool on)
        {
            if (on && !focused) button = 0;
            focused = on;
            Paint();
        }

        // Faux : Gauche depuis le premier bouton du premier titre (la barre latérale).
        public bool Left()
        {
            if (items.Count == 0) return false;
            Restart();
            if (button > 0) { button--; Paint(); return true; }
            if (index == 0) return false;
            Go(-1);
            return true;
        }

        // Faux : rien à droite (un seul titre).
        public bool Right()
        {
            if (items.Count == 0) return false;
            Restart();
            int shown = Playable(Showing) ? 2 : 1;
            if (button < shown - 1) { button++; Paint(); return true; }
            if (items.Count < 2) return false;
            Go(1);
            return true;
        }

        public void Activate()
        {
            Item it = Showing;
            if (it == null) return;
            Restart();
            if (Playable(it) && button == 0) PlayIt?.Invoke(it);
            else OpenIt?.Invoke(it);
        }

        // Haut de l'image : centrée dans sa zone (comme « object-cover »).
        int ImageTop(int imgH) => (boxH - imgH) / 2;

        // ---- Bande-annonce (quand la bannière suit la carte), comme HeroTrailer du web ----
        // (components/media/hero.tsx et features/trailers/preview.tsx de la version publique,
        // lus le 2026-11-16) : boîte au format 2,39:1, haute comme la bannière, en haut à droite
        // (les bandes noires des bandes-annonces sont coupées) ; l'image fixe garde sa place et
        // s'efface en 0,7 s ; bords fondus dans l'arrière-plan.

        // Rectangle de la vidéo : 1420 × 594 (suit la carte) ou 1162 × 486 (dans le coin).
        public Rectangle VideoRect
        {
            get
            {
                int w = (int)Math.Round(H * 2.39);
                return new Rectangle(1920 - w, 0, w, H);
            }
        }

        Animation videoFade;
        ImageView veil, filler;

        // Préparé quand la bande-annonce se charge, pour être décodé avant qu'elle se voie :
        // l'arrière-plan d'après l'image en plein écran (ou une image unie de la couleur de fond),
        // deux fois : le voile, percé par le masque à la place de la vidéo (bords fondus dans
        // l'arrière-plan lui-même, sans démarcation), et le remplissage, entier.
        public void PrepareVideo()
        {
            DropVideo();
            string src = Source(out bool dark);
            veilSrc = src;
            veilAt = Environment.TickCount;
            veil = Cover(src, dark);
            veil.CropToMask = false;
            veil.AlphaMaskURL = VeilMask();
            filler = Cover(src, dark);
            shades.Add(veil);
            shades.Add(filler);
            shades.Opacity = 0;
        }

        string veilSrc;
        int veilAt; // mesure pour la sonde
        bool imagesHidden; // image laissée cachée après une bande-annonce, jusqu'au titre suivant
        readonly List<Timer> veilTimers = new List<Timer>(); // attentes du voile, gardées

        bool VeilReady => veil != null && filler != null
            && veil.LoadingStatus == ImageView.LoadingStatusType.Ready && filler.LoadingStatus == ImageView.LoadingStatusType.Ready;

        // go(prêt) quand le voile et le remplissage sont décodés, ou go(false) au bout de timeoutMs
        // (voile refait entre-temps, ou décodage trop long).
        void OnVeil(Action<bool> go, int timeoutMs)
        {
            ImageView v = veil, f = filler;
            if (v == null || VeilReady) { go(VeilReady); return; }
            bool done = false;
            var t = new Timer((uint)timeoutMs);
            void Go(bool ok)
            {
                if (done || closed) return;
                done = true;
                t.Stop();
                veilTimers.Remove(t);
                go(ok);
            }
            EventHandler<ImageView.ResourceReadyEventArgs> ready = (o, e) => { if (v == veil && VeilReady) guard("HeroVeilReady", () => Go(true)); };
            v.ResourceReady += ready;
            f.ResourceReady += ready;
            t.Tick += (o, e) => { guard("HeroVeilWait", () => Go(false)); return false; };
            veilTimers.Add(t);
            t.Start();
        }

        // go() quand le voile est décodé (ou au bout de 2 s) : la vidéo ne se montre pas avant
        // ses bords fondus. Refait d'abord s'il date d'un autre arrière-plan.
        public void WhenVideoReady(Action go)
        {
            if (veil == null || veilSrc != Source(out _)) PrepareVideo();
            int asked = Environment.TickCount;
            OnVeil(ok =>
            {
                Probe.Log("hero", $"voile {(ok ? "prêt" : "en retard")} : préparé {asked - veilAt} ms avant la vidéo, attente {Environment.TickCount - asked} ms");
                guard("HeroVeil", go);
            }, 2000);
        }

        // Image de la bannière remise (bande-annonce arrêtée sans changement de titre).
        public void Reveal()
        {
            if (!imagesHidden) return;
            imagesHidden = false;
            var a = new Animation(Theme.Fade);
            a.AnimateTo(images, "Opacity", 1f);
            a.Play();
            keep.Add(a);
            if (keep.Count > 4) keep.RemoveAt(0);
        }

        static string Source(out bool dark)
        {
            dark = false;
            string src = ArtLayer.Visible ? ArtLayer.Current(out dark) : null;
            if (src == null) { src = SolidPng(); dark = false; }
            return src;
        }

        static ImageView Cover(string src, bool dark)
        {
            var v = new ImageView(src) { Size = new Size(1920, 1080) };
            // Comme l'arrière-plan d'après l'image : repli assombri de 80 %.
            if (dark) { v.FittingMode = FittingModeType.ScaleToFill; v.Color = new Color(0.2f, 0.2f, 0.2f, 1f); }
            return v;
        }

        void DropVideo()
        {
            foreach (ImageView v in new[] { veil, filler }) if (v != null) { v.Unparent(); v.Dispose(); }
            veil = filler = null;
            veilSrc = null;
            shades.Opacity = 0;
        }

        // La vidéo se voit (on) : le voile et le remplissage (identiques à l'arrière-plan) sont
        // posés aussitôt, puis l'image et le remplissage s'effacent ensemble en 0,5 s ; l'inverse
        // à la fin. now : sans fondu (arrêt), le voile retiré (et préparé de nouveau pour la
        // prochaine) ; keepHidden : l'image reste cachée jusqu'au titre suivant (changement de
        // carte : sinon l'image de la carte d'avant reparaissait). done : à la fin du fondu.
        public void Video(bool on, Action done = null, bool now = false, bool keepHidden = false)
        {
            videoFade?.Stop();
            videoFade = null;
            // Préparé avec un autre arrière-plan (titre entré depuis) : refait.
            if (on && (veil == null || veilSrc != Source(out _))) PrepareVideo();
            if (now)
            {
                if (on) { images.Opacity = 0; shades.Opacity = 1; filler.Opacity = 0; }
                else
                {
                    bool wasShown = shades.Opacity > 0;
                    if (keepHidden && wasShown && images.Opacity < 1) imagesHidden = true;
                    else { images.Opacity = 1; imagesHidden = false; }
                    // Retiré ; un neuf caché pour la prochaine, sauf en changeant de carte (rien de
                    // lourd pendant le changement : le titre suivant le préparera, une fois complet).
                    if (wasShown) { if (keepHidden) DropVideo(); else PrepareVideo(); }
                }
                done?.Invoke();
                return;
            }
            if (on) { shades.Opacity = 1; filler.Opacity = 1; }
            // Image fixe en 0,7 s, remplissage (TrailerVeil du web) en 0,5 s.
            videoFade = new Animation(700);
            videoFade.AnimateTo(images, "Opacity", on ? 0f : 1f, 0, 700);
            if (filler != null) videoFade.AnimateTo(filler, "Opacity", on ? 0f : 1f, 0, 500);
            if (done != null) videoFade.Finished += (s, e) => { if (!closed) guard("HeroVideo", done); };
            videoFade.Play();
        }

        // ---- Suit la carte sélectionnée ----

        Timer follow; // gardé dans un champ, sinon il s'arrête
        Item[] neighbours = new Item[0];

        // Carte regardée dans les rangées : son titre est montré si le focus y reste 0,3 s.
        // left, right : cartes voisines, dont l'image est décodée d'avance.
        public void Follow(Item it, Item left, Item right)
        {
            if (closed || !Following || it == null) return;
            follow?.Stop();
            follow = new Timer(FollowMs);
            follow.Tick += (s, e) => { if (!closed) guard("HeroFollow", () => Pick(it, left, right)); return false; };
            follow.Start();
        }

        void Pick(Item it, Item left, Item right)
        {
            neighbours = new[] { right, left };
            if (it.Id != null && Current?.Id == it.Id)
            {
                Reveal(); // revenu sur la même carte : pas de nouveau titre pour remettre l'image
                foreach (Item n in neighbours) if (n != null && n.Id != it.Id) Prepare(n);
                ImageCache.Pump();
                return;
            }
            items.Clear();
            items.Add(it);
            index = 0;
            Apply();
        }

        // ---- Défilement ----

        // La bannière se voit et l'accueil est au premier plan : elle défile.
        public void Run(bool on)
        {
            if (running == on) return;
            running = on;
            Restart();
        }

        void Restart()
        {
            rotate?.Stop();
            rotate = null;
            if (!running || closed || items.Count < 2 || Following) return;
            rotate = new Timer((uint)(Prefs.HeroSeconds * 1000)); // réglage « Durée de chaque titre », relu à chaque fois
            rotate.Tick += (s, e) => { if (!closed) guard("HeroRotate", () => { rotate = null; Go(1, keepButton: true); Restart(); }); return false; };
            rotate.Start();
        }

        public void RefreshTexts()
        {
            if (front.Item != null) FillTexts(front, front.Item);
            PlaceButtons();
        }

        // Arrêtée et cachée, sa vue laissée en place (une animation de l'accueil peut encore la
        // déplacer) ; Close la détruit.
        public void Stop()
        {
            closed = true;
            rotate?.Stop();
            apply?.Stop();
            gate?.Stop();
            fade?.Stop();
            follow?.Stop();
            imageWait?.Stop();
            videoFade?.Stop();
            foreach (Timer t in veilTimers) t.Stop();
            veilTimers.Clear();
            Root.Hide();
        }

        public void Close()
        {
            Stop();
            Root.Unparent();
            Root.Dispose();
        }

        // ---- Masque de l'image ----
        // Transparence de l'image de fond (mise à l'échelle de l'image, CropToMask = false) :
        // bord gauche adouci sur 192 px, voile de côté (fond plein à gauche, 70 % à 35 % de la
        // largeur, rien à droite) et voile du bas (2/3 de la hauteur : fond plein en bas, 60 % au
        // milieu, rien en haut), comme Shade et BackdropFrame du web. Calculé une fois par lancement.

        // Un masque par hauteur d'image (image de imgH px, centrée sur la bannière) : ce qui passe
        // sous la bannière est transparent.
        // Suit la carte (image sur tout l'écran) : mêmes voiles, sur la hauteur de l'écran.
        // Dans le coin (hero-corner du web) : transparent à gauche, 40 % à 20 % de la largeur,
        // plein à 45 % ; plein jusqu'à 45 % de la hauteur de la zone, 40 % à 75 %, rien en bas.
        static readonly Dictionary<string, string> masks = new Dictionary<string, string>();

        string Mask(int imgH)
        {
            string key = kind + "-" + imgH;
            if (masks.TryGetValue(key, out string known) && File.Exists(known)) return known;
            const int W = 300;
            int MH = Math.Max(2, (int)Math.Round((double)W * imgH / frameW));
            int top = ImageTop(imgH);
            var px = new byte[W * MH * 4];
            for (int y = 0; y < MH; y++)
            {
                float py = top + (y + 0.5f) / MH * imgH; // hauteur dans la zone de l'image
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 4;
                    px[o] = px[o + 1] = px[o + 2] = 255;
                    px[o + 3] = Byte(Seen((x + 0.5f) / W * frameW, py));
                }
            }
            return WriteMask(key, W, MH, px);
        }

        // Part visible de l'image (ou de la vidéo) en un point de sa zone : lx depuis son bord
        // gauche, py depuis le haut de la zone.
        // - Fait défiler, et suit la carte (image sur tout l'écran) : bord gauche adouci, voile de
        //   côté et voile du bas (Shade et BackdropFrame du web) ;
        // - Dans le coin (hero-corner du web) : transparent à gauche, 40 % à 20 % de la largeur,
        //   plein à 45 % ; plein jusqu'à 45 % de la hauteur, 40 % à 75 %, rien en bas.
        float Seen(float lx, float py)
        {
            if (kind != Kind.Corner)
            {
                float sx = frameX + lx;
                float t = (py - boxH / 3f) / (boxH * 2f / 3f); // 0 en haut du voile, 1 en bas
                float bottom = t <= 0 ? 0 : t < 0.5f ? 0.6f * t / 0.5f : 0.6f + 0.4f * (t - 0.5f) / 0.5f;
                float edge = Math.Min(1f, lx / EdgeW);
                float side = sx <= 672 ? 1 - 0.3f * sx / 672 : 0.7f * (1 - (sx - 672) / (1920 - 672));
                return edge * (1 - side) * (1 - Math.Min(1f, bottom));
            }
            float v = py / boxH;
            float down = v < 0 ? 1 : v < 0.45f ? 1 : v < 0.75f ? 1 - 0.6f * (v - 0.45f) / 0.3f : v < 1 ? 0.4f * (1 - (v - 0.75f) / 0.25f) : 0;
            float u = lx / frameW;
            if (u < 0 || u > 1) return 0;
            float across = u < 0.2f ? 0.4f * u / 0.2f : u < 0.45f ? 0.4f + 0.6f * (u - 0.2f) / 0.25f : 1;
            return across * down;
        }

        static byte Byte(float a) => (byte)Math.Round(255 * Math.Max(0, Math.Min(1, a)));

        static string WriteMask(string key, int w, int h, byte[] px)
        {
            string d = Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.Cache, "wash-masks");
            Directory.CreateDirectory(d);
            string p = Path.Combine(d, "hero-" + key + ".png");
            Png.Write(p, w, h, 4, px);
            masks[key] = p;
            return p;
        }

        // Part visible de la vidéo en un point de sa boîte (x, y depuis son coin haut gauche), comme
        // le web : l'arrière-plan revient en fondu sur la moitié gauche et la moitié basse (trou
        // « soft » de preview.tsx), plus les voiles de HeroTrailer : gauche sur 40 % de la largeur
        // et bas sur 40 % de la hauteur (plein au bord, 60 % à 30 % de la bande, rien au bout),
        // haut sur 12 % (70 % au bord, rien au bout). Ici tous faits de l'arrière-plan lui-même.
        static float VideoSeen(float x, float y, float w, float h)
        {
            float left = Math.Max(0, 1 - x / (w * 0.5f)), down = Math.Max(0, 1 - (h - y) / (h * 0.5f));
            float seen = (1 - left) * (1 - down);
            seen *= 1 - Band(x / (w * 0.4f));
            seen *= 1 - Band((h - y) / (h * 0.4f));
            float top = y / (h * 0.12f);
            seen *= 1 - (top < 1 ? 0.7f * (1 - top) : 0);
            return seen;
        }

        // Voile d'une bande : plein au bord (t = 0), 60 % à 30 %, rien au bout (t = 1).
        static float Band(float t) => t < 0 ? 1 : t < 0.3f ? 1 - 0.4f * t / 0.3f : t < 1 ? 0.6f * (1 - (t - 0.3f) / 0.7f) : 0;

        // Voile plein écran posé sur la vidéo : opaque hors de sa boîte, et l'inverse de la part
        // visible dedans (la vidéo se fond dans l'arrière-plan).
        string VeilMask()
        {
            string key = "veil-" + kind;
            if (masks.TryGetValue(key, out string known) && File.Exists(known)) return known;
            const int W = 384, MH = 216;
            Rectangle r = VideoRect;
            var px = new byte[W * MH * 4];
            for (int y = 0; y < MH; y++)
            {
                float sy = (y + 0.5f) / MH * 1080;
                for (int x = 0; x < W; x++)
                {
                    float sx = (x + 0.5f) / W * 1920;
                    float a = sx < r.X || sx >= r.X + r.Width || sy >= r.Y + r.Height ? 1 : 1 - VideoSeen(sx - r.X, sy - r.Y, r.Width, r.Height);
                    int o = (y * W + x) * 4;
                    px[o] = px[o + 1] = px[o + 2] = 255;
                    px[o + 3] = Byte(a);
                }
            }
            return WriteMask(key, W, MH, px);
        }

        // Petite image unie de la couleur de fond (sans arrière-plan d'après l'image).
        static string SolidPng()
        {
            Color c = Theme.Background;
            string key = "solid-" + Byte(c.R) + "-" + Byte(c.G) + "-" + Byte(c.B);
            if (masks.TryGetValue(key, out string known) && File.Exists(known)) return known;
            var px = new byte[4 * 4 * 4];
            for (int i = 0; i < 16; i++) { px[i * 4] = Byte(c.R); px[i * 4 + 1] = Byte(c.G); px[i * 4 + 2] = Byte(c.B); px[i * 4 + 3] = 255; }
            return WriteMask(key, 4, 4, px);
        }
    }
}
