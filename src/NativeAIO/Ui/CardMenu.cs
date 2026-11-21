// Menu d'une carte (demande de l'auteur, 2026-11-21, comme le menu contextuel du web,
// components/item-menu.tsx) : appui long sur OK (0,5 s, comme dans le lecteur) sur une carte de
// titre, partout où le web en a un. OK agit donc au relâchement sur ces cartes (appui court :
// l'action habituelle de l'écran). Une touche gardée enfoncée n'envoie qu'un appui et un
// relâchement (LECONS.md).
// Lignes, selon les règles du web : Lecture ou Reprendre, puis « Choisir une version » si la
// lecture part directement, sinon « Lire immédiatement » (films et épisodes) ; Aller à la série ou
// Ouvrir (pas sur la page du titre lui-même) ; trait ; Marquer comme vu / non vu (pas une
// collection) ; vu jusqu'ici (épisode) ; Retirer de Reprendre (titre commencé) ; Ajouter aux
// favoris / Retirer des favoris (films et séries, ou titre déjà en favori). Retirés par l'auteur :
// playlist, téléchargement, « Détails » ; « Épisode aléatoire » plus tard.
// Boîte commune (Ui/Picker.cs, OpenMenu) posée par l'écran au-dessus de tout le reste.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NativeAIO.Core;
using NativeAIO.Screens;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Item = NativeAIO.Core.Item;

namespace NativeAIO.Ui
{
    class CardMenu
    {
        const int HoldMs = 500, MenuW = 440;

        readonly Screen owner;
        readonly Action<string, Action> guard;
        readonly Picker picker = new Picker();
        Timer holdTimer; // gardé dans un champ, sinon il s'arrête
        Item pressed, show;
        Action onShort;
        Func<(float X, float Y, float W, float H)> where;
        bool onPage;

        // Après un envoi au serveur (réussi ou non), l'écran met à jour la carte ou sa liste. Les
        // données du titre (UserData) sont déjà à jour quand le serveur les renvoie.
        public Action<Item> Changed;
        // Vrai pendant Changed quand l'envoi touche aussi d'autres titres (« vu jusqu'ici »).
        public bool Wide { get; private set; }

        public View View => picker.View;
        public bool IsOpen => picker.IsOpen;

        public CardMenu(Screen owner, Action<string, Action> guard)
        {
            this.owner = owner;
            this.guard = guard;
        }

        // OK enfoncé sur une carte. shortPress : l'action de l'appui court ; place : la carte à
        // l'écran (lue à l'ouverture du menu) ; onPage : carte de la page du titre lui-même
        // (épisode, saison) ; show : la série, pour le panneau des versions d'un épisode.
        public void Press(Item it, Action shortPress, Func<(float X, float Y, float W, float H)> place, bool onPage = false, Item show = null)
        {
            if (it == null) { shortPress?.Invoke(); return; }
            pressed = it;
            onShort = shortPress;
            where = place;
            this.onPage = onPage;
            this.show = show;
            if (holdTimer == null)
            {
                holdTimer = new Timer(HoldMs);
                holdTimer.Tick += (s, e) => { guard("Hold", Held); return false; };
            }
            holdTimer.Stop();
            holdTimer.Start();
        }

        public void Press(Item it, Action shortPress, View card, bool onPage = false, Item show = null) =>
            Press(it, shortPress, () => Place(card), onPage, show);

        // Place d'une vue à l'écran, agrandissement compris : calculée en remontant ses parents,
        // d'après leurs valeurs visées (position, taille, agrandissement autour du point de
        // pivot). WorldPosition de la TV ne tenait pas compte du défilement des rangées
        // (vu le 2026-11-21 : « Episode 5 » annoncé à x = 2327).
        public static (float X, float Y, float W, float H) Place(View v)
        {
            if (v == null) return (0, 0, 0, 0);
            float l = 0, t = 0, w = v.SizeWidth, h = v.SizeHeight;
            for (View cur = v; cur != null; cur = cur.GetParent() as View)
            {
                float cw = cur.SizeWidth, ch = cur.SizeHeight, sx = cur.ScaleX, sy = cur.ScaleY;
                Position pivot = cur.PivotPoint;
                float ox = cur.PositionX - (cur.PositionUsesPivotPoint ? pivot.X * cw : 0);
                float oy = cur.PositionY - (cur.PositionUsesPivotPoint ? pivot.Y * ch : 0);
                l = ox + pivot.X * cw + (l - pivot.X * cw) * sx;
                t = oy + pivot.Y * ch + (t - pivot.Y * ch) * sy;
                w *= sx;
                h *= sy;
            }
            return (l, t, w, h);
        }

        // À appeler en tête de OnKey : true si la touche est prise par le menu ouvert. Une autre
        // touche pendant l'appui sur OK l'abandonne.
        public bool Key(string key)
        {
            if (picker.IsOpen) { if (!picker.OnKey(key)) App.Current.KeyIgnored(); return true; }
            if (pressed != null) Drop();
            return false;
        }

        // OK relâché avant 0,5 s : l'action de l'appui court.
        public bool KeyUp(string key)
        {
            if ((key != "Return" && key != "KP_Enter") || pressed == null) return false;
            Action a = onShort;
            Drop();
            if (a != null) guard("KeyUp", a);
            return true;
        }

        public bool Back()
        {
            if (!picker.IsOpen) return false;
            guard("Back", picker.Close);
            return true;
        }

        // Écran recouvert ou fermé.
        public void Hide()
        {
            Drop();
            if (picker.IsOpen) picker.Close();
        }

        public void Drop()
        {
            pressed = null;
            onShort = null;
            holdTimer?.Stop();
        }

        void Held()
        {
            Item it = pressed;
            Func<(float X, float Y, float W, float H)> at = where;
            Drop();
            if (it == null || owner.Closed || owner.Nav?.Top != owner) return;
            Open(it, at());
        }

        public static long ResumeTicks(Item it) => it.UserData?.Played == true ? 0 : it.UserData?.PlaybackPositionTicks ?? 0;

        static string Icon(string name) => Tizen.Applications.Application.Current.DirectoryInfo.Resource + "icons/" + name + ".png";

        void Open(Item it, (float X, float Y, float W, float H) card)
        {
            bool episode = it.Type == "Episode" && it.SeriesId != null;
            bool playable = (it.Type == "Movie" || it.Type == "Episode") && it.LocationType != "Virtual";
            bool played = it.UserData?.Played == true, favorite = it.UserData?.IsFavorite == true;
            long ticks = ResumeTicks(it);
            // Lecture directe (useStraightPlay du web) : réglage, ou version retenue pour une reprise.
            bool straight = Prefs.AutoPlayFirst || (ticks > 0 && LastVersions.Get(it.Id) != null);
            Item serie = show;
            var items = new List<Picker.Item>();
            var acts = new List<Action>();
            void Add(string text, string icon, Action act, bool rule = false)
            {
                items.Add(new Picker.Item { Text = text, Icon = Icon(icon), Rule = rule && items.Count > 0 });
                acts.Add(act);
            }
            if (playable)
            {
                Add(Lang.T(ticks > 0 ? "menu.resume" : "menu.play"), "menu_play", () => owner.Nav.Push(new VersionsScreen(it, ticks, serie)));
                if (straight) Add(Lang.T("menu.versions"), "menu_versions", () => owner.Nav.Push(new VersionsScreen(it, ticks, serie, straight: false)));
                else Add(Lang.T("menu.now"), "menu_now", () => owner.Nav.Push(new VersionsScreen(it, ticks, serie, now: true)));
            }
            if (!onPage)
            {
                if (episode) Add(Lang.T("menu.show"), "menu_show", () => owner.Nav.Push(ItemScreen.For(it)));
                else Add(Lang.T("menu.open"), "menu_open", () => owner.Nav.Push(ItemScreen.For(it)));
            }
            if (it.Type != "BoxSet")
            {
                Add(Lang.T(played ? "item.mark_unwatched" : "item.mark_watched"), "menu_watched",
                    () => Send(it, played ? "non vu" : "vu", async () =>
                    {
                        UserItemData d = await Library.SetPlayed(it.Id, !played);
                        Keep(it, d);
                        it.UserData.Played = d?.Played ?? !played;
                    }), rule: true);
                if (episode) Add(Lang.T("menu.upto"), "menu_upto", () => Send(it, "vu jusqu'ici", async () =>
                {
                    await Library.SetPlayedUpTo(it);
                    if (it.UserData != null) { it.UserData.Played = true; it.UserData.PlaybackPositionTicks = 0; }
                }, wide: true));
                if (ticks > 0) Add(Lang.T("menu.remove"), "menu_reset", () => Send(it, "retiré de Reprendre", async () =>
                {
                    await Library.ClearResume(it.Id);
                    if (it.UserData != null) it.UserData.PlaybackPositionTicks = 0;
                }));
            }
            if (favorite || it.Type == "Movie" || it.Type == "Series")
                Add(Lang.T(favorite ? "item.remove_favourite" : "item.add_favourite"), favorite ? "menu_unfav" : "menu_fav",
                    () => Send(it, favorite ? "retiré des favoris" : "ajouté aux favoris", async () =>
                    {
                        UserItemData d = await Library.SetFavorite(it.Id, !favorite);
                        Keep(it, d);
                        it.UserData.IsFavorite = d?.IsFavorite ?? !favorite;
                    }));
            if (items.Count == 0) { App.Current.KeyIgnored(); return; }
            string title = onPage || !episode ? it.Name : it.SeriesName ?? it.Name;
            picker.OpenMenu(items, card.X, card.Y, card.W, card.H, MenuW, title, i => guard("Menu", () => acts[i]()));
            Probe.Log("menu", $"menu de « {title} » ouvert ({items.Count} lignes, carte {card.X:0},{card.Y:0} {card.W:0}×{card.H:0})");
        }

        // Données renvoyées par le serveur : gardées dans le titre (cartes, page). Sans réponse,
        // l'appelant pose lui-même le nouvel état (comme les boutons de la page d'un titre).
        static void Keep(Item it, UserItemData d)
        {
            if (d != null) it.UserData = d;
            else if (it.UserData == null) it.UserData = new UserItemData();
        }

        // Coche « vu » d'une carte (le ✓ sur fond d'accent) ajoutée ou retirée après un envoi du
        // menu. La coche porte le nom « check » (posé ici ou à la construction de la carte).
        public const string CheckName = "check";

        public static void SetCheck(View frame, bool on, Func<View> make)
        {
            if (frame == null) return;
            View found = null;
            for (uint i = 0; i < frame.ChildCount; i++)
            {
                View c = frame.GetChildAt(i);
                if (c.Name == CheckName) { found = c; break; }
            }
            if (on && found == null)
            {
                View b = make();
                b.Name = CheckName;
                frame.Add(b);
            }
            else if (!on && found != null)
            {
                found.Unparent();
                found.Dispose();
            }
        }

        async void Send(Item it, string what, Func<Task> request, bool wide = false)
        {
            try { await request(); Probe.Log("menu", what + " : fait"); }
            catch (Exception e) { Probe.Log("menu", $"{what} : échec ({e.Message})"); }
            if (owner.Closed) return;
            guard("MenuDone", () =>
            {
                Wide = wide;
                try { Changed?.Invoke(it); }
                finally { Wide = false; }
            });
        }
    }
}
