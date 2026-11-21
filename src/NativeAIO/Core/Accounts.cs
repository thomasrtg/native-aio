// Utilisateurs de la configuration (« Qui regarde ? ») : liste demandée une fois par
// utilisateur connecté, partagée par la barre latérale (photo du profil, changement
// d'utilisateur) et les Paramètres.
using System;
using System.Threading.Tasks;
using NativeAIO.Screens;

namespace NativeAIO.Core
{
    static class Accounts
    {
        static Task<PickableUser[]> users;
        static string usersFor;

        public static Task<PickableUser[]> Users()
        {
            if (users == null || usersFor != Session.UserId || users.IsFaulted || users.IsCanceled)
            {
                usersFor = Session.UserId;
                users = Session.Client.Get<PickableUser[]>("/AIOStreams/Users");
            }
            return users;
        }

        // Photo de l'utilisateur connecté (adresse http), ou null.
        public static string Avatar(PickableUser[] list)
        {
            if (list == null) return null;
            foreach (PickableUser u in list)
                if (u.User?.Id == Session.UserId && u.Avatar != null && u.Avatar.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return u.Avatar;
            return null;
        }

        static bool busy;

        // Ouvre « Qui regarde ? ». Les messages (chargement, un seul utilisateur, erreur)
        // passent par say ; from : l'écran qui demande (rien n'est fait s'il est fermé entre-temps).
        public static async void Switch(Screen from, Action<Func<string>> say)
        {
            if (busy) return;
            busy = true;
            say(() => Lang.T("home.loading_users"));
            try
            {
                PickableUser[] list = await Users();
                if (from.Closed) return;
                if (list == null || list.Length < 2) say(() => Lang.T("home.single_user"));
                else { say(null); from.Nav.Push(new WhoScreen(list)); }
            }
            catch (ApiError e) { if (!from.Closed) say(() => SignInScreen.ErrorText(e)); }
            catch (Exception) { if (!from.Closed) say(() => Lang.T("error.unknown")); }
            finally { busy = false; }
        }

        public static void SignOut(Screen from)
        {
            users = null;
            Session.SignOut();
            from.Nav.Reset(new SignInScreen());
        }
    }
}
