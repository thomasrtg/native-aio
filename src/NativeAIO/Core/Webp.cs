// Recherche, sans rien ajouter au paquet, d'une bibliothèque WebP fournie par le
// système de la TV (libwebp). Si elle existe, les images WebP du serveur pourront
// être converties dans un format que la TV affiche. Ici, on ne fait que la chercher
// et lire sa version : le résultat s'affiche dans le panneau de mesures.
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NativeAIO.Core
{
    static class Webp
    {
        // « non cherchée », « absente », ou nom du fichier trouvé et version.
        public static string SystemLibrary { get; private set; } = "…";

        [DllImport("libdl.so.2")] static extern IntPtr dlopen(string file, int mode);
        [DllImport("libdl.so.2")] static extern IntPtr dlsym(IntPtr handle, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int VersionFn();

        static readonly string[] Names =
        {
            "libwebp.so", "libwebp.so.7", "libwebp.so.6", "libwebp.so.5", "libwebp.so.4",
            "/usr/lib/libwebp.so.7", "/usr/lib/libwebp.so.6", "/usr/lib/libwebp.so.5",
            "/usr/lib/libwebp.so.4", "/usr/lib/libwebp.so",
        };

        public static void Check()
        {
            Task.Run(() =>
            {
                try
                {
                    foreach (string name in Names)
                    {
                        IntPtr h = dlopen(name, 2); // RTLD_NOW
                        if (h == IntPtr.Zero) continue;
                        string found = name;
                        IntPtr v = dlsym(h, "WebPGetDecoderVersion");
                        IntPtr dec = dlsym(h, "WebPDecodeRGBA");
                        if (v != IntPtr.Zero)
                        {
                            int n = Marshal.GetDelegateForFunctionPointer<VersionFn>(v)();
                            found += $" v{(n >> 16) & 0xFF}.{(n >> 8) & 0xFF}.{n & 0xFF}";
                        }
                        found += dec != IntPtr.Zero ? ", WebPDecodeRGBA" : ", sans WebPDecodeRGBA";
                        SystemLibrary = found;
                        return;
                    }
                    SystemLibrary = "-";
                }
                catch (Exception e) { SystemLibrary = "× " + e.GetType().Name; }
            });
        }
    }
}
