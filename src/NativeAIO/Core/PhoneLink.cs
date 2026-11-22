// Connexion avec un téléphone (2026-11-22) : petit serveur ouvert au réseau local pendant que
// l'écran du QR code est affiché (essai de faisabilité vérifié sur la TV : LECONS.md).
// - Adresse du QR code : http://<adresse de la TV>:8125/?k=<code secret>. Le code est tiré au
//   hasard à chaque ouverture et meurt avec le serveur (connexion faite, écran quitté, délai).
// - GET / : page du téléphone (sans le bon code, la page ne montre que « code expiré ») ;
//   GET /logo.png : logo d'AIOStreams ; GET /code.ttf : police du code de connexion rapide
//   (Cousine, comme sur la TV) ; POST /<étape> : formulaire, réponse en JSON.
// - Chaque demande est traitée sur le fil d'interface par l'écran (handler) ; la suite éventuelle
//   (After, par exemple changer d'écran) ne part qu'une fois la réponse envoyée au téléphone.
// Les échanges passent en clair sur le réseau local (choix de l'auteur) : le mot de passe ne fait
// que traverser la TV, seul le jeton reçu du serveur est gardé, comme pour la saisie à la télécommande.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NativeAIO.Core
{
    class PhoneLinkError : Exception
    {
        public string Key { get; }
        public PhoneLinkError(string key, string detail = null) : base(detail ?? key) { Key = key; }
    }

    class PhoneLink
    {
        public const int Port = 8125;
        const string Letters = "abcdefghjkmnpqrstuvwxyz23456789"; // sans 0/o, 1/i/l

        public class Reply
        {
            public string Json;
            public Action After;
        }

        public delegate Task<Reply> Handler(string step, Dictionary<string, string> form);

        readonly HttpListener listener;
        readonly SynchronizationContext ui;
        readonly Handler handler;
        readonly Func<bool, string> page;   // page du téléphone, avec ou sans le bon code
        readonly byte[] logo, font;
        readonly string key;
        volatile bool stopped;

        public string Url { get; }

        PhoneLink(HttpListener listener, string ip, Handler handler, Func<bool, string> page)
        {
            this.listener = listener;
            this.handler = handler;
            this.page = page;
            ui = SynchronizationContext.Current;
            key = NewKey();
            Url = $"http://{ip}:{Port}/?k={key}";
            logo = Read("images/aiostreams.png");
            font = Read("fonts/Cousine-Regular.ttf");
        }

        static byte[] Read(string file)
        {
            try { return File.ReadAllBytes(Tizen.Applications.Application.Current.DirectoryInfo.Resource + file); }
            catch (Exception) { return new byte[0]; }
        }

        // À appeler sur le fil d'interface. Lève PhoneLinkError (pas d'adresse, port occupé…).
        public static PhoneLink Start(Handler handler, Func<bool, string> page)
        {
            string ip = LocalAddress() ?? throw new PhoneLinkError("phone.no_network");
            var l = new HttpListener();
            try
            {
                l.Prefixes.Add($"http://*:{Port}/");
                l.Start();
            }
            catch (Exception e)
            {
                try { l.Close(); } catch (Exception) { }
                throw new PhoneLinkError("phone.failed", e.GetType().Name + " : " + e.Message);
            }
            var link = new PhoneLink(l, ip, handler, page);
            Task.Run(link.Loop);
            Probe.Log("phone", "serveur démarré sur " + ip + ":" + Port);
            return link;
        }

        public void Stop()
        {
            if (stopped) return;
            stopped = true;
            try { listener.Stop(); listener.Close(); } catch (Exception) { }
            Probe.Log("phone", "serveur arrêté");
        }

        // Première adresse IPv4 du réseau local (ni 127.x, ni 169.254.x), Wi-Fi ou câble.
        static string LocalAddress()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address)) continue;
                        byte[] b = a.Address.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue;
                        return a.Address.ToString();
                    }
                }
            }
            catch (Exception e) { Probe.Log("phone", "adresses illisibles : " + e.GetType().Name); }
            return null;
        }

        static string NewKey()
        {
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var s = new StringBuilder();
            foreach (byte b in bytes) s.Append(Letters[b % Letters.Length]);
            return s.ToString();
        }

        async Task Loop()
        {
            while (!stopped)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { return; }
                _ = Task.Run(() => Serve(ctx));
            }
        }

        async Task Serve(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            string path = req.Url.AbsolutePath;
            try
            {
                if (req.HttpMethod == "GET" && (path == "/logo.png" || path == "/code.ttf"))
                {
                    ctx.Response.Headers["Cache-Control"] = "max-age=3600";
                    if (path == "/logo.png") await Send(ctx.Response, logo, "image/png");
                    else await Send(ctx.Response, font, "font/ttf");
                    return;
                }
                if (req.HttpMethod == "GET" && path == "/")
                {
                    bool good = req.QueryString["k"] == key && !stopped;
                    await Send(ctx.Response, Encoding.UTF8.GetBytes(page(good)), "text/html; charset=utf-8");
                    return;
                }
                if (req.HttpMethod != "POST" || path.Length < 2)
                {
                    ctx.Response.StatusCode = 404;
                    await Send(ctx.Response, new byte[0], "text/plain");
                    return;
                }

                string body;
                using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
                Dictionary<string, string> form = Parse(body);
                Reply reply;
                if (!form.TryGetValue("k", out string k) || k != key || stopped) reply = new Reply { Json = Json.Error(Lang.T("phone.invalid")) };
                else reply = await OnUi(path.Substring(1), form);
                await Send(ctx.Response, Encoding.UTF8.GetBytes(reply.Json), "application/json; charset=utf-8");
                if (reply.After != null) ui.Post(_ => reply.After(), null);
            }
            catch (Exception e)
            {
                Probe.Log("phone", "erreur : " + e.GetType().Name);
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch (Exception) { }
            }
        }

        // Fait traiter la demande par l'écran, sur le fil d'interface, et attend sa réponse.
        Task<Reply> OnUi(string step, Dictionary<string, string> form)
        {
            var done = new TaskCompletionSource<Reply>();
            ui.Post(async _ =>
            {
                try { done.SetResult(await handler(step, form)); }
                catch (Exception e)
                {
                    Probe.Log("phone", "erreur de l'écran : " + e.GetType().Name);
                    done.SetResult(new Reply { Json = Json.Error(Lang.T("error.unknown")) });
                }
            }, null);
            return done.Task;
        }

        static Dictionary<string, string> Parse(string body)
        {
            var form = new Dictionary<string, string>();
            foreach (string part in body.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                form[WebUtility.UrlDecode(part.Substring(0, eq))] = WebUtility.UrlDecode(part.Substring(eq + 1));
            }
            return form;
        }

        static async Task Send(HttpListenerResponse res, byte[] data, string type)
        {
            res.ContentType = type;
            res.ContentLength64 = data.Length;
            res.Headers["Cache-Control"] = res.Headers["Cache-Control"] ?? "no-store";
            await res.OutputStream.WriteAsync(data, 0, data.Length);
            res.Close();
        }

        // Petites réponses JSON écrites à la main.
        public static class Json
        {
            public static string Ok(string name = null) => name == null ? "{\"ok\":true}" : "{\"ok\":true,\"name\":" + Str(name) + "}";
            public static string Error(string text) => "{\"error\":" + Str(text) + "}";

            // Chaîne JSON, aussi sûre dans un <script> (« < » échappé).
            public static string Str(string s)
            {
                var b = new StringBuilder("\"");
                foreach (char c in s ?? "")
                {
                    switch (c)
                    {
                        case '"': b.Append("\\\""); break;
                        case '\\': b.Append("\\\\"); break;
                        case '\n': b.Append("\\n"); break;
                        case '\r': break;
                        case '<': b.Append("\\u003c"); break;
                        default:
                            if (c < 0x20 || c == 0x2028 || c == 0x2029) b.Append("\\u").Append(((int)c).ToString("x4"));
                            else b.Append(c);
                            break;
                    }
                }
                return b.Append('"').ToString();
            }
        }
    }
}
