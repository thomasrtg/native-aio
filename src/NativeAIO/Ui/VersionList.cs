// Liste des versions, commune à la liste des liens (Screens/VersionsScreen.cs) et au tiroir des
// versions du lecteur (module 14, comme le web) : une carte par version (rond lecture, nom en
// demi-gras, description en gris clair) et les messages des addons (Type « Placeholder ») placés
// comme le web (placedNotices), sans doublons ; le focus ne s'y arrête pas (sur la TV, leurs liens
// ne mènent nulle part). Version en cours (lecteur) : pastille blanche « Lecture en cours ».
// La TV ne coupe pas ce qui dépasse d'une zone (ClipChildren sans effet) : les cartes glissent
// sous l'en-tête du panneau, que l'écran pose par-dessus la liste ; en bas, une carte n'est
// montrée qu'entière, et un curseur à droite donne la position dans la liste (fenêtre de taille
// fixe, demandes de l'auteur) ; les cartes qui entrent ou sortent passent en fondu.
// Les cartes sont créées au tour de boucle suivant, une à la fois, autour du focus.
using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using Size = Tizen.NUI.Size;

namespace NativeAIO.Ui
{
    class VersionList
    {
        // Carte : marge intérieure, rond lecture, icône des messages.
        const int In = 16, Circle = Theme.PillHeight, IconSize = 26;
        // 16 px entre deux cartes : le contour de la carte regardée y déborde.
        const int Gap = Theme.Gap, Ahead = 5;
        // Lignes estimées (lire LineCount forçait la mise en page, jusqu'à 109 ms par ligne) :
        // 18 pt, 28 px par ligne ; 15 pt, 20 px par ligne (capture de la sonde du 2026-11-18).
        const int NameLine = 28, InfoLine = 20;
        // Pastille « Lecture en cours » (12 pt sur blanc).
        const int BadgeH = 26;

        class Row
        {
            public Source Source;
            public bool Notice;
            public View Box, Round;
            public ImageView Play;
            public int Y, H;
            public bool Shown = true;
            public Animation Fade; // gardée dans un champ
        }

        readonly int left, width, listTop, listH, panelTop, panelBottom;
        readonly int textX, textW, noticeX, noticeW, namePer, infoPer;
        readonly View list, bar, thumb;
        readonly CardFocus lit; // contour et assombrissement de la carte regardée, qui glissent
        readonly Guarded guard;
        readonly Func<bool> closed;
        readonly List<Row> rows = new List<Row>();
        int built, focus = -1, scroll;
        Timer builder; // gardé dans un champ, sinon il s'arrête
        Animation scrollAnim, barAnim; // gardées dans des champs

        // Version jouée (lecteur) : pastille « Lecture en cours » sur sa carte.
        public Source Current;
        // Le focus est dans la liste (sinon : en-tête de la liste des liens).
        public bool Active = true;

        public int Versions { get; private set; }
        public int RowCount => rows.Count;
        public int Built => built;
        public int Focus => focus;
        public Source Focused => focus >= 0 && focus < rows.Count ? rows[focus].Source : null;

        // parent : vue où poser la liste et le curseur, en coordonnées de l'écran (à créer avant
        // l'en-tête, posé ensuite par-dessus) ; left, width : colonne des cartes ; listTop, listH :
        // fenêtre fixe de la liste ; panelTop, panelBottom : bords du panneau (une carte passe sous
        // l'en-tête jusqu'au haut du panneau) ; barX : rail du curseur, dans la marge de droite.
        public VersionList(View parent, int left, int width, int listTop, int listH, int panelTop, int panelBottom, int barX, Guarded guard, Func<bool> closed)
        {
            this.left = left;
            this.width = width;
            this.listTop = listTop;
            this.listH = listH;
            this.panelTop = panelTop;
            this.panelBottom = panelBottom;
            this.guard = guard;
            this.closed = closed;
            textX = In + Circle + In;
            textW = width - textX - In;
            noticeX = In + IconSize + In;
            noticeW = width - noticeX - In;
            // ~84 caractères en 18 pt et ~120 en 15 pt dans les 868 px de la liste des liens.
            namePer = Math.Max(20, 84 * textW / 868);
            infoPer = Math.Max(20, 120 * textW / 868);
            // Liste, sous l'en-tête. ClipChildren ne coupe rien sur la TV (voir plus haut).
            var clip = new View { Position = new Position(0, listTop), Size = new Size(1920, listH) };
            list = new View();
            lit = new CardFocus(list, left, width, Theme.RadiusLarge);
            clip.Add(list);
            parent.Add(clip);
            // Curseur de défilement : rail à 4 %, curseur à 35 %.
            bar = new View { Position = new Position(barX, listTop), Size = new Size(4, listH), CornerRadius = 2f, BackgroundColor = Theme.Faint };
            thumb = new View { Size = new Size(4, 48), CornerRadius = 2f, BackgroundColor = Theme.Faded };
            bar.Hide();
            bar.Add(thumb);
            parent.Add(bar);
        }

        // Versions jouables, dans l'ordre de la liste.
        public List<Source> Playable()
        {
            var l = new List<Source>();
            foreach (Row r in rows) if (!r.Notice) l.Add(r.Source);
            return l;
        }

        // Messages placés après la dernière version qui les précède dans la réponse d'AIOStreams
        // (index), ceux d'avant toutes les versions en tête, comme placedNotices du web. Focus sur la
        // version en cours s'il y en a une, sinon sur la première.
        public void Place(Source[] sources)
        {
            var playable = new List<Source>();
            foreach (Source src in sources) if (src.Type != "Placeholder") playable.Add(src);
            Versions = playable.Count;
            var after = new Dictionary<Source, List<Source>>();
            var head = new List<Source>();
            var seen = new HashSet<string>();
            foreach (Source n in sources)
            {
                if (n.Type != "Placeholder") continue;
                if (!seen.Add((n.Aio?.Name ?? n.Name) + "|" + n.Aio?.Description)) continue;
                int at = n.Aio?.Index ?? int.MaxValue;
                Source last = null;
                foreach (Source v in playable) if ((v.Aio?.Index ?? int.MinValue) < at) last = v;
                if (last == null) head.Add(n);
                else
                {
                    if (!after.TryGetValue(last, out var l)) after[last] = l = new List<Source>();
                    l.Add(n);
                }
            }
            foreach (Source n in head) rows.Add(new Row { Source = n, Notice = true });
            foreach (Source v in playable)
            {
                rows.Add(new Row { Source = v });
                if (after.TryGetValue(v, out var l)) foreach (Source n in l) rows.Add(new Row { Source = n, Notice = true });
            }
            for (int i = 0; i < rows.Count; i++) if (!rows[i].Notice) { focus = i; break; }
            for (int i = 0; i < rows.Count; i++) if (!rows[i].Notice && rows[i].Source == Current) { focus = i; break; }
        }

        // Liste vidée (« Rechercher à nouveau »).
        public void Clear()
        {
            Stop();
            foreach (Row r in rows)
            {
                r.Fade?.Stop();
                if (r.Box != null) { r.Box.Unparent(); r.Box.Dispose(); }
            }
            rows.Clear();
            built = 0;
            Versions = 0;
            focus = -1;
            scroll = 0;
            lit.Clear();
            scrollAnim?.Stop();
            list.PositionY = 0;
            bar.Hide();
        }

        public void Stop()
        {
            builder?.Stop();
            builder = null;
        }

        // Premières cartes : celle du focus montrée (créée tout de suite si elle est plus bas).
        public void Start()
        {
            if (focus > FirstVersion()) SetFocus(focus);
            else EnsureBuilder();
        }

        // Focus entré ou sorti de la liste (Active changé) : carte repeinte, contour et
        // assombrissement effacés sur place en sortant, posés sur la carte en revenant.
        public void Refocus()
        {
            if (Active && focus >= 0) SetFocus(focus);
            else { lit.Hide(); Paint(focus); }
        }

        // Version voisine : false s'il n'y en a pas.
        public bool Up()
        {
            int k = focus < 0 ? -1 : Next(focus, -1);
            if (k < 0) return false;
            lastMove = Environment.TickCount;
            SetFocus(k);
            return true;
        }

        public bool Down()
        {
            int k = focus < 0 ? -1 : Next(focus, 1);
            if (k < 0) return false;
            lastMove = Environment.TickCount;
            SetFocus(k);
            return true;
        }

        // ---- Cartes ----

        // Cartes d'avance construites seulement 250 ms après le dernier déplacement : construite
        // juste après un appui, une carte retardait l'image de cet appui de 350 à 600 ms (mise en
        // page de son texte par la TV ; aucun traitement de l'application au-dessus de 30 ms,
        // sonde du 2026-11-19), sur un appui sur Bas sur deux ou trois.
        const int BuildSettle = 250;
        int lastMove = Environment.TickCount - BuildSettle; // à l'ouverture : tout de suite

        void EnsureBuilder()
        {
            if (builder != null) return;
            builder = new Timer(20);
            builder.Tick += (s, e) =>
            {
                if (unchecked(Environment.TickCount - lastMove) < BuildSettle) return true; // on attend
                bool more = false;
                guard.Run("BuildRow", () =>
                {
                    if (closed() || built >= rows.Count || built > Math.Max(focus, 0) + Ahead) return;
                    BuildRow();
                    more = true;
                });
                if (more) return true;
                builder = null;
                guard.Run("Bar", () => { if (!closed()) UpdateBar(); });
                return false;
            };
            builder.Start();
        }

        // La carte suivante, sous la précédente ; sa hauteur suit le nombre de lignes de texte.
        void BuildRow()
        {
            Row r = rows[built];
            r.Y = built == 0 ? 0 : rows[built - 1].Y + rows[built - 1].H + Gap;
            // Grossie autour de son centre sans changer de place (comme la grille de Découvrir).
            r.Box = new View { Position = new Position(left, r.Y), CornerRadius = Theme.RadiusLarge };
            r.H = r.Notice ? BuildNotice(r) : BuildVersion(r);
            Probe.Log("vlist", "carte " + built + " créée"); // repère dans le journal de la sonde
            r.Box.Size = new Size(width, r.H);
            list.Add(r.Box);
            lit.Raise();
            built++;
            Arrange(built - 1, false);
            if (built - 1 == focus) Paint(focus);
        }

        // Version : rond lecture, nom en demi-gras, description en gris clair (comme le web).
        int BuildVersion(Row r)
        {
            string name = r.Source.Aio?.Name;
            if (string.IsNullOrEmpty(name)) name = r.Source.Name ?? "";
            name = name.Trim();
            string desc = r.Source.Aio?.Description?.Trim();
            r.Box.BackgroundColor = Theme.Surface;
            r.Round = new View { Position = new Position(In, In), Size = new Size(Circle, Circle), CornerRadius = Circle / 2f, BackgroundColor = Theme.Surface };
            r.Play = new ImageView { ResourceUrl = Theme.IconPath("play_small"), Size = new Size(IconSize, IconSize), Position = new Position((Circle - IconSize) / 2, (Circle - IconSize) / 2), Color = Color.White };
            r.Round.Add(r.Play);
            r.Box.Add(r.Round);
            int h = In;
            if (r.Source == Current)
            {
                // Pastille blanche comme le web (« Lecture en cours ») ; largeur estimée à 6 px par
                // caractère en 12 pt demi-gras (8 px laissait trop de blanc, sonde du 2026-11-19).
                string badge = Lang.T("player.version_current");
                var pill = new View { Position = new Position(textX, h), Size = new Size(24 + badge.Length * 6, BadgeH), CornerRadius = BadgeH / 2f, BackgroundColor = Color.White };
                pill.Add(new TextLabel { Size = new Size(24 + badge.Length * 6, BadgeH), PointSize = Theme.SizeSmall, TextColor = Theme.Ink, FontStyle = Theme.SemiBold(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = badge });
                r.Box.Add(pill);
                h += BadgeH + 6;
            }
            int nameH = Lines(name, namePer) * NameLine;
            r.Box.Add(new TextLabel { Position = new Position(textX, h), Size = new Size(textW, nameH), PointSize = Theme.SizeBody, MultiLine = true, TextColor = Color.White, FontStyle = Theme.SemiBold(), Text = name });
            h += nameH;
            if (!string.IsNullOrEmpty(desc))
            {
                h += 4;
                r.Box.Add(new TextLabel { Position = new Position(textX, h), Size = new Size(textW, Lines(desc, infoPer) * InfoLine), PointSize = Theme.SizeInfo, MultiLine = true, TextColor = Theme.Soft, Text = desc });
                h += Lines(desc, infoPer) * InfoLine;
            }
            return Math.Max(h, In + Circle) + In;
        }

        // Message d'un addon : icône grise de son type, nom, texte gris ; erreur en jaune (charte).
        int BuildNotice(Row r)
        {
            SourceExtension aio = r.Source.Aio;
            string name, desc;
            if (aio != null) { name = aio.Name ?? ""; desc = aio.Description ?? ""; }
            else
            {
                // Autres serveurs : le texte dans le nom, la première ligne en titre (comme le web).
                string all = r.Source.Name ?? "";
                int cut = all.IndexOf('\n');
                name = cut < 0 ? all : all.Substring(0, cut);
                desc = cut < 0 ? "" : all.Substring(cut + 1);
            }
            name = name.Trim();
            desc = desc.Trim();
            bool error = aio?.Type == "error";
            string icon = error ? "notice_error" : aio?.Type == "statistic" ? "notice_stats" : aio?.Type == "external" ? "notice_link" : "info";
            int per = infoPer * noticeW / textW;
            r.Box.BackgroundColor = Theme.Faint;
            r.Box.Add(new ImageView { ResourceUrl = Theme.IconPath(icon), Size = new Size(IconSize, IconSize), Position = new Position(In, In), Color = error ? Theme.Error : Theme.Grey });
            int h = In;
            if (name.Length > 0)
            {
                int nameH = Lines(name, per) * InfoLine;
                r.Box.Add(new TextLabel { Position = new Position(noticeX, h), Size = new Size(noticeW, nameH), PointSize = Theme.SizeInfo, MultiLine = true, TextColor = error ? Theme.Error : Color.White, FontStyle = Theme.SemiBold(), Text = name });
                h += nameH;
            }
            if (desc.Length > 0)
            {
                if (h > In) h += 4;
                r.Box.Add(new TextLabel { Position = new Position(noticeX, h), Size = new Size(noticeW, Lines(desc, per) * InfoLine), PointSize = Theme.SizeInfo, MultiLine = true, TextColor = Theme.Grey, Text = desc });
                h += Lines(desc, per) * InfoLine;
            }
            return Math.Max(h, In + IconSize) + In;
        }

        // Nombre de lignes : une par ligne du texte, plus les retours à la ligne estimés mot par
        // mot comme la TV (un mot trop long pour finir la ligne passe à la suivante : le nom de
        // fichier après « 📁 » prenait une ligne de plus que prévu dans le tiroir du lecteur, et
        // sa fin était coupée, sonde du 2026-11-19).
        static int Lines(string text, int perLine)
        {
            int n = 0;
            foreach (string l in text.Split('\n'))
            {
                int lines = 1, used = 0;
                foreach (string w in l.Split(' '))
                {
                    int need = used == 0 ? w.Length : used + 1 + w.Length;
                    if (need <= perLine) { used = need; continue; }
                    lines++;
                    if (used == 0) lines--; // mot seul en début de ligne : coupé sur place
                    int extra = Math.Max(0, (w.Length - 1) / perLine);
                    lines += extra;
                    used = w.Length - extra * perLine;
                }
                n += lines;
            }
            return Math.Max(1, n);
        }

        // Place d'une carte dans la fenêtre fixe de la liste, le haut de la liste à scroll : une carte
        // n'est montrée qu'entière (l'auteur, 2026-11-18 : plus de coupe par « … », la position se lit
        // sur le curseur de droite), sauf au-dessus, où elle passe sous l'en-tête (cachée seulement
        // si elle dépasserait le haut du panneau). fade : entrée et sortie en fondu ; delay : l'entrée
        // attend la fin du défilement, la sortie est courte (sinon la carte dépasserait de la fenêtre).
        void Arrange(int k, bool fade, int delay = 0, float from = 0, float to = 0)
        {
            Row r = rows[k];
            int top = r.Y - scroll;
            bool shown = top >= -(listTop - panelTop) && top + r.H <= listH;
            bool leaving = !shown && r.Fade != null; // encore en train de s'effacer : nouveau trajet
            if (shown == r.Shown && !leaving) return;
            r.Shown = shown;
            r.Fade?.Stop();
            r.Fade = null;
            if (!fade)
            {
                r.Box.Opacity = 1f;
                if (shown) r.Box.Show(); else r.Box.Hide();
                return;
            }
            Animation a;
            if (shown)
            {
                r.Box.Opacity = 0f;
                r.Box.Show();
                a = new Animation(delay + Theme.Slide);
                a.AnimateTo(r.Box, "Opacity", 1f, delay, delay + Theme.Slide);
            }
            else
            {
                int end = OutTime(r, from, to);
                if (end <= 0) { r.Box.Hide(); return; }
                a = new Animation(end);
                a.AnimateTo(r.Box, "Opacity", 0f);
                a.Finished += (o, e) => { if (!r.Shown && r.Box != null) r.Box.Hide(); };
            }
            r.Fade = a;
            a.Play();
        }

        // Durée de l'effacement d'une carte qui sort : finie avant qu'elle ne passe le bord du
        // panneau pendant le défilement (vu par l'auteur : en défilant vite, une carte qui s'en allait
        // se voyait au-dessus du panneau). Défilement EaseOut de la TV : chemin fait = 1 - (1 - t)³.
        int OutTime(Row r, float from, float to)
        {
            float d = to - from;
            if (d == 0) return Theme.Slide / 2;
            float topNow = listTop + from + r.Y, bottomNow = topNow + r.H;
            float p = d < 0 ? (panelTop - topNow) / d : (panelBottom - bottomNow) / d;
            if (p <= 0) return 0;
            if (p >= 1) return Theme.Slide / 2;
            int t = (int)Math.Min(Theme.Slide / 2, Theme.Slide * (1 - Math.Pow(1 - p, 1 / 3.0)));
            return t < 17 ? 0 : t;
        }

        // Curseur de défilement à droite (demande de l'auteur, 2026-11-18) : sa hauteur suit la part
        // de la liste visible, sa place la position dans la liste. Hauteur totale estimée d'après
        // les cartes déjà créées (elles le sont au fur et à mesure) ; caché si tout tient.
        void UpdateBar()
        {
            if (built == 0) { bar.Hide(); return; }
            int end = rows[built - 1].Y + rows[built - 1].H;
            int total = built == rows.Count ? end : (end + Gap) * rows.Count / built - Gap;
            if (total <= listH) { bar.Hide(); return; }
            bar.Show();
            int h = Math.Max(48, listH * listH / total);
            float part = Math.Max(0f, Math.Min(1f, scroll / (float)(total - listH)));
            int y = (int)Math.Round((listH - h) * part); // dans le rail
            barAnim?.Stop();
            barAnim = new Animation(Theme.Slide);
            barAnim.AnimateTo(thumb, "PositionY", (float)y, Theme.Ease());
            barAnim.AnimateTo(thumb, "SizeHeight", (float)h, Theme.Ease());
            barAnim.Play();
        }

        // Carte regardée : rond lecture blanc et triangle foncé (comme le survol du web) ; contour et
        // assombrissement de 20 % portés par la pièce commune qui glisse (CardFocus). Pas de
        // grossissement (essayé, retiré par l'auteur le 2026-11-19).
        void Paint(int i)
        {
            if (i < 0 || i >= built) return;
            Row r = rows[i];
            if (r.Notice) return;
            bool on = Active && i == focus;
            r.Round.BackgroundColor = on ? Color.White : Theme.Surface;
            r.Play.Color = on ? Theme.Ink : Color.White;
            if (on) lit.To(r.Y, r.H);
        }

        void SetFocus(int i)
        {
            while (built <= i) BuildRow();
            int old = focus;
            focus = i;
            if (old != i) Paint(old);
            Row r = rows[focus];
            int target = scroll;
            // 12 px gardés en bas pour le contour.
            if (r.Y + r.H - target > listH - 12) target = r.Y + r.H - (listH - 12);
            if (r.Y - target < 0) target = r.Y;
            // Messages en tête ou en fin de liste : montrés en arrivant sur la première ou la dernière version.
            if (focus == FirstVersion() && r.Y + r.H <= listH) target = 0;
            if (focus == LastVersion())
            {
                while (built < rows.Count) BuildRow();
                int end = rows[built - 1].Y + rows[built - 1].H;
                if (end - listH > target && end - listH <= r.Y) target = end - listH;
            }
            // Le haut de la liste tombe au début d'une carte, celles d'au-dessus passent sous l'en-tête.
            // Arrondi vers la carte suivante : vers la précédente, la carte regardée restait en
            // partie sous la fenêtre (vu par l'auteur, 2026-11-18).
            for (int k = 0; k < built; k++)
                if (rows[k].Y >= target) { target = Math.Min(rows[k].Y, r.Y); break; }
            bool moved = target != scroll;
            float from = list.CurrentPosition.Y; // place affichée, défilement en cours compris
            scroll = target;
            for (int k = 0; k < built; k++) Arrange(k, true, moved ? Theme.Slide : 0, from, -scroll);
            Paint(focus);
            if (moved)
            {
                scrollAnim?.Stop();
                scrollAnim = new Animation(Theme.Slide);
                scrollAnim.AnimateTo(list, "PositionY", (float)-scroll, Theme.Ease());
                scrollAnim.Play();
            }
            UpdateBar();
            EnsureBuilder();
        }

        int FirstVersion()
        {
            for (int k = 0; k < rows.Count; k++) if (!rows[k].Notice) return k;
            return -1;
        }

        int LastVersion()
        {
            for (int k = rows.Count - 1; k >= 0; k--) if (!rows[k].Notice) return k;
            return -1;
        }

        int Next(int from, int step)
        {
            for (int k = from + step; k >= 0 && k < rows.Count; k += step) if (!rows[k].Notice) return k;
            return -1;
        }
    }
}
