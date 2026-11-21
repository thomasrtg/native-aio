// Coins arrondis compatibles avec toutes les versions de Tizen.
// Sur Tizen 6.0 (API 8, celle de la construction), View.CornerRadius est un seul nombre ; les
// versions suivantes l'ont remplacé par quatre nombres (Vector4, un rayon par coin). Un appel direct,
// compilé pour l'API 8, fait fermer l'application dès le démarrage sur une TV plus récente
// (MissingMethodException sur set_CornerRadius(Single), vu sur l'émulateur Tizen 9).
// On cherche donc le réglage de la propriété une seule fois, au premier usage, et on l'appelle
// ensuite directement (délégué), sans coût sensible.
// Usage : new View { ... }.Rounded(r) ou vue.Rounded(r), à la place de CornerRadius = r.
using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NativeAIO.Ui
{
    public static class Corners
    {
        static readonly Action<View, float> Set = Find();

        static Action<View, float> Find()
        {
            var setter = typeof(View).GetProperty("CornerRadius")?.GetSetMethod();
            if (setter == null) return (v, r) => { };
            var type = setter.GetParameters()[0].ParameterType;
            if (type == typeof(float))
                return (Action<View, float>)Delegate.CreateDelegate(typeof(Action<View, float>), setter);
            if (type == typeof(Vector4))
            {
                var set4 = (Action<View, Vector4>)Delegate.CreateDelegate(typeof(Action<View, Vector4>), setter);
                return (v, r) => set4(v, new Vector4(r, r, r, r));
            }
            return (v, r) => { }; // forme inconnue : coins droits plutôt qu'une fermeture
        }

        /// <summary>Arrondit les quatre coins de la vue (rayon en pixels) et la rend, pour s'enchaîner.</summary>
        public static T Rounded<T>(this T view, float radius) where T : View
        {
            Set(view, radius);
            return view;
        }
    }
}
