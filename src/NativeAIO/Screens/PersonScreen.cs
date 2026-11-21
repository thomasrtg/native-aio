// Fiche d'une personne (pages/person.tsx du web), ouverte depuis la distribution ou l'équipe
// d'un titre et depuis « Personnalités » dans la recherche. Disposition demandée :
// - à gauche, une colonne fixe de 560 px : photo (2:3, arrondie à 12 px), nom, dates de
//   naissance et de décès avec l'âge (textes du web), biographie coupée par « … » ;
// - à droite, « Filmographie  N », les pastilles Tout / Films / Séries et la grille,
//   5 affiches par ligne, du plus récent au plus ancien.
// Pas de barre latérale, comme la page d'un titre.
using System;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NativeAIO.Core;
using NativeAIO.Ui;
using Size = Tizen.NUI.Size;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Screens
{
    class PersonScreen : GridScreen
    {
        const int LeftX = 96, LeftW = 560, PhotoW = 200, PhotoH = 300, InfoX = LeftX + PhotoW + 32, InfoW = LeftX + LeftW - InfoX;
        const int BioY = TopY + PhotoH + 32, BioMax = 1300;
        static readonly Color TileBack = new Color(0.122f, 0.129f, 0.161f, 1f); // #1F2129
        static readonly Color Error = new Color(1f, 0.85f, 0.4f, 1f);         // #FFD966

        readonly string id, imageTag;
        string name;
        Item person;
        bool personFailed;
        View photoFrame;
        TextLabel nameLabel, dates, bio;

        public PersonScreen(string id, string name, string imageTag)
        {
            this.id = id;
            this.name = name;
            this.imageTag = imageTag;
        }

        protected override int X => LeftX + LeftW + 56;
        protected override int Cols => 5;
        protected override int TitleSize => 22;
        protected override int TitleH => 40;
        protected override int CountSize => 15;
        protected override string TitleText => Lang.T("person.filmography");
        protected override string[] Types => new[] { "Movie,Series", "Movie", "Series" };
        protected override string Name => "personne";
        protected override string EmptyText => Lang.T("person.empty");
        protected override string FailedText => Lang.T("person.work_failed");
        protected override bool RefreshOnReturn => false;
        protected override Task<ItemsResult> Fetch(string types, int start, int size) => Library.PersonItems(id, types, start, size);

        protected override void BuildMore()
        {
            photoFrame = new View { Position = new Position(LeftX, TopY), Size = new Size(PhotoW, PhotoH), BackgroundColor = TileBack }.Rounded(12);
            nameLabel = new TextLabel
            {
                Position = new Position(InfoX, TopY),
                Size = new Size(InfoW, 110),
                PointSize = 30,
                MultiLine = true,
                Ellipsis = true,
                TextColor = Color.White,
                Text = name ?? "",
            };
            dates = new TextLabel { Position = new Position(InfoX, TopY + 124), Size = new Size(InfoW, PhotoH - 124), PointSize = 15, MultiLine = true, TextColor = Muted };
            bio = new TextLabel { Position = new Position(LeftX, BioY), Size = new Size(LeftW, 1080 - 40 - BioY), PointSize = 18, MultiLine = true, Ellipsis = true, TextColor = Color.White };
            root.Add(photoFrame);
            root.Add(nameLabel);
            root.Add(dates);
            root.Add(bio);
            AskPhoto(imageTag);
            LoadPerson();
        }

        public override void ApplyTexts()
        {
            base.ApplyTexts();
            ShowPerson();
        }

        void AskPhoto(string tag)
        {
            string url = Library.Image(id, "Primary", tag, PhotoW);
            if (url == null) return;
            ImageCache.Request(url, () => Closed ? int.MaxValue : 0, path => guard.Run("Photo", () => OnPhoto(path)));
        }

        void OnPhoto(string path)
        {
            if (Closed || path == null || photoFrame.ChildCount > 0) return;
            View v = Pictures.Cover(path, PhotoW, PhotoH, out ImageView img);
            if (v == null) return;
            img.Rounded(12);
            photoFrame.Add(v);
        }

        async void LoadPerson()
        {
            Item p;
            try { p = await Library.Full(id); }
            catch (Exception e)
            {
                Probe.Log("grid", "personne : fiche en échec, " + e.GetType().Name);
                if (Closed) return;
                guard.Run("Person", () => { personFailed = true; ShowPerson(); });
                return;
            }
            if (Closed) return;
            guard.Run("Person", () =>
            {
                person = p;
                personFailed = p == null;
                if (p?.Name != null) name = p.Name;
                if (imageTag == null && p?.ImageTags?.Primary != null) AskPhoto(p.ImageTags.Primary);
                ShowPerson();
            });
        }

        void ShowPerson()
        {
            if (nameLabel == null) return;
            nameLabel.Text = name ?? "";
            // Dates juste sous le nom : une ligne de 30 pt tient ~22 caractères dans 328 px
            // (mesuré à la sonde : 14 px par caractère).
            dates.PositionY = TopY + ((name ?? "").Length > 22 ? 2 : 1) * 46 + 14;
            if (personFailed)
            {
                dates.Text = "";
                bio.TextColor = Error;
                bio.Text = Lang.T("person.failed");
                return;
            }
            if (person == null) { dates.Text = ""; bio.Text = ""; return; }
            dates.Text = LifeDates(person);
            bio.TextColor = Color.White;
            string text = (person.Overview ?? "").Replace("\r", "");
            while (text.Contains("\n\n")) text = text.Replace("\n\n", "\n");
            bio.Text = Pictures.Cut(text.Trim(), BioMax);
        }

        // Lignes « Né(e) le … (64 ans) à … » et « Décédé(e) le … (à l'âge de … ans) », comme
        // le web (bornLine, diedLine), avec l'âge atteint.
        static string LifeDates(Item p)
        {
            DateTime? born = Library.Date(p.PremiereDate), died = Library.Date(p.EndDate);
            string place = p.ProductionLocations != null && p.ProductionLocations.Length > 0 ? p.ProductionLocations[0] : null;
            string first;
            if (born == null) first = place != null ? Lang.T("person.born_in", place) : null;
            else
            {
                string date = Library.LongDate(born.Value);
                if (died != null) first = place != null ? Lang.T("person.born_date_in", date, place) : Lang.T("person.born_date", date);
                else
                {
                    int age = Years(born.Value, DateTime.UtcNow);
                    first = place != null ? Lang.T("person.born_age_in", date, age, place) : Lang.T("person.born_age", date, age);
                }
            }
            string second = null;
            if (died != null)
            {
                string date = Library.LongDate(died.Value);
                second = born == null ? Lang.T("person.died", date) : Lang.T("person.died_age", date, Years(born.Value, died.Value));
            }
            if (first == null) return second ?? "";
            return second == null ? first : first + "\n" + second;
        }

        static int Years(DateTime from, DateTime to)
        {
            int y = to.Year - from.Year;
            if (to.Month < from.Month || (to.Month == from.Month && to.Day < from.Day)) y--;
            return Math.Max(0, y);
        }
    }
}
