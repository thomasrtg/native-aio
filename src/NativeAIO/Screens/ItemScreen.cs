// Page d'un titre, reprise de la version web (pages/item.tsx) :
// - en haut : image de fond assombrie à gauche et en bas, affiche, logo (sinon titre
//   écrit), ligne d'informations, prochaine diffusion, genres, résumé, boutons
//   Lecture (ou Reprendre), Depuis le début, Vu, Favori ;
// - en dessous, parcourues avec Haut/Bas : saisons et épisodes (série), titres de la
//   collection, distribution, équipe technique, détails, titres similaires ;
// - OK sur Lecture ou sur un épisode ouvre la liste des versions (VersionsScreen).
// Mêmes règles que l'accueil : rien de lourd pendant une touche. Les vues des cartes,
// leurs textes et leurs demandes d'images sont faits au tour de boucle suivant, un
// morceau à la fois, d'abord près de la rangée regardée (BuildStep).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Screens
{
    class ItemScreen : Screen
    {
        // Mise en page compacte, comme la version web : le haut, les saisons, les épisodes
        // et le début de la distribution tiennent dans le premier écran (demande de l'auteur).
        // Le haut est posé dans « header », placé ensuite pour que ses boutons finissent à ButtonsBottom.
        // Couleurs, tailles, arrondis et contours : ceux de la charte (Ui/Theme.cs, module 14).
        const int Left = Theme.Margin, Top = 0, PosterW = 230, PosterH = 345, ColX = Left + PosterW + 40, ColW = 1000;
        const int LogoW = 440, LogoH = 120, OverviewLines = 2, OverviewLineH = 31, BtnH = Theme.ButtonHeight, ButtonsBottom = 460;
        // Titres de rangées comme ceux de l'accueil (choix de l'auteur, module 14) : 33 pt demi-gras,
        // dans une boîte de 60 px, cartes 12 px plus bas.
        const int SectionTitleH = 72, SectionLabelH = 60, SectionGap = 24, ScrollMargin = 40;
        const float SectionSize = 33;
        const int ScrollMs = 350, SlideMs = 250, SeasonDelayMs = 500;
        const string Muted = "#9CA3AF"; // Theme.Grey, pour le balisage des textes

        enum Kind { Buttons, Seasons, Episodes, Posters, People }

        // Une rangée de la page, avec ses cartes (ou les boutons du haut).
        class Section
        {
            public Kind Kind;
            public View Root;
            public TextLabel Title, Message;
            public int Y, Height;          // place dans la page
            public int ShowTop, ShowBottom; // ce qui doit être visible quand elle est regardée
            public int TrackY;
            public View Track;
            public Animation Slide;         // gardée ici, sinon elle s'arrête
            public readonly List<Tile> Tiles = new List<Tile>();
            public int Col, First;
            public int TileW, ImageH, TextH, Step, Visible;
            public bool Focusable => Kind == Kind.Buttons || Tiles.Count > 0;
        }

        class Tile
        {
            public Section Sec;
            public int Index;
            public Item Item;     // épisode, saison, titre
            public Person Person; // distribution, équipe technique
            public View Root, Ring, Frame;
            public TextLabel Text, Label;
            public string Url;
            public List<string> Urls; // images possibles, essayées dans l'ordre (épisodes)
            public bool Built, Texted, Requested, Dead;
        }

        // Un bouton du haut.
        class Btn
        {
            public View Root, Ring, Box;
            public TextLabel Label;
            public int Width, RingW;
            public Func<string> Text;
            public Func<bool> Shown;
            public Action Run;
            public Func<string> Hint;   // icône : son nom, montré dessous quand elle est regardée
            public Func<bool> Active;   // icône : état allumé (vu, favori)
            public bool Heart;
            public Func<string> Icon;  // icône devant le texte (Lecture)
            public ImageView Image;
            public string ImageName;
        }

        readonly string id, seasonWanted, episodeWanted;
        Item item, seed, target, nextUp;
        Item artItem; // titre de l'arrière-plan d'après l'image
        Item[] seasons;
        string seasonId;
        int seasonGen;

        View root, page, backLayer, header, buttonsView;
        bool art; // arrière-plan d'après l'image
        TextLabel title, meta, airing, overview, note, hint;
        View genres;
        readonly List<Btn> buttons = new List<Btn>();
        int buttonCol;
        readonly List<Section> sections = new List<Section>(); // dans l'ordre de la page
        Section btnSection, seasonSec, episodeSec, similarSec;
        TextLabel seasonSummary;
        int focus, scroll, pageHeight;
        Animation scrollAnim;
        Timer builder, seasonTimer; // gardés dans des champs, sinon ils s'arrêtent
        readonly Queue<View> toDrop = new Queue<View>();
        bool similarAsked, busy;
        Guarded guard;

        // Mesures (panneau, touche rouge).
        readonly Stopwatch clock = Stopwatch.StartNew();
        string tItem = "-", tSeasons = "-", tEpisodes = "-", tSimilar = "-";
        int imagesAsked, imagesShown;

        ItemScreen(string id, Item seed, string seasonId, string episodeId)
        {
            this.id = id;
            this.seed = seed;
            seasonWanted = seasonId;
            episodeWanted = episodeId;
        }

        // Un épisode ouvre la page de sa série, sur sa saison et sur lui (comme la version web).
        public static ItemScreen For(Item it)
        {
            if (it.Type == "Episode" && it.SeriesId != null) return new ItemScreen(it.SeriesId, null, it.SeasonId, it.Id);
            return new ItemScreen(it.Id, it, null, null);
        }

        protected override View Build()
        {
            root = new View { Size = new Size(1920, 1080), BackgroundColor = Theme.ScreenBack(Theme.Background) };
            guard = new Guarded(root);
            page = new View { Size = new Size(1920, 1080) };
            root.Add(page);

            // Image de fond, assombrie à gauche et en bas (deux dégradés en ressource).
            backLayer = new View { Size = new Size(1920, 1080) };
            page.Add(backLayer);
            // Dégradés natifs de NUI (GradientVisual), continus. Avant : une PNG étirée
            // (bord net sur la TV), puis 40 bandes d'opacité (paliers visibles, vu par l'auteur).
            // Avec l'arrière-plan d'après l'image (réglage du thème) : pas de voiles, l'image de
            // fond devient transparente à gauche et en bas (masque, Ui/ArtWash.cs) au-dessus de
            // l'arrière-plan sombre de l'application (Ui/ArtLayer.cs).
            art = ArtWash.On;
            if (!art)
            {
                backLayer.Add(Shade(new Position(0, 0), new Size(1300, 1080), true, t => 0.92f * (1 - t) * (1 - t)));
                backLayer.Add(Shade(new Position(0, 380), new Size(1920, 700), false, t => (float)Math.Pow(t, 1.6)));
            }

            header = new View { Size = new Size(1920, 1080), PositionY = 90 };
            page.Add(header);
            title = new TextLabel
            {
                Position = new Position(ColX, Top),
                Size = new Size(ColW, LogoH),
                PointSize = Theme.SizeHuge,
                MultiLine = true,
                TextColor = Color.White,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            title.Hide();
            header.Add(title);
            // Informations en 22 pt et résumé en 20 pt : même exception que l'accueil et la bannière
            // (charte, choix de l'auteur, module 14).
            meta = new TextLabel { PointSize = 22, TextColor = Theme.Text, Size = new Size(ColW, 40) };
            airing = new TextLabel { PointSize = Theme.SizeInfo, Size = new Size(ColW, 36), EnableMarkup = true, TextColor = Color.White };
            overview = new TextLabel { PointSize = 20, MultiLine = true, Size = new Size(ColW, OverviewLineH * OverviewLines), TextColor = Theme.Text };
            note = new TextLabel { PointSize = Theme.SizeInfo, Size = new Size(ColW, 36), TextColor = Theme.Grey };
            genres = new View();
            buttonsView = new View();
            // Nom de l'icône regardée : bulle comme celle de la barre latérale.
            hint = new TextLabel { PointSize = Theme.SizeBody, TextColor = Color.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, BackgroundColor = Theme.Panel }.Rounded(Theme.RadiusLarge);
            hint.Hide();
            buttonsView.Add(hint);
            foreach (View v in new View[] { meta, airing, genres, overview, buttonsView, note }) { v.Hide(); header.Add(v); }

            btnSection = new Section { Kind = Kind.Buttons };
            sections.Add(btnSection);
            // Lecture en texte ; les autres en icônes rondes, leur nom affiché dessous quand
            // elles sont regardées (comme l'infobulle de la version web).
            // Icônes de la version web (Boxicons, react-icons/bi), en PNG dans res/icons.
            AddButton(PlayText, () => target != null, () => OpenVersions(target, Resume(target)), icon: () => "play");
            // Bandes-annonces (comme TrailerButton du web) : une seule se lance tout de suite,
            // plusieurs ouvrent la liste.
            AddButton(() => Lang.T(TrailersOf(item).Count > 1 ? "item.trailers" : "item.trailer"), () => TrailersOf(item).Count > 0, OpenTrailers, icon: () => "trailer");
            AddButton(() => "restart", () => target != null && Resume(target) > 0, () => OpenVersions(target, 0),
                () => Lang.T("item.restart"));
            AddButton(() => "check", () => item != null && item.Type != "BoxSet", TogglePlayed,
                () => item?.UserData?.Played == true ? Lang.T("item.mark_unwatched") : Lang.T("item.mark_watched"),
                () => item?.UserData?.Played == true);
            AddButton(() => item?.UserData?.IsFavorite == true ? "heart_solid" : "heart",
                () => item != null && (item.UserData?.IsFavorite == true || item.Type == "Movie" || item.Type == "Series"), ToggleFavorite,
                () => item?.UserData?.IsFavorite == true ? Lang.T("item.remove_favourite") : Lang.T("item.add_favourite"),
                () => item?.UserData?.IsFavorite == true, true);

            // Ce que l'accueil savait déjà : les images arrivent pendant que la fiche est demandée.
            if (seed != null) RequestHeaderImages(seed);
            cardMenu = new CardMenu(this, Guard) { Changed = MenuDone };
            root.Add(cardMenu.View); // menu d'une carte, au-dessus de tout
            Load();
            return root;
        }

        public override void ApplyTexts()
        {
            foreach (Btn b in buttons) b.Label.Text = b.Text();
            if (item != null) Guard("Texts", () => { meta.Text = MetaLine(item); });
            foreach (Section s in sections)
            {
                if (s.Title != null) s.Title.Text = SectionTitle(s);
            }
            Note();
        }

        public override void OnShow()
        {
            Note();
            ArtLayer.Show(artItem);
            Guard("AfterPlay", AfterPlay);
        }

        // Retour du lecteur : position et « vu » mis à jour sur place, sans redemander la fiche.
        void AfterPlay()
        {
            string id = Playback.LastItemId;
            if (id == null) return;
            Playback.LastItemId = null;
            if (Playback.Chained && Playback.LastItem != null)
            {
                Playback.Chained = false;
                AfterChain(Playback.LastItem);
                return;
            }
            bool changed = false;
            foreach (Item it in new[] { item, target })
            {
                if (it == null || it.Id != id) continue;
                if (it.UserData == null) it.UserData = new UserItemData();
                it.UserData.PlaybackPositionTicks = Playback.LastPositionTicks;
                if (Playback.LastPlayed) it.UserData.Played = true;
                changed = true;
            }
            if (changed) RefreshButtons();
        }

        // Épisodes enchaînés dans le lecteur (module 7) : la saison du dernier épisode lu est
        // rechargée avec le focus sur lui, et Lecture reprend où la série en est. Attendu d'abord :
        // l'arrêt envoyé au serveur (2 s au plus), sinon la saison arriverait sans le « vu ».
        async void AfterChain(Item last)
        {
            if (item == null || item.Type != "Series" || seasons == null || last.SeasonId == null) return;
            await Task.WhenAny(Playback.LastStop, Task.Delay(2000));
            if (Closed) return;
            Guard("AfterChain", () =>
            {
                for (int i = 0; i < seasons.Length; i++)
                    if (seasons[i].Id == last.SeasonId) { seasonSec.Col = i; ScrollTrack(seasonSec, false); }
                seasonId = null;
            });
            SelectSeason(last.SeasonId, last.Id);
            Item next = null;
            try { next = await Library.NextUp(item.Id); }
            catch (Exception) { }
            if (Closed) return;
            if (next != null) Guard("NextUp", () => { target = next; RefreshButtons(); });
        }

        public override void OnHide() { Measure.Note = null; cardMenu?.Hide(); }
        public override bool OnBack() => cardMenu.Back();
        public override bool OnKeyUp(string key) => cardMenu.KeyUp(key);

        // Menu d'une carte (Ui/CardMenu.cs) : épisodes et saisons (« sur la page », sans Ouvrir),
        // titres similaires et de la collection. Après un envoi, la carte est remise à jour ; les
        // épisodes de la saison affichée sont redemandés si une saison a changé ou après « vu
        // jusqu'ici ».
        CardMenu cardMenu;

        void MenuDone(Item it)
        {
            bool reload = false;
            foreach (Section s in sections)
                foreach (Tile t in s.Tiles)
                {
                    if (t.Item != it || !t.Built) continue;
                    if (s.Kind == Kind.Seasons)
                    {
                        if (t.Label != null) t.Label.Text = (it.Name ?? "") + (it.UserData?.Played == true ? "  ✓" : "");
                        reload |= it.Id == seasonId;
                    }
                    else if (s.Kind == Kind.Episodes && cardMenu.Wide) reload = true;
                    else if (t.Frame != null)
                    {
                        Section sec = s;
                        CardMenu.SetCheck(t.Frame, it.UserData?.Played == true, () => Badge("✓", sec.TileW - 52, 12, 40));
                        if (s.Kind == Kind.Episodes)
                        {
                            var bars = new List<View>();
                            for (uint i = 0; i < t.Frame.ChildCount; i++)
                                if (t.Frame.GetChildAt(i).Name == "progress") bars.Add(t.Frame.GetChildAt(i));
                            foreach (View b in bars) { b.Unparent(); b.Dispose(); }
                            AddProgress(t);
                        }
                    }
                }
            if (!reload || seasonId == null) return;
            string sid = seasonId;
            seasonId = null;
            SelectSeason(sid, it.Type == "Episode" ? it.Id : null);
        }
        public override bool ArtBackdrop => true;

        public override void OnClose()
        {
            foreach (Section s in sections) foreach (Tile t in s.Tiles) t.Dead = true;
            builder?.Stop();
            seasonTimer?.Stop();
            guard.Stop();
            Measure.Note = null;
        }

        void Guard(string where, Action run) => guard.Run(where, run);

        // Voile sombre en dégradé (Ui/Gradient.cs, dessiné par la TV).
        static View Shade(Position at, Size size, bool horizontal, Func<float, float> alpha) =>
            Gradient.Make(at, size, horizontal, alpha, 0.04f);

        // ---- Chargement ----

        async void Load()
        {
            SetNote(Lang.T("item.loading"));
            note.Position = new Position(ColX, Top + LogoH + 40);
            note.Show();
            Item it;
            try { it = await Library.Full(id); }
            catch (ApiError e) { if (!Closed) SetNote(SignInScreen.ErrorText(e)); return; }
            catch (Exception) { if (!Closed) SetNote(Lang.T("error.unknown")); return; }
            if (Closed) return;
            if (it == null) { SetNote(Lang.T("item.not_found")); return; }
            item = it;
            tItem = Seconds();
            Guard("Header", () =>
            {
                SetNote("");
                if (seed == null) RequestHeaderImages(item);
                if (item.Type == "Movie") target = item;
                LayoutHeader();
                LayoutSections();
                SetFocus(0);
            });
            if (item.Type == "Series") LoadShow();
            if (item.Type == "BoxSet") LoadMembers();
        }

        async void LoadShow()
        {
            var nextTask = Library.NextUp(item.Id);
            ItemsResult r;
            try { r = await Library.Seasons(item.Id); }
            catch (Exception) { if (!Closed) SetMessage(seasonSec, Lang.T("item.load_failed")); return; }
            if (Closed) return;
            seasons = r?.Items ?? new Item[0];
            tSeasons = Seconds();
            string open = null;
            foreach (Item s in seasons) if (s.Id == seasonWanted) open = s.Id;
            if (open == null) open = Library.OpeningSeason(seasons)?.Id;
            Guard("Seasons", () =>
            {
                if (seasons.Length == 0) { SetMessage(seasonSec, Lang.T("item.no_episodes")); return; }
                SetMessage(seasonSec, null);
                FillTiles(seasonSec, new List<Item>(seasons));
                int col = 0;
                for (int i = 0; i < seasons.Length; i++) if (seasons[i].Id == open) col = i;
                seasonSec.Col = col;
                ScrollTrack(seasonSec, false);
                PaintSeasons();
                EnsureBuilder();
            });
            SelectSeason(open, episodeWanted);
            try { nextUp = await nextTask; }
            catch (Exception) { }
            if (Closed) return;
            if (nextUp != null) Guard("NextUp", () => { target = nextUp; RefreshButtons(); });
            LoadAiring();
        }

        // Prochaine diffusion d'une série en cours : dans la dernière saison.
        async void LoadAiring()
        {
            if (item.Status != "Continuing" || seasons == null) return;
            Item latest = null;
            foreach (Item s in seasons)
                if ((s.IndexNumber ?? 0) > 0 && (latest == null || s.IndexNumber > latest.IndexNumber)) latest = s;
            if (latest == null) return;
            ItemsResult r;
            try { r = await Library.Episodes(item.Id, latest.Id); }
            catch (Exception) { return; }
            if (Closed || r?.Items == null) return;
            foreach (Item e in r.Items)
            {
                DateTime? d = Library.Date(e.PremiereDate);
                if (d == null || d.Value <= DateTime.UtcNow) continue;
                Guard("Airing", () =>
                {
                    airing.Text = Lang.T("item.next_airing", Esc(Library.Code(e) ?? ""), Library.Until(d.Value), "<color value='" + Muted + "'>" + Library.DayDate(d.Value) + "</color>");
                });
                return;
            }
        }

        // Saison choisie : ses épisodes remplacent ceux d'avant.
        async void SelectSeason(string sid, string focusEpisode)
        {
            if (sid == null || sid == seasonId) return;
            seasonId = sid;
            int gen = ++seasonGen;
            Guard("Season", () =>
            {
                PaintSeasons();
                ClearTiles(episodeSec);
                seasonSummary.Text = "";
                SetMessage(episodeSec, Lang.T("home.row_loading"));
            });
            ItemsResult r;
            try { r = await Library.Episodes(item.Id, sid); }
            catch (Exception) { if (!Closed && gen == seasonGen) SetMessage(episodeSec, Lang.T("item.load_failed")); return; }
            if (Closed || gen != seasonGen) return;
            if (tEpisodes == "-") tEpisodes = Seconds();
            var list = new List<Item>(r?.Items ?? new Item[0]);
            Guard("Episodes", () =>
            {
                SetMessage(episodeSec, list.Count == 0 ? Lang.T("item.no_episodes") : null);
                seasonSummary.Text = SeasonSummary(list);
                FillTiles(episodeSec, list);
                int col = Library.UpTo(list);
                if (focusEpisode != null) for (int i = 0; i < list.Count; i++) if (list[i].Id == focusEpisode) col = i;
                episodeSec.Col = col;
                ScrollTrack(episodeSec, false);
                // Série sans épisode suivant (tout est vu) : Lecture prend le premier épisode montré.
                if (target == null && list.Count > 0) { target = list[col]; RefreshButtons(); }
                if (focusEpisode != null && sections[focus] == btnSection) SetFocus(sections.IndexOf(episodeSec));
                else PaintFocus();
                EnsureBuilder();
            });
        }

        async void LoadMembers()
        {
            ItemsResult r;
            try { r = await Library.Members(item.Id); }
            catch (Exception) { if (!Closed) SetMessage(episodeSec, Lang.T("item.load_failed")); return; }
            if (Closed) return;
            var list = new List<Item>(r?.Items ?? new Item[0]);
            Guard("Members", () =>
            {
                SetMessage(episodeSec, list.Count == 0 ? Lang.T("home.row_empty") : null);
                FillTiles(episodeSec, list);
                PaintFocus();
                EnsureBuilder();
            });
        }

        // « Titres similaires » : demandés quand on descend dans la page (le serveur lit un catalogue).
        async void LoadSimilar()
        {
            if (similarAsked || similarSec == null) return;
            similarAsked = true;
            long t0 = clock.ElapsedMilliseconds;
            ItemsResult r;
            try { r = await Library.Similar(item.Id); }
            catch (Exception) { if (!Closed) SetMessage(similarSec, Lang.T("item.load_failed")); return; }
            if (Closed) return;
            tSimilar = ((clock.ElapsedMilliseconds - t0) / 1000.0).ToString("0.0"); // durée de la demande
            var list = new List<Item>(r?.Items ?? new Item[0]);
            Guard("Similar", () =>
            {
                if (list.Count == 0) { similarSec.Root.Hide(); return; }
                SetMessage(similarSec, null);
                FillTiles(similarSec, list);
                PaintFocus();
                EnsureBuilder();
            });
        }

        string Seconds() => (clock.ElapsedMilliseconds / 1000.0).ToString("0.0");

        // ---- Haut de la page ----

        void RequestHeaderImages(Item it)
        {
            // Arrière-plan d'après l'image : sous la page, il ne défile pas avec elle. Posé aussi
            // à chaque retour sur la page (OnShow : la liste des versions montre celui de l'épisode).
            artItem = it;
            if (!Closed && Nav?.Top == this) ArtLayer.Show(it);
            string back = Library.Backdrop(it, 1920) ?? Library.Landscape(it, 1920);
            string poster = Library.Poster(it, PosterW);
            string logo = Library.Logo(it, LogoW);
            if (logo == null) ShowTitle(it);
            Want(back, 0, path => Guard("Backdrop", () => ShowBackdrop(path)));
            Want(poster, 1, path => Guard("Poster", () => ShowPoster(path)));
            Want(logo, 0, path => Guard("Logo", () => ShowLogo(path, it)));
        }

        void Want(string url, int priority, Action<string> done)
        {
            if (url == null) return;
            imagesAsked++;
            ImageCache.Request(url, () => Closed ? int.MaxValue : priority, done);
        }

        void ShowBackdrop(string path)
        {
            if (Closed || path == null) return;
            View v = Pictures.Cover(path, 1920, 1080, out ImageView img);
            if (v == null) return;
            if (art)
            {
                // Transparente à gauche et en bas sur l'arrière-plan d'après l'image (masque mis à
                // l'échelle de l'image).
                img.CropToMask = false;
                img.AlphaMaskURL = ArtWash.BackdropMask();
            }
            backLayer.Add(v);
            v.LowerToBottom();
            Fade(img);
        }

        void ShowPoster(string path)
        {
            if (Closed || path == null) return;
            View v = Pictures.Cover(path, PosterW, PosterH, out ImageView img);
            if (v == null) return;
            img.Rounded(Theme.RadiusLarge); // grande image (charte)
            v.Position = new Position(Left, Top);
            header.Add(v);
            Fade(img);
        }

        void ShowLogo(string path, Item it)
        {
            if (Closed) return;
            if (path == null) { ShowTitle(it); return; }
            Pictures.ImageSize(path, out int nw, out int nh);
            if (nw <= 0 || nh <= 0) { ShowTitle(it); return; }
            float k = Math.Min(1f, Math.Min((float)LogoW / nw, (float)LogoH / nh));
            int w = Math.Max(1, (int)(nw * k)), h = Math.Max(1, (int)(nh * k));
            ImageView logo = Pictures.Decoded(path, w, h);
            if (logo == null) { ShowTitle(it); return; }
            logo.Position = new Position(ColX, Top + LogoH - h);
            header.Add(logo);
            Fade(logo);
        }

        void ShowTitle(Item it)
        {
            title.Text = it.Name ?? "";
            title.Show();
        }

        // Image qui apparaît en fondu une fois prête.
        void Fade(ImageView img)
        {
            if (img == null) return;
            // Image déjà prête (même fichier qu'une autre vignette, gardé en mémoire par la
            // TV) : le signal « prête » est déjà passé quand la vignette a été posée. Sans
            // ce cas, elle restait transparente pour toujours (vu le 2026-11-11 : épisodes
            // qui partagent l'image de la série).
            if (img.LoadingStatus == ImageView.LoadingStatusType.Ready) { imagesShown++; return; }
            img.Opacity = 0;
            img.ResourceReady += (s, e) =>
            {
                if (Closed) return;
                imagesShown++;
                var a = new Animation(Theme.Fade);
                a.AnimateTo(img, "Opacity", 1f);
                a.Play();
                fades.Add(a);
                if (fades.Count > 8) fades.RemoveAt(0);
            };
        }
        readonly List<Animation> fades = new List<Animation>(); // gardées, sinon elles s'arrêtent

        // Informations, prochaine diffusion, genres, résumé et boutons, les uns sous les autres.
        void LayoutHeader()
        {
            int y = Top + LogoH + 14;
            meta.Text = MetaLine(item);
            meta.Position = new Position(ColX, y);
            meta.Show();
            y += 44;
            if (item.Type == "Series" && item.Status == "Continuing")
            {
                airing.Position = new Position(ColX, y);
                airing.Show();
                y += 38;
            }
            if (item.Genres != null && item.Genres.Length > 0)
            {
                int x = 0;
                for (int i = 0; i < item.Genres.Length && i < 4; i++)
                {
                    // Pastille d'information sur l'image (charte) : blanc à 16 %, 18 pt, 44 px.
                    // Largeur estimée : 10 px par caractère à 18 pt (9 à 9,5 mesurés à la sonde).
                    const int h = Theme.PillHeight;
                    int w = 48 + item.Genres[i].Length * 10;
                    var chip = new View { Position = new Position(x, 0), Size = new Size(w, h), BackgroundColor = Theme.Marked }.Rounded(h / 2);
                    chip.Add(new TextLabel { Size = new Size(w, h), PointSize = Theme.SizeBody, Text = item.Genres[i], TextColor = Color.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                    genres.Add(chip);
                    x += w + 12;
                }
                genres.Position = new Position(ColX, y);
                genres.Show();
                y += Theme.PillHeight + 14;
            }
            if (!string.IsNullOrEmpty(item.Overview))
            {
                overview.Position = new Position(ColX, y);
                int lines = SetOverview(item.Overview);
                overview.Show();
                y += lines * OverviewLineH + 16;
            }
            buttonsView.Position = new Position(ColX, y);
            buttonsView.Show();
            RefreshButtons();
            y += BtnH;
            // Tout le haut glisse pour que les boutons finissent à ButtonsBottom.
            int offset = Math.Max(30, ButtonsBottom - Math.Max(y, Top + PosterH));
            header.PositionY = offset;
            note.Show();
            btnSection.Y = 0;
            btnSection.Height = offset + Math.Max(y, Top + PosterH) + 56; // place du nom de l'icône regardée
            btnSection.ShowTop = 0;
            btnSection.ShowBottom = btnSection.Height;
        }

        // Le résumé tient en OverviewLines lignes ; sinon il est coupé à la fin d'un mot
        // et se termine par « … » (même méthode que l'accueil). Renvoie le nombre de lignes.
        int SetOverview(string text)
        {
            overview.Text = text;
            int lines = overview.LineCount;
            if (lines <= OverviewLines) return Math.Max(1, lines);
            int len = (int)(text.Length * (double)OverviewLines / lines);
            for (int tries = 0; tries < 6 && len > 1; tries++)
            {
                overview.Text = Pictures.Cut(text, len);
                if (overview.LineCount <= OverviewLines) break;
                len = (int)(len * 0.92);
            }
            return OverviewLines;
        }

        string MetaLine(Item it)
        {
            var parts = new List<string>();
            int? start = it.ProductionYear;
            if (it.Type == "Series" && start != null)
            {
                int? end = Library.Date(it.EndDate)?.Year;
                parts.Add(it.Status == "Continuing" ? start + "–" : end != null && end != start ? start + "–" + end : start.ToString());
            }
            else if (start != null) parts.Add(start.ToString());
            if (!string.IsNullOrEmpty(it.OfficialRating)) parts.Add(it.OfficialRating);
            if (it.Type != "Series" && it.RunTimeTicks > 0)
            {
                string d = Library.Duration(it.RunTimeTicks.Value);
                if (d != null) parts.Add(d);
            }
            string score = Library.Score(it);
            if (score != null) parts.Add(score);
            if (it.Type == "Series" && it.Status != null)
                parts.Add(it.Status == "Continuing" ? Lang.T("item.continuing") : it.Status == "Ended" ? Lang.T("item.ended") : it.Status);
            return string.Join("   ·   ", parts);
        }

        void SetNote(string text) => note.Text = text ?? "";

        // ---- Boutons ----

        static long Resume(Item it) => it?.UserData?.Played == true ? 0 : it?.UserData?.PlaybackPositionTicks ?? 0;

        string PlayText()
        {
            if (target == null) return Lang.T("item.play");
            long pos = Resume(target);
            string code = target.Type == "Episode" ? Library.Code(target) : null;
            if (pos > 0) return code != null ? Lang.T("item.resume_episode", code) : Lang.T("item.resume_at", Library.Clock(pos));
            return code != null ? Lang.T("item.play_episode", code) : Lang.T("item.play");
        }

        // Bouton à icône seule (hint donné : « text » est le nom de l'icône), ou à texte,
        // avec une icône devant (icon).
        void AddButton(Func<string> text, Func<bool> shown, Action run, Func<string> hint = null, Func<bool> active = null, bool heart = false, Func<string> icon = null)
        {
            int width = BtnH;
            var b = new Btn { Text = text, Shown = shown, Run = run, Width = width, Hint = hint, Active = active, Heart = heart, Icon = icon };
            b.Root = new View();
            b.Box = new View { Size = new Size(width, BtnH) }.Rounded(BtnH / 2);
            b.Label = new TextLabel { Size = new Size(width, BtnH), PointSize = Theme.SizeBody, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = text() };
            b.Box.Add(b.Label);
            b.Root.Add(b.Box);
            b.Root.Hide();
            buttonsView.Add(b.Root);
            buttons.Add(b);
        }

        // Boutons montrés selon l'état (reprise, vu, favori), placés les uns après les autres.
        void RefreshButtons()
        {
            // Réglage « Trouver des versions quand un titre s'ouvre » : celles du titre que Lecture
            // lancerait, demandées tout de suite (une seule fois par titre pendant 3 min).
            if (target != null && Prefs.PreloadVersions) Library.Preload(target.Id);
            int x = 0;
            foreach (Btn b in buttons)
            {
                if (!b.Shown()) { b.Root.Hide(); continue; }
                string text = b.Text();
                const int iconSize = 26;
                string iconName = b.Hint != null ? text : b.Icon?.Invoke();
                if (b.Hint != null) text = "";
                b.Label.Text = text;
                // Icône ronde, ou largeur suivant le texte (estimée : 13 px par caractère à 18 pt).
                int iconW = b.Icon != null ? 34 : 0;
                b.Width = b.Hint != null ? BtnH : Math.Max(170, 60 + iconW + text.Length * 13);
                b.Box.Size = new Size(b.Width, BtnH);
                b.Label.Size = new Size(b.Width - iconW, BtnH);
                b.Label.PositionX = iconW;
                if (iconName != null && iconName != b.ImageName)
                {
                    b.Image?.Unparent();
                    b.Image?.Dispose();
                    b.ImageName = iconName;
                    b.Image = new ImageView(IconPath(iconName)) { Size = new Size(iconSize, iconSize) };
                    b.Box.Add(b.Image);
                }
                if (b.Image != null)
                {
                    // Seule, centrée ; devant un texte, à gauche du texte centré dans le reste.
                    int tw = Math.Min(b.Width - iconW, 24 + text.Length * 13);
                    int ix = b.Hint != null ? (BtnH - iconSize) / 2 : iconW + (b.Width - iconW - tw) / 2 - 8;
                    b.Image.Position = new Position(ix, (BtnH - iconSize) / 2);
                }
                // Contour de focus de la charte (boutons blancs seulement), refait si la largeur change.
                if (b.Hint == null && b.RingW != b.Width)
                {
                    bool was = b.Ring != null && b.Ring.Visibility;
                    b.Ring?.Unparent();
                    b.Ring?.Dispose();
                    b.Ring = Theme.FocusRing(b.Width, BtnH, BtnH / 2);
                    b.Ring.Position = new Position(-Theme.RingOut, -Theme.RingOut);
                    b.Root.Add(b.Ring);
                    b.Ring.LowerToBottom();
                    if (!was) b.Ring.Hide();
                    b.RingW = b.Width;
                }
                b.Root.Position = new Position(x, 0);
                b.Root.Show();
                x += b.Width + Theme.Gap;
            }
            // Messages (enregistrement, erreur) à droite des boutons.
            note.Position = new Position(ColX + x + 12, (int)buttonsView.PositionY + 14);
            var shown = ShownButtons();
            if (buttonCol >= shown.Count) buttonCol = Math.Max(0, shown.Count - 1);
            PaintButtons();
        }

        static string IconPath(string name) =>
            Tizen.Applications.Application.Current.DirectoryInfo.Resource + "icons/" + name + ".png";

        List<Btn> ShownButtons()
        {
            var l = new List<Btn>();
            foreach (Btn b in buttons) if (b.Shown()) l.Add(b);
            return l;
        }

        void PaintButtons()
        {
            bool hintShown = false;
            var shown = ShownButtons();
            bool active = sections.Count > 0 && sections[focus] == btnSection;
            for (int i = 0; i < shown.Count; i++)
            {
                Btn b = shown[i];
                bool on = active && i == buttonCol;
                // Lecture et Bandes-annonces : toujours sur fond blanc, texte et icône foncés
                // (demande de l'auteur) ; regardés : contour blanc et liseré sombre (charte).
                bool play = b == buttons[0] || b == buttons[1];
                bool lit = b.Active?.Invoke() == true;
                if (play)
                {
                    b.Box.BackgroundColor = Color.White;
                    b.Label.TextColor = Theme.Ink;
                    if (b.Ring != null) { if (on) b.Ring.Show(); else b.Ring.Hide(); }
                    continue;
                }
                // Icônes rondes (charte) : blanc à 16 % au repos (posées sur l'image comme les genres ;
                // à 8 %, presque invisibles sur une image claire, capture du 2026-11-18) ; regardée : fond blanc, icône
                // foncée (icône blanche teintée, le cœur plein reste rouge) ; Vu allumé : accent du thème.
                b.Box.BackgroundColor = on ? Color.White : lit && !b.Heart ? Theme.Accent : Theme.Marked;
                if (b.Image != null)
                    b.Image.Color = b.ImageName == "heart_solid" ? Color.White : on ? Theme.Ink : lit && !b.Heart ? Theme.OnAccent : Color.White;
                if (on && b.Hint != null)
                {
                    string h = b.Hint();
                    hint.Text = h;
                    int w = 40 + h.Length * 10; // 10 px par caractère à 18 pt (9,4 mesurés)
                    hint.Size = new Size(w, 44);
                    hint.Position = new Position((int)b.Root.PositionX + BtnH / 2 - w / 2, BtnH + 8);
                    hintShown = true;
                }
            }
            if (!hintShown) hint.Hide(); else hint.Show();
        }

        async void TogglePlayed()
        {
            if (busy || item == null) return;
            busy = true;
            bool want = item.UserData?.Played != true;
            SetNote(Lang.T("item.saving"));
            try
            {
                UserItemData d = await Library.SetPlayed(item.Id, want);
                if (Closed) return;
                if (item.UserData == null) item.UserData = new UserItemData();
                item.UserData.Played = d?.Played ?? want;
                SetNote("");
                Guard("Played", RefreshButtons);
            }
            catch (ApiError e) { if (!Closed) SetNote(SignInScreen.ErrorText(e)); }
            catch (Exception) { if (!Closed) SetNote(Lang.T("error.unknown")); }
            finally { busy = false; }
        }

        async void ToggleFavorite()
        {
            if (busy || item == null) return;
            busy = true;
            bool want = item.UserData?.IsFavorite != true;
            SetNote(Lang.T("item.saving"));
            try
            {
                UserItemData d = await Library.SetFavorite(item.Id, want);
                if (Closed) return;
                if (item.UserData == null) item.UserData = new UserItemData();
                item.UserData.IsFavorite = d?.IsFavorite ?? want;
                SetNote("");
                Guard("Favourite", RefreshButtons);
            }
            catch (ApiError e) { if (!Closed) SetNote(SignInScreen.ErrorText(e)); }
            catch (Exception) { if (!Closed) SetNote(Lang.T("error.unknown")); }
            finally { busy = false; }
        }

        static List<MediaUrl> TrailersOf(Item it)
        {
            var l = new List<MediaUrl>();
            if (it?.RemoteTrailers != null)
                foreach (MediaUrl m in it.RemoteTrailers)
                    if (!string.IsNullOrEmpty(m?.Url)) l.Add(m);
            return l;
        }

        void OpenTrailers()
        {
            List<MediaUrl> l = TrailersOf(item);
            if (l.Count == 1) Nav.Push(new TrailerScreen(item, l[0]));
            else if (l.Count > 1) Nav.Push(new TrailersScreen(item, l));
        }

        void OpenVersions(Item it, long startTicks)
        {
            if (it == null) return;
            Nav.Push(new VersionsScreen(it, startTicks, item));
        }

        // ---- Rangées sous le haut de la page ----

        void LayoutSections()
        {
            int y = btnSection.Height + 8;
            if (item.Type == "Series")
            {
                // Saisons (pastilles) et résumé de la saison, puis les épisodes, sans titre.
                const int pill = Theme.PillHeight;
                seasonSec = NewSection(Kind.Seasons, ref y, 200, pill, 0, 200 + Theme.Gap);
                seasonSummary = new TextLabel { Position = new Position(Left, SectionTitleH + pill + 10), Size = new Size(1600, 28), PointSize = Theme.SizeInfo, TextColor = Theme.Grey };
                seasonSec.Root.Add(seasonSummary);
                seasonSec.Height = SectionTitleH + pill + 10 + 28 + 14;
                y = seasonSec.Y + seasonSec.Height;
                episodeSec = NewSection(Kind.Episodes, ref y, 300, 169, 104, 320, false);
                seasonSec.ShowBottom = episodeSec.Y + episodeSec.Height - SectionGap;
                episodeSec.ShowTop = seasonSec.Y + SectionTitleH - 8;
                SetMessage(seasonSec, Lang.T("home.row_loading"));
            }
            else if (item.Type == "BoxSet")
            {
                episodeSec = NewSection(Kind.Posters, ref y, 220, 330, 74, 244);
                SetMessage(episodeSec, Lang.T("home.row_loading"));
            }
            var cast = new List<Person>();
            var crew = new List<Person>();
            foreach (Person p in item.People ?? new Person[0])
            {
                if (p.Type == "Actor" || p.Type == "GuestStar") cast.Add(p);
                else crew.Add(p);
            }
            if (cast.Count > 0) FillPeople(NewSection(Kind.People, ref y, 180, 150, 100, 196), cast, "cast");
            if (crew.Count > 0) FillPeople(NewSection(Kind.People, ref y, 180, 150, 100, 196), crew, "crew");
            AddDetails(ref y);
            if (item.Type == "Movie" || item.Type == "Series")
            {
                similarSec = NewSection(Kind.Posters, ref y, 220, 330, 74, 244);
                similarSec.Title.Text = Lang.T("item.similar");
                SetMessage(similarSec, Lang.T("home.row_loading"));
            }
            pageHeight = y;
            if (episodeSec != null && item.Type == "BoxSet") episodeSec.Title.Text = Lang.T("item.in_collection");
            foreach (Section s in sections) if (s.Title != null && string.IsNullOrEmpty(s.Title.Text)) s.Title.Text = SectionTitle(s);
        }

        // Une rangée : titre, puis les cartes (image de imageH, textes de textH) espacées de step.
        Section NewSection(Kind kind, ref int y, int tileW, int imageH, int textH, int step, bool withTitle = true)
        {
            var s = new Section { Kind = kind, Y = y, TileW = tileW, ImageH = imageH, TextH = textH, Step = step };
            s.Visible = Math.Max(1, (1920 - Left) / step);
            s.TrackY = withTitle ? SectionTitleH : 0;
            s.Height = s.TrackY + imageH + (textH > 0 ? 12 + textH : 0) + SectionGap;
            s.ShowTop = y;
            s.ShowBottom = y + s.Height - SectionGap + 8;
            s.Root = new View { Position = new Position(0, y), Size = new Size(1920, s.Height) };
            if (withTitle)
            {
                s.Title = new TextLabel { Position = new Position(Left, 0), Size = new Size(1200, SectionLabelH), PointSize = SectionSize, TextColor = Color.White, VerticalAlignment = VerticalAlignment.Bottom, FontStyle = Theme.SemiBold() };
                s.Root.Add(s.Title);
            }
            s.Track = new View { Position = new Position(Left, s.TrackY) };
            s.Root.Add(s.Track);
            page.Add(s.Root);
            sections.Add(s);
            y += s.Height;
            return s;
        }

        string SectionTitle(Section s)
        {
            if (s == seasonSec) return Lang.T("item.episodes");
            if (s == similarSec) return Lang.T("item.similar");
            if (s == episodeSec && item?.Type == "BoxSet") return Lang.T("item.in_collection");
            return s.Title?.Name == "crew" ? Lang.T("item.crew") : Lang.T("item.cast");
        }

        void FillPeople(Section s, List<Person> people, string which)
        {
            s.Title.Name = which;
            s.Title.Text = SectionTitle(s);
            for (int i = 0; i < people.Count; i++)
                s.Tiles.Add(new Tile { Sec = s, Index = i, Person = people[i], Url = Library.PersonImage(people[i], 150) });
        }

        void FillTiles(Section s, List<Item> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                string url = null;
                List<string> urls = null;
                // Épisode : son image, sinon celles de la série (si le serveur ne la fournit pas).
                if (s.Kind == Kind.Episodes)
                {
                    urls = Library.LandscapeCandidates(it, s.TileW);
                    // En dernier recours, les images de la série elle-même : la liste des
                    // épisodes ne porte pas toujours celles de la série, et l'image d'un
                    // épisode peut être refusée par le serveur (403, vu le 2026-11-11).
                    foreach (string u in new[] { Library.Backdrop(item, s.TileW), Library.Image(item?.Id, "Thumb", item?.ImageTags?.Thumb, s.TileW) })
                        if (u != null && !urls.Contains(u)) urls.Add(u);
                    url = urls.Count > 0 ? urls[0] : null;
                    Probe.Log("tileurls", "épisode " + i + " : " + urls.Count + " images possibles");
                }
                else if (s.Kind == Kind.Posters) url = Library.Poster(it, s.TileW);
                s.Tiles.Add(new Tile { Sec = s, Index = i, Item = it, Url = url, Urls = urls });
            }
            s.Col = Math.Min(s.Col, Math.Max(0, s.Tiles.Count - 1));
            s.First = 0;
        }

        // Les cartes d'avant sont retirées au tour de boucle suivant.
        void ClearTiles(Section s)
        {
            foreach (Tile t in s.Tiles)
            {
                t.Dead = true;
                if (t.Root != null) { t.Root.Hide(); toDrop.Enqueue(t.Root); }
            }
            s.Tiles.Clear();
            s.Col = s.First = 0;
            s.Track.PositionX = Left;
            EnsureBuilder();
        }

        void SetMessage(Section s, string text)
        {
            if (s == null) return;
            if (text == null) { s.Message?.Hide(); return; }
            if (s.Message == null)
            {
                s.Message = new TextLabel { Position = new Position(Left, s.TrackY + (s.Kind == Kind.Seasons ? 8 : 40)), Size = new Size(1400, 44), PointSize = Theme.SizeBody, TextColor = Theme.Grey };
                s.Root.Add(s.Message);
            }
            s.Message.Text = text;
            s.Message.Show();
        }

        // Détails : studios, date de sortie (ou de première diffusion), pays. Pas de focus.
        void AddDetails(ref int y)
        {
            var rows = new List<(string, string)>();
            if (item.Studios != null && item.Studios.Length > 0)
            {
                var names = new List<string>();
                foreach (NameOnly n in item.Studios) if (!string.IsNullOrEmpty(n.Name)) names.Add(n.Name);
                if (names.Count > 0) rows.Add((Lang.T("item.studios"), string.Join(", ", names)));
            }
            DateTime? d = Library.Date(item.PremiereDate);
            if (d != null) rows.Add((Lang.T(item.Type == "Series" ? "item.first_aired" : "item.released"), Library.LongDate(d.Value)));
            if (item.ProductionLocations != null && item.ProductionLocations.Length > 0)
                rows.Add((Lang.T("item.country"), string.Join(", ", item.ProductionLocations)));
            if (rows.Count == 0) return;
            const int height = SectionTitleH + 24 + 60 + SectionGap;
            var root = new View { Position = new Position(0, y), Size = new Size(1920, height) };
            root.Add(new TextLabel { Position = new Position(Left, 0), Size = new Size(1200, SectionLabelH), PointSize = SectionSize, TextColor = Color.White, VerticalAlignment = VerticalAlignment.Bottom, FontStyle = Theme.SemiBold(), Text = Lang.T("item.details") });
            for (int i = 0; i < rows.Count; i++)
            {
                // Étiquette en petites majuscules grises (12 pt), valeur en texte courant dessous.
                root.Add(new TextLabel { Position = new Position(Left + i * 560, SectionTitleH), Size = new Size(540, 22), PointSize = Theme.SizeSmall, TextColor = Theme.Grey, Text = rows[i].Item1.ToUpperInvariant() });
                root.Add(new TextLabel { Position = new Position(Left + i * 560, SectionTitleH + 24), Size = new Size(540, 60), PointSize = Theme.SizeBody, MultiLine = true, TextColor = Color.White, Text = rows[i].Item2 ?? "" });
            }
            page.Add(root);
            y += height;
        }

        static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        string SeasonSummary(List<Item> episodes)
        {
            if (episodes.Count == 0) return "";
            int? year = null;
            foreach (Item e in episodes) { DateTime? d = Library.Date(e.PremiereDate); if (d != null) { year = d.Value.Year; break; } }
            int unaired = 0;
            foreach (Item e in episodes) if (Library.Unaired(e)) unaired++;
            var parts = new List<string>();
            if (year != null) parts.Add(year.ToString());
            parts.Add(Lang.T(episodes.Count == 1 ? "item.one_episode" : "item.episodes_count", episodes.Count));
            if (unaired > 0) parts.Add(Lang.T("item.unaired_count", unaired));
            return string.Join("  ·  ", parts);
        }

        // ---- Cartes : vues, textes et images, faits par petits morceaux ----

        void EnsureBuilder()
        {
            if (builder != null) return;
            builder = new Timer(1);
            builder.Tick += (s, e) =>
            {
                bool more = false;
                Guard("Build", () => more = BuildStep());
                if (more) return true;
                builder = null;
                return false;
            };
            builder.Start();
        }

        // Un morceau de travail : une vue à détruire, sinon la carte la plus utile
        // maintenant (vues, puis textes, puis demande de son image).
        bool BuildStep()
        {
            if (Closed) return false;
            if (toDrop.Count > 0)
            {
                View v = toDrop.Dequeue();
                v.Unparent();
                v.Dispose();
                return true;
            }
            Tile best = null;
            int bestPrio = int.MaxValue;
            foreach (Section s in sections)
                foreach (Tile t in s.Tiles)
                {
                    if (t.Built && t.Texted && (t.Requested || t.Url == null)) continue;
                    int p = Priority(t);
                    if (p < bestPrio) { bestPrio = p; best = t; }
                }
            if (best == null) return false;
            if (!best.Built) BuildTile(best);
            else if (!best.Texted) TextTile(best);
            else
            {
                best.Requested = true;
                Tile t = best;
                imagesAsked++;
                // Carte trop loin : le chargeur abandonne la demande, elle sera refaite au retour.
                ImageCache.RequestAny(t.Urls ?? new List<string> { t.Url }, () =>
                {
                    int p = Priority(t);
                    if (p == int.MaxValue && !t.Dead) t.Requested = false;
                    return p;
                }, path => Guard("Tile", () => OnTileFile(t, path)));
            }
            return true;
        }

        // Plus petit = plus tôt. Seules les cartes visibles ou proches de l'écran sont traitées.
        int Priority(Tile t)
        {
            if (t.Dead || Closed) return int.MaxValue;
            Section s = t.Sec;
            if (t.Index < s.First - 2 || t.Index > s.First + s.Visible + 2) return int.MaxValue;
            int ds = Math.Abs(sections.IndexOf(s) - focus);
            if (ds > 3) return int.MaxValue;
            return ds * 100 + Math.Abs(t.Index - s.Col);
        }

        void BuildTile(Tile t)
        {
            Section s = t.Sec;
            t.Built = true;
            t.Root = new View { Position = new Position(t.Index * s.Step, 0) };
            if (s.Kind == Kind.Seasons)
            {
                // Pastille d'une saison (charte) : le texte y est posé tout de suite (peu de saisons).
                // Pas de contour : regardée, elle passe sur fond blanc (PaintSeason).
                t.Frame = new View { Size = new Size(s.TileW, s.ImageH) }.Rounded(s.ImageH / 2);
                t.Label = new TextLabel { Size = new Size(s.TileW, s.ImageH), PointSize = Theme.SizeBody, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                t.Label.Text = (t.Item.Name ?? "") + (t.Item.UserData?.Played == true ? "  ✓" : "");
                t.Frame.Add(t.Label);
                t.Texted = true;
                t.Requested = true;
                t.Root.Add(t.Frame);
                s.Track.Add(t.Root);
                PaintSeasons();
                return;
            }
            // Carte qui grossit autour du centre de son image (charte), contour compris.
            t.Root.Size = new Size(s.TileW, s.ImageH);
            t.Root.PivotPoint = Tizen.NUI.PivotPoint.Center;
            t.Root.PositionUsesPivotPoint = false;
            bool round = s.Kind == Kind.People;
            int w = round ? s.ImageH : s.TileW, x = round ? (s.TileW - s.ImageH) / 2 : 0;
            int radius = round ? w / 2 : Theme.RadiusSmall;
            t.Ring = Theme.FocusRing(w, s.ImageH, radius);
            t.Ring.Position = new Position(x - Theme.RingOut, -Theme.RingOut);
            t.Frame = new View { Position = new Position(x, 0), Size = new Size(w, s.ImageH), BackgroundColor = Theme.Placeholder }.Rounded(radius);
            if (s.Kind == Kind.Episodes && t.Item != null) AddProgress(t);
            t.Ring.Hide();
            t.Root.Add(t.Ring);
            t.Root.Add(t.Frame);
            s.Track.Add(t.Root);
            if (sections[focus] == s && t.Index == s.Col) grown = null; // grossie par PaintTile
            PaintTile(t);
        }

        void AddProgress(Tile t)
        {
            Section s = t.Sec;
            double pct = t.Item.UserData?.Played == true ? 0 : Library.Progress(t.Item);
            if (pct <= 0) return;
            // Comme l'accueil (charte) : 8 px collée au bas, arrondie aux bouts, raccourcie de
            // (arrondi de l'image - 4 px) de chaque côté pour épouser les coins.
            const int h = 8;
            int inset = Theme.RadiusSmall - h / 2, bw = s.TileW - 2 * inset;
            t.Frame.Add(new View { Name = "progress", Position = new Position(inset, s.ImageH - h), Size = new Size(bw, h), BackgroundColor = new Color(0f, 0f, 0f, 0.6f) }.Rounded(h / 2));
            t.Frame.Add(new View { Name = "progress", Position = new Position(inset, s.ImageH - h), Size = new Size(Math.Max(h, (int)(bw * pct / 100)), h), BackgroundColor = Theme.Accent }.Rounded(h / 2));
        }

        // Textes sous la carte (un seul TextLabel par carte, sauf les épisodes : titre et corps).
        void TextTile(Tile t)
        {
            Section s = t.Sec;
            t.Texted = true;
            int ty = s.ImageH + 12;
            if (s.Kind == Kind.Episodes)
            {
                Item e = t.Item;
                string name = e.IndexNumber != null ? e.IndexNumber + ". " + e.Name : e.Name;
                t.Label = new TextLabel { Position = new Position(0, ty), Size = new Size(s.TileW, 30), PointSize = Theme.SizeBody, TextColor = Color.White, Text = name ?? "" };
                t.Root.Add(t.Label);
                t.Text = new TextLabel
                {
                    Position = new Position(0, ty + 32),
                    Size = new Size(s.TileW, s.TextH - 32),
                    PointSize = 15,
                    MultiLine = true,
                    EnableMarkup = true,
                    TextColor = Theme.Text,
                    Text = "<color value='" + Muted + "'>" + Esc(EpisodeLine(e)) + "</color>\n" + Esc(Pictures.Cut(e.Overview, 70)),
                };
                t.Root.Add(t.Text);
                // Pas d'image à lui (épisode à venir) : grand numéro sur l'image de la série, assombrie.
                if (e.ImageTags?.Primary == null)
                {
                    var number = new TextLabel { Name = "number", Size = new Size(s.TileW, s.ImageH), PointSize = Theme.SizeHuge, TextColor = Color.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = e.IndexNumber?.ToString() ?? "" };
                    t.Frame.Add(number);
                }
                if (Library.Unaired(e)) t.Frame.Add(Badge(Lang.T("item.unaired"), 12, 12));
                if (e.UserData?.Played == true) t.Frame.Add(Badge("✓", s.TileW - 52, 12, 40));
            }
            else if (s.Kind == Kind.Posters)
            {
                // Comme la grille de Favoris : titre en 18 pt, année en 15 pt grise dessous.
                Item it = t.Item;
                if (Prefs.PosterTitle)
                {
                    t.Label = new TextLabel { Position = new Position(0, ty), Size = new Size(s.TileW, 34), PointSize = Theme.SizeBody, Ellipsis = true, TextColor = Color.White, Text = it.Name ?? "" };
                    t.Root.Add(t.Label);
                }
                if (Prefs.PosterYear && it.ProductionYear != null)
                {
                    t.Text = new TextLabel { Position = new Position(0, ty + (Prefs.PosterTitle ? 36 : 0)), Size = new Size(s.TileW, 28), PointSize = Theme.SizeInfo, Ellipsis = true, TextColor = Theme.Grey, Text = it.ProductionYear.ToString() };
                    t.Root.Add(t.Text);
                }
                if (!Prefs.PosterTitle && t.Url == null) t.Frame.Add(Pictures.NoArt(it.Name, s.TileW, s.ImageH));
                if (it.UserData?.Played == true) t.Frame.Add(Badge("✓", s.TileW - 52, 12, 40));
            }
            else if (s.Kind == Kind.People)
            {
                Person p = t.Person;
                string role = p.Role;
                if (string.IsNullOrEmpty(role) && p.Type != "Actor")
                {
                    role = Lang.T("role." + p.Type);
                    if (role == "role." + p.Type) role = p.Type;
                }
                t.Text = new TextLabel
                {
                    Position = new Position(0, ty),
                    Size = new Size(s.TileW, s.TextH),
                    PointSize = 15,
                    MultiLine = true,
                    EnableMarkup = true,
                    TextColor = Color.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Text = Esc(p.Name) + (string.IsNullOrEmpty(role) ? "" : "\n<color value='" + Muted + "'>" + Esc(Pictures.Cut(role, 40)) + "</color>"),
                };
                t.Root.Add(t.Text);
                if (t.Url == null) t.Frame.Add(new TextLabel { Size = new Size(s.ImageH, s.ImageH), PointSize = Theme.SizeTitle, TextColor = Theme.Grey, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = Initials(p.Name) });
            }
        }

        View Badge(string text, int x, int y, int w = 0)
        {
            if (w == 0) w = 24 + text.Length * 10;
            // Coche « vu » : rond à l'accent du thème ; « Non diffusé » : pastille sombre à 90 %,
            // lisible sur une image claire (charte).
            bool check = w == 40;
            var b = new View { Name = check ? CardMenu.CheckName : "", Position = new Position(x, y), Size = new Size(w, 32), BackgroundColor = check ? Theme.Accent : Theme.Panel }.Rounded(16);
            b.Add(new TextLabel { Size = new Size(w, 32), PointSize = Theme.SizeSmall, TextColor = check ? Theme.OnAccent : Color.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = text });
            return b;
        }

        static string Initials(string name)
        {
            var sb = new StringBuilder();
            foreach (string part in (name ?? "").Split(' '))
                if (part.Length > 0 && sb.Length < 2) sb.Append(char.ToUpperInvariant(part[0]));
            return sb.ToString();
        }

        // « 30 oct. 2026 · 27 min restantes · ★ 8.9 », ou « Diffusion ven. 13 nov. · dans 5 jours · 40 min ».
        string EpisodeLine(Item e)
        {
            var parts = new List<string>();
            DateTime? d = Library.Date(e.PremiereDate);
            bool unaired = Library.Unaired(e);
            if (d != null) parts.Add(unaired ? Lang.T("item.airs_on", Library.DayDate(d.Value)) : Library.ShortDate(d.Value, true));
            if (unaired && d != null) parts.Add(Library.Until(d.Value));
            if (e.RunTimeTicks > 0)
            {
                long pos = e.UserData?.Played == true ? 0 : e.UserData?.PlaybackPositionTicks ?? 0;
                string dur = pos > 0 && pos < e.RunTimeTicks ? Lang.T("time.left", Library.Duration(e.RunTimeTicks.Value - pos)) : Library.Duration(e.RunTimeTicks.Value);
                if (dur != null) parts.Add(dur);
            }
            if (!unaired)
            {
                string score = Library.Score(e);
                if (score != null) parts.Add(score);
            }
            return string.Join(" · ", parts);
        }

        void OnTileFile(Tile t, string path)
        {
            if (path == null && !t.Dead && t.Sec.Kind == Kind.Episodes) Probe.Log("tile", "épisode " + t.Index + " : aucune image");
            if (t.Dead || Closed || path == null) return;
            Section s = t.Sec;
            if (s.Kind == Kind.People)
            {
                ImageView c = Pictures.Circle(path, s.ImageH);
                if (c == null) return;
                t.Frame.Add(c);
                Fade(c);
                return;
            }
            View v = Pictures.Cover(path, s.TileW, s.ImageH, out ImageView img);
            if (s.Kind == Kind.Episodes)
                Probe.Log("tile", "épisode " + t.Index + " : " + System.IO.Path.GetExtension(path) + (v == null ? " refusé" : " affiché"));
            if (v == null) return;
            img.Rounded(Theme.RadiusSmall); // images arrondies au décodage (charte)
            t.Frame.Add(v);
            v.LowerToBottom();
            // Image de la série sous un épisode à venir : assombrie, le numéro par-dessus.
            if (s.Kind == Kind.Episodes && t.Item.ImageTags?.Primary == null) v.Opacity = 0.35f;
            Fade(img);
        }

        // ---- Focus ----

        void SetFocus(int index)
        {
            focus = index;
            Section s = sections[focus];
            PaintFocus();
            // La rangée regardée entièrement visible (le haut de la page pour les boutons).
            int target = scroll;
            if (s == btnSection) target = 0;
            else
            {
                if (s.ShowBottom - target > 1080 - ScrollMargin) target = s.ShowBottom - (1080 - ScrollMargin);
                if (s.ShowTop - target < ScrollMargin) target = s.ShowTop - ScrollMargin;
            }
            target = Math.Max(0, Math.Min(target, Math.Max(0, pageHeight - 1080)));
            if (target != scroll)
            {
                scroll = target;
                scrollAnim?.Stop();
                scrollAnim = new Animation(ScrollMs);
                scrollAnim.AnimateTo(page, "PositionY", (float)-scroll, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
                scrollAnim.Play();
            }
            if (s != btnSection) LoadSimilar();
            EnsureBuilder();
        }

        void PaintFocus()
        {
            PaintButtons();
            foreach (Section s in sections)
                foreach (Tile t in s.Tiles) PaintTile(t);
            GrowFocused();
        }

        void PaintTile(Tile t)
        {
            if (t.Sec.Kind == Kind.Seasons) { PaintSeason(t); return; }
            if (t.Ring == null) return;
            bool on = sections[focus] == t.Sec && t.Index == t.Sec.Col;
            if (on) t.Ring.Show(); else t.Ring.Hide();
            if (on) GrowFocused();
        }

        void PaintSeasons()
        {
            if (seasonSec == null) return;
            foreach (Tile t in seasonSec.Tiles) PaintSeason(t);
        }

        // Pastille de saison (charte) : regardée, fond blanc et texte foncé ; saison affichée
        // sans le focus, blanc à 24 % ; les autres, blanc à 8 %.
        void PaintSeason(Tile t)
        {
            if (t.Frame == null) return;
            bool on = sections[focus] == seasonSec && t.Index == seasonSec.Col;
            bool sel = t.Item.Id == seasonId;
            t.Frame.BackgroundColor = on ? Color.White : sel ? Theme.Chosen : Theme.Surface;
            t.Label.TextColor = on ? Theme.Ink : Color.White;
        }

        // La carte regardée grossit (x1,06 en 0,2 s, charte), l'ancienne revient à sa taille ;
        // chaque animation va jusqu'au bout et part de la taille affichée. Pas pour les boutons
        // ni les pastilles de saisons.
        Tile grown;
        readonly List<Animation> grows = new List<Animation>(); // gardées, sinon elles s'arrêtent

        void GrowFocused()
        {
            Section s = sections[focus];
            Tile want = s.Kind != Kind.Buttons && s.Kind != Kind.Seasons && s.Col < s.Tiles.Count && s.Tiles[s.Col].Root != null ? s.Tiles[s.Col] : null;
            if (want == grown) return;
            var a = new Animation(Theme.Slide);
            var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
            bool any = false;
            if (grown?.Root != null && !grown.Dead) { a.AnimateTo(grown.Root, "Scale", new Vector3(1f, 1f, 1f), ease); any = true; }
            if (want != null)
            {
                want.Root.RaiseToTop();
                a.AnimateTo(want.Root, "Scale", new Vector3(Theme.Grow, Theme.Grow, 1f), ease);
                any = true;
            }
            grown = want;
            if (!any) return;
            a.Play();
            grows.Add(a);
            if (grows.Count > 4) grows.RemoveAt(0);
        }

        // La carte regardée reste visible : la rangée glisse seulement quand elle atteint un bord.
        void ScrollTrack(Section s, bool animate)
        {
            int first = s.First;
            if (s.Col < first) first = s.Col;
            if (s.Col > first + s.Visible - 1) first = s.Col - s.Visible + 1;
            if (!animate) first = Math.Max(0, Math.Min(s.Col, s.Tiles.Count - s.Visible));
            first = Math.Max(0, first);
            if (first == s.First && animate) return;
            s.First = first;
            float x = Left - first * s.Step;
            s.Slide?.Stop();
            if (!animate) { s.Track.PositionX = x; return; }
            s.Slide = new Animation(SlideMs);
            s.Slide.AnimateTo(s.Track, "PositionX", x, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            s.Slide.Play();
        }

        // ---- Touches ----

        public override bool OnKey(string key)
        {
            bool done = true;
            Guard("Key " + key, () => done = cardMenu.Key(key) || Keys(key));
            return done;
        }

        bool Keys(string key)
        {
            if (item == null || sections.Count == 0) return false;
            Section s = sections[focus];
            switch (key)
            {
                case "Up": Move(-1); return true;
                case "Down": Move(1); return true;
                case "Left":
                case "Right":
                {
                    int step = key == "Left" ? -1 : 1;
                    if (s == btnSection)
                    {
                        int n = ShownButtons().Count;
                        if (buttonCol + step < 0 || buttonCol + step >= n) { Ignored(); return true; }
                        buttonCol += step;
                        PaintButtons();
                        return true;
                    }
                    int col = s.Col + step;
                    if (col < 0 || col >= s.Tiles.Count) { Ignored(); return true; }
                    s.Col = col;
                    ScrollTrack(s, true);
                    // La carte regardée existe tout de suite (sinon le cadre n'aurait rien à entourer).
                    if (!s.Tiles[col].Built) BuildTile(s.Tiles[col]);
                    PaintFocus();
                    EnsureBuilder();
                    ImageCache.Pump();
                    if (s == seasonSec) DelaySeason();
                    return true;
                }
                case "Return":
                case "KP_Enter":
                    Ok(s);
                    return true;
            }
            return false;
        }

        void Move(int step)
        {
            for (int i = focus + step; i >= 0 && i < sections.Count; i += step)
            {
                if (!sections[i].Focusable || (sections[i].Root != null && !sections[i].Root.Visibility)) continue;
                SetFocus(i);
                return;
            }
            Ignored();
        }

        void Ok(Section s)
        {
            if (s == btnSection)
            {
                var shown = ShownButtons();
                if (buttonCol < shown.Count) shown[buttonCol].Run();
                return;
            }
            if (s.Tiles.Count == 0) { Ignored(); return; }
            Tile t = s.Tiles[s.Col];
            switch (s.Kind)
            {
                case Kind.Seasons:
                    seasonTimer?.Stop();
                    cardMenu.Press(t.Item, () => SelectSeason(t.Item.Id, null), t.Frame, onPage: true);
                    break;
                case Kind.Episodes: cardMenu.Press(t.Item, () => OpenVersions(t.Item, Resume(t.Item)), t.Frame, onPage: true, show: item); break;
                case Kind.Posters: cardMenu.Press(t.Item, () => Nav.Push(For(t.Item)), t.Frame); break;
                case Kind.People when t.Person?.Id != null: Nav.Push(new PersonScreen(t.Person.Id, t.Person.Name, t.Person.PrimaryImageTag)); break;
                default: Ignored(); break;
            }
        }

        // Saison regardée pendant une demi-seconde : ses épisodes sont demandés.
        void DelaySeason()
        {
            seasonTimer?.Stop();
            seasonTimer = new Timer(SeasonDelayMs);
            seasonTimer.Tick += (s, e) =>
            {
                Guard("SeasonDelay", () => SelectSeason(seasonSec.Tiles[seasonSec.Col].Item.Id, null));
                return false;
            };
            seasonTimer.Start();
        }

        static void Ignored() => App.Current.KeyIgnored();

        // ---- Mesures ----

        void Note() => Measure.Note = NoteText;

        string NoteText()
        {
            if (Closed) return null;
            return Lang.T("item.panel", tItem, tSeasons, tEpisodes, tSimilar, imagesAsked, imagesShown,
                ImageCache.Queued, ImageCache.Running, guard.SlowCount, guard.Slowest(), guard.LastError ?? Lang.T("home.none"));
        }
    }
}
