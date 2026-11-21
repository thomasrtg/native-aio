// Un écran de l'application : une vue de la fenêtre unique, posée par le
// Navigator au-dessus de l'écran précédent.
using Tizen.NUI.BaseComponents;

namespace NativeAIO.Core
{
    abstract class Screen
    {
        public View Root { get; private set; }
        public Navigator Nav { get; internal set; }

        // Vrai une fois l'écran fermé : une réponse du serveur arrivée après ne doit plus le toucher.
        public bool Closed { get; internal set; }

        // Vrai pour un panneau posé par-dessus l'écran précédent, qui reste visible dessous.
        public virtual bool Overlay => false;

        // Section de la barre latérale (module 13 : "home", "discover", "search", "favorites",
        // "history", "calendar", "settings") ; null si l'écran ne montre pas la barre.
        public virtual string SidebarSection => null;

        // Vrai si l'arrière-plan d'après l'image (Ui/ArtLayer.cs) se voit sous cet écran, quand
        // le réglage est mis : son fond est alors transparent (Theme.ScreenBack).
        public virtual bool ArtBackdrop => false;

        // Vrai pour un panneau qui disparaît en fondu de 0,2 s à sa fermeture (liste des liens,
        // bandes-annonces ; demande de l'auteur, 2026-11-18), comme il est apparu.
        public virtual bool FadeOut => false;

        // La barre latérale prend (true) puis rend (false) le focus de cet écran.
        public virtual void OnSidebar(bool focused) { }

        // Construit la vue une seule fois, à la première ouverture.
        internal View Create()
        {
            Root = Build();
            ApplyTexts();
            return Root;
        }

        protected abstract View Build();

        // Pose les textes de la langue en cours. Rappelée quand la langue change.
        public abstract void ApplyTexts();

        // L'écran redevient visible (ouverture, ou retour depuis l'écran du dessus).
        public virtual void OnShow() { }

        // L'écran est recouvert ou fermé.
        public virtual void OnHide() { }

        // Touche reçue par l'écran visible. Retour est traité par le Navigator.
        public virtual bool OnKey(string key) => false;

        // Touche relâchée (seul le lecteur s'en sert : vitesse maintenue).
        public virtual bool OnKeyUp(string key) => false;

        // Retour reçu par l'écran visible avant le Navigator. Renvoyer true pour
        // le garder (ex. sortir d'une saisie) au lieu de fermer l'écran.
        public virtual bool OnBack() => false;

        // L'écran est fermé pour de bon : libérer ce qui n'est pas une vue (minuteurs…).
        public virtual void OnClose() { }

        // L'application passe en arrière-plan (touche Home, TV éteinte) ou revient.
        public virtual void OnAppPause() { }
        public virtual void OnAppResume() { }

        // L'application se ferme : dernière chance d'envoyer quelque chose au serveur.
        public virtual void OnAppTerminate() { }
    }
}
