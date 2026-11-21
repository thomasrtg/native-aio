// Pile des écrans. Ouvrir un écran le pose au-dessus et cache le précédent ;
// Retour détruit l'écran visible et fait réapparaître celui de dessous, dans
// l'état où on l'a laissé (focus compris).
using System;
using System.Collections.Generic;
using Tizen.NUI.BaseComponents;

namespace NativeAIO.Core
{
    class Navigator
    {
        readonly View host;
        readonly List<Screen> stack = new List<Screen>();

        public Navigator(View host) { this.host = host; }

        public Screen Top => stack.Count > 0 ? stack[stack.Count - 1] : null;
        public int Depth => stack.Count;

        // Après chaque changement d'écran visible (la barre latérale suit).
        public event Action Changed;

        public void Push(Screen screen)
        {
            Screen below = Top;
            if (below != null)
            {
                below.OnHide();
                // Un panneau laisse voir l'écran de dessous, même caché par un écran remplacé
                // (lecteur → liste des liens de l'épisode suivant).
                if (!screen.Overlay) below.Root.Hide(); else below.Root.Show();
            }
            screen.Nav = this;
            host.Add(screen.Create());
            stack.Add(screen);
            screen.OnShow();
            Changed?.Invoke();
        }

        // Ferme l'écran visible. Renvoie false s'il n'y a rien dessous (à l'appelant de quitter).
        public bool Back()
        {
            if (stack.Count <= 1) return false;
            CloseTop();
            Screen below = Top;
            below.Root.Show();
            below.OnShow();
            Changed?.Invoke();
            return true;
        }

        // Barre latérale : revient au premier écran (l'accueil) et ouvre éventuellement une
        // section au-dessus. Retour depuis une section ramène donc toujours à l'accueil.
        public void Section(Screen screen)
        {
            while (stack.Count > 1) CloseTop();
            if (screen != null) { Push(screen); return; }
            Top.Root.Show();
            Top.OnShow();
            Changed?.Invoke();
        }

        // Ferme l'écran visible et ouvre celui-ci à sa place (ex. le lecteur à la place de la
        // liste des liens : Retour ramène alors à la page du titre).
        public void Replace(Screen screen)
        {
            if (stack.Count > 0) CloseTop();
            Push(screen);
        }

        // Ferme tous les écrans et ouvre celui-ci : il devient le premier (Retour y quitte).
        public void Reset(Screen screen)
        {
            while (stack.Count > 0) CloseTop();
            Push(screen);
        }

        void CloseTop()
        {
            Screen top = Top;
            stack.RemoveAt(stack.Count - 1);
            top.Closed = true;
            top.OnHide();
            top.OnClose();
            if (top.FadeOut) { Fade(top.Root); return; }
            host.Remove(top.Root);
            top.Root.Dispose();
        }

        // Fondu de fermeture : la vue reste posée (sans plus recevoir de touche) le temps du
        // fondu, puis elle est retirée. Les animations sont gardées jusque-là.
        readonly List<Tizen.NUI.Animation> fades = new List<Tizen.NUI.Animation>();

        void Fade(View root)
        {
            root.RaiseToTop();
            var a = new Tizen.NUI.Animation(200);
            a.AnimateTo(root, "Opacity", 0f);
            a.Finished += (o, e) =>
            {
                fades.Remove(a);
                host.Remove(root);
                root.Dispose();
            };
            fades.Add(a);
            a.Play();
        }

        public bool OnKey(string key) => Top != null && Top.OnKey(key);

        // Après un changement de langue : tous les écrans de la pile relisent leurs textes.
        public void ApplyTexts()
        {
            foreach (Screen s in stack) s.ApplyTexts();
        }
    }
}
