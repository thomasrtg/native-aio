// Lecture vue du serveur, comme la version web (lib/playback/stream.ts et reporter.ts) :
// adresse du flux d'une version, et avancement envoyé comme le fait un client Jellyfin
// (début, toutes les 10 s, pause, reprise, changement de piste, arrêt), pour que le
// titre reprenne au bon endroit et soit marqué vu.
using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace NativeAIO.Core
{
    static class Playback
    {
        public const long TicksPerMs = 10000;

        // L'adresse propre de la version si c'est un lien http, sinon la route de lecture du
        // serveur. Le lecteur ne peut pas envoyer l'en-tête de connexion : le jeton passe
        // dans l'adresse. Cette adresse contient des clés : ne jamais l'afficher.
        public static string StreamUrl(string itemId, Source source)
        {
            if (source.Path != null && (source.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || source.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return source.Path;
            return Session.Base + $"/Videos/{itemId}/stream?static=true&MediaSourceId={Uri.EscapeDataString(source.Id ?? "")}&ApiKey={Uri.EscapeDataString(Session.Token ?? "")}";
        }

        // Dernière lecture arrêtée, relue par la page du titre à son retour pour mettre à jour
        // « Reprendre à… » sans redemander la fiche au serveur.
        public static string LastItemId;
        public static long LastPositionTicks;
        public static bool LastPlayed;
        // Lecteur passé seul à l'épisode suivant (module 7) : la page du titre recharge la saison
        // du dernier épisode lu, après l'envoi de son arrêt (LastStop).
        public static bool Chained;
        public static Item LastItem;
        public static Task LastStop = Task.CompletedTask;
    }

    // Envois au serveur pendant une lecture. Les erreurs sont ignorées, comme dans la
    // version web : une lecture ne doit jamais s'arrêter parce qu'un envoi a échoué.
    class Reporter
    {
        readonly string itemId, sourceId, session;
        bool stopped;
        public int Sent, Failed;

        public Reporter(string itemId, string sourceId, string playSessionId)
        {
            this.itemId = itemId;
            this.sourceId = sourceId ?? "";
            session = playSessionId;
        }

        PlaybackReport Body(long ms, bool paused, int? audio, int? subtitle, string evt) => new PlaybackReport
        {
            ItemId = itemId,
            MediaSourceId = sourceId,
            PlaySessionId = session,
            PositionTicks = Math.Max(0, ms) * Playback.TicksPerMs,
            IsPaused = paused,
            AudioStreamIndex = audio,
            SubtitleStreamIndex = subtitle,
            CanSeek = true,
            PlayMethod = "DirectPlay",
            EventName = evt,
        };

        public void Start(long ms, bool paused, int? audio, int? subtitle) =>
            Send("/Sessions/Playing", Body(ms, paused, audio, subtitle, null));

        // evt : TimeUpdate, Pause, Unpause, AudioTrackChange, SubtitleTrackChange.
        public void Progress(string evt, long ms, bool paused, int? audio, int? subtitle)
        {
            if (stopped) return;
            Send("/Sessions/Playing/Progress", Body(ms, paused, audio, subtitle, evt));
        }

        // Arrêt : envoyé une seule fois. Renvoie la tâche pour pouvoir l'attendre à la fermeture
        // de l'application.
        public Task Stop(long ms, int? audio, int? subtitle)
        {
            if (stopped) return Task.CompletedTask;
            stopped = true;
            return Send("/Sessions/Playing/Stopped", Body(ms, false, audio, subtitle, null));
        }

        Task Send(string path, PlaybackReport body)
        {
            Api client = Session.Client;
            // Hors du fil d'interface : la fermeture de l'application peut attendre cette tâche
            // sans bloquer la réponse, qui ne repasse pas par le fil d'interface.
            return Task.Run(async () =>
            {
                try { await client.Post<ErrorBody>(path, body).ConfigureAwait(false); Sent++; }
                catch (Exception) { Failed++; }
            });
        }
    }

    [DataContract]
    public class PlaybackReport
    {
        [DataMember] public string ItemId;
        [DataMember] public string MediaSourceId;
        [DataMember(EmitDefaultValue = false)] public string PlaySessionId;
        [DataMember] public long PositionTicks;
        [DataMember] public bool IsPaused;
        [DataMember(EmitDefaultValue = false)] public int? AudioStreamIndex;
        [DataMember(EmitDefaultValue = false)] public int? SubtitleStreamIndex;
        [DataMember] public bool CanSeek;
        [DataMember] public string PlayMethod;
        [DataMember(EmitDefaultValue = false)] public string EventName;
    }
}
