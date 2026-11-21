// Arrière-plan d'après l'image, sous tous les écrans (réglage du thème, Prefs.ArtBackground ;
// comme components/shell/layout.tsx du web) : une seule couche posée par l'application sous
// les écrans. Avec le réglage, les écrans qui la montrent (Screen.ArtBackdrop) ont un fond
// transparent (Theme.ScreenBack).
// - Show(titre) : tout de suite (page d'un titre, liste des versions) ;
// - Hover(titre) : carte regardée 0,5 s (accueil, rangées, grilles) ;
// - ShowWith(titre, prêt) : bannière à la une, fondu lancé par la bannière (même moment) ;
// - un titre sans image garde l'image affichée ; changement en fondu de 0,3 s ;
// - Hole : pendant la bande-annonce d'une carte de l'accueil, la couche est faite de quatre
//   bandes autour de la carte (même image, partie montrée par PixelArea) : la vidéo, dessinée
//   sous la fenêtre, se voit au milieu ;
// - cachée hors des écrans qui la montrent (lecteur, bande-annonce plein écran, connexion).
// Sans image encore (premier titre pas encore calculé) : la couleur de fond du thème.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Ui
{
    static class ArtLayer
    {
        const int HoverMs = 500, FadeMs = 300;

        class Slide
        {
            public View Box;
            public readonly ImageView[] Bands = new ImageView[4];
            public Animation Fade;
            public string Path;
            public bool Dark;
        }

        static Slide shown; // dernière image dont le fondu a commencé (celle qui se voit)

        // Image affichée (fichier, et repli assombri), ou null : la couleur de fond du thème.
        // Sert à la bannière qui suit la carte pour fondre sa bande-annonce dans l'arrière-plan.
        public static string Current(out bool dark)
        {
            dark = shown?.Dark ?? false;
            return shown?.Box != null ? shown.Path : null;
        }

        static View layer;
        static readonly View[] back = new View[4];
        static readonly List<Slide> slides = new List<Slide>(); // la dernière par-dessus
        static Rectangle hole; // null : plein écran
        static Screen top;
        static string wanted; // adresse de l'image demandée ou affichée
        static int token;
        static Timer hover;
        static Item hovered;
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static long askedAt; // dernier appui qui a changé la carte regardée, ou ouverture

        public static bool Visible => layer != null && layer.Visibility;

        public static void Init(Window parent)
        {
            layer = new View { Size = new Size(1920, 1080) };
            for (int i = 0; i < 4; i++)
            {
                back[i] = new View();
                layer.Add(back[i]);
            }
            layer.Hide();
            parent.Add(layer);
            Layout();
        }

        // Écran visible changé (Navigator.Changed), ou réglage changé dans les Paramètres.
        public static void Sync(Screen s)
        {
            if (layer == null) return;
            if (s != top) hole = null;
            top = s;
            bool on = ArtWash.On && s != null && s.ArtBackdrop;
            if (on) { foreach (View b in back) b.BackgroundColor = Theme.Background; layer.Show(); }
            else { layer.Hide(); hover?.Stop(); }
            Layout();
        }

        public static void Refresh() => Sync(top);

        // Image du titre, tout de suite (gardée si elle est déjà faite).
        public static void Show(Item it)
        {
            if (!ArtWash.On || it == null) return;
            hover?.Stop();
            if (ArtWash.UrlOf(it) != wanted) askedAt = clock.ElapsedMilliseconds;
            Ask(it);
        }

        // Carte regardée : image demandée si le focus y reste 0,5 s.
        public static void Hover(Item it)
        {
            if (!ArtWash.On || it == null) return;
            askedAt = clock.ElapsedMilliseconds;
            hovered = it;
            if (hover == null)
            {
                hover = new Timer(HoverMs);
                hover.Tick += (s, e) =>
                {
                    try { if (Visible) Ask(hovered); }
                    catch (System.Exception ex) { Probe.Log("art", "échec : " + ex.GetType().Name + " " + ex.Message); }
                    return false;
                };
            }
            hover.Stop();
            hover.Start();
        }

        // Bannière à la une : l'image du titre est préparée sans s'afficher ; ready(play) est
        // appelé quand elle est prête, et play() lance son fondu, en même temps que la bannière
        // change. ready(null) : rien à changer (même image, couche cachée ou réglage éteint).
        public static void ShowWith(Item it, Action<Action> ready)
        {
            if (ArtWash.On) hover?.Stop();
            string url = ArtWash.On && Visible ? ArtWash.UrlOf(it) : null;
            if (url == null || url == wanted) { ready(null); return; }
            askedAt = clock.ElapsedMilliseconds;
            wanted = url;
            int t = ++token;
            ArtWash.Request(it, () => t == token ? 0 : int.MaxValue, (path, dark) =>
            {
                if (t != token || layer == null) return;
                Put(path, dark, s => ready(() => { if (t == token) Start(s); }));
            });
        }

        static void Ask(Item it)
        {
            string url = ArtWash.UrlOf(it);
            if (url == null || url == wanted) return; // sans image : garde la précédente
            wanted = url;
            int t = ++token;
            ArtWash.Request(it, () => t == token ? 0 : int.MaxValue, (path, dark) =>
            {
                if (t != token || layer == null) return; // un autre titre est demandé depuis
                Put(path, dark);
            });
        }

        // Nouvelle image par-dessus les précédentes, en fondu ; les précédentes sont retirées après.
        static void Put(string path, bool dark, Action<Slide> loaded = null)
        {
            var s = new Slide { Box = new View { Size = new Size(1920, 1080), Opacity = 0f }, Path = path, Dark = dark };
            for (int i = 0; i < 4; i++)
            {
                var img = new ImageView();
                // Repli (points illisibles) : la petite image étirée, assombrie de 80 %.
                if (dark) { img.FittingMode = FittingModeType.ScaleToFill; img.Color = new Color(0.2f, 0.2f, 0.2f, 1f); }
                s.Bands[i] = img;
                s.Box.Add(img);
            }
            bool started = false;
            s.Bands[0].ResourceReady += (o, e) =>
            {
                if (started || s.Box == null) return;
                started = true;
                Probe.Log("art", $"image prête {clock.ElapsedMilliseconds - askedAt} ms après la carte ou l'ouverture");
                if (loaded != null) loaded(s); else Start(s);
            };
            foreach (ImageView img in s.Bands) img.ResourceUrl = path;
            slides.Add(s);
            layer.Add(s.Box);
            Place(s);
        }

        static void Start(Slide s)
        {
            if (s.Box == null || s.Fade != null) return;
            shown = s;
            s.Fade = new Animation(FadeMs);
            s.Fade.AnimateTo(s.Box, "Opacity", 1f);
            s.Fade.Finished += (o, e) => Drop(s);
            s.Fade.Play();
        }

        // Fondu fini : les images dessous ne se voient plus.
        static void Drop(Slide s)
        {
            int k = slides.IndexOf(s);
            for (int i = 0; i < k; i++)
            {
                Slide o = slides[0];
                slides.RemoveAt(0);
                o.Fade?.Stop();
                o.Fade?.Dispose();
                layer.Remove(o.Box);
                o.Box.Dispose();
                o.Box = null;
            }
            s.Fade?.Dispose();
            s.Fade = null;
        }

        // Bande-annonce d'une carte : la couche laisse ce rectangle vide (null : plein écran).
        public static void Hole(Rectangle r)
        {
            hole = r;
            Layout();
        }

        static void Layout()
        {
            Rectangle[] r = Bands();
            for (int i = 0; i < 4; i++) SetBand(back[i], r[i], false);
            foreach (Slide s in slides) Place(s);
        }

        static void Place(Slide s)
        {
            Rectangle[] r = Bands();
            for (int i = 0; i < 4; i++) SetBand(s.Bands[i], r[i], true);
        }

        static Rectangle[] Bands()
        {
            if (hole == null) return new[] { new Rectangle(0, 0, 1920, 1080), null, null, null };
            int x = hole.X, y = hole.Y, w = hole.Width, h = hole.Height;
            return new[]
            {
                new Rectangle(0, 0, 1920, y),
                new Rectangle(0, y + h, 1920, 1080 - y - h),
                new Rectangle(0, y, x, h),
                new Rectangle(x + w, y, 1920 - x - w, h),
            };
        }

        static void SetBand(View v, Rectangle r, bool image)
        {
            if (r == null || r.Width <= 0 || r.Height <= 0) { v.Hide(); return; }
            v.Position = new Position(r.X, r.Y);
            v.Size = new Size(r.Width, r.Height);
            if (image) ((ImageView)v).PixelArea = new RelativeVector4(r.X / 1920f, r.Y / 1080f, r.Width / 1920f, r.Height / 1080f);
            v.Show();
        }

        // Fond d'un panneau posé sur un écran (liste des liens, bandes-annonces), même règle que les
        // écrans : la couleur de fond du thème, ou avec le réglage la partie de l'arrière-plan
        // affiché qui se trouve à cet endroit (comme une vitre sans flou : l'image est déjà floue).
        // mask : masque d'opacité en coordonnées de l'écran (null : opaque) ; sans arrière-plan
        // d'après l'image, rien n'est rendu (null) dans ce cas.
        public static View Piece(int x, int y, int w, int h, float radius, string mask = null)
        {
            bool dark = false;
            string src = ArtWash.On ? Current(out dark) : null;
            if (src == null)
                return mask != null ? null : new View { Position = new Position(x, y), Size = new Size(w, h), BackgroundColor = Theme.Background }.Rounded(radius);
            var v = new ImageView { Position = new Position(x, y), Size = new Size(w, h) }.Rounded(radius);
            if (dark) { v.FittingMode = FittingModeType.ScaleToFill; v.Color = new Color(0.2f, 0.2f, 0.2f, 1f); }
            if (mask != null) { v.CropToMask = false; v.AlphaMaskURL = mask; }
            v.ResourceUrl = src;
            v.PixelArea = new RelativeVector4(x / 1920f, y / 1080f, w / 1920f, h / 1080f);
            return v;
        }
    }
}
