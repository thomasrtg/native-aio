// Écriture d'une image PNG (RGB ou RGBA, 8 bits) : arrière-plan d'après l'image et ses
// masques (Ui/ArtWash.cs), calculés par l'application puis affichés comme un fichier.
// Compression par DeflateStream, enveloppe zlib et sommes de contrôle écrites à la main.
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace NativeAIO.Core
{
    static class Png
    {
        // pixels : lignes de w × channels octets (1 = gris, 3 = RGB, 4 = RGBA), de haut en bas.
        public static void Write(string path, int w, int h, int channels, byte[] pixels)
        {
            byte[] raw = new byte[(w * channels + 1) * h];
            for (int y = 0; y < h; y++)
            {
                int o = y * (w * channels + 1);
                raw[o] = 0; // pas de filtre
                Buffer.BlockCopy(pixels, y * w * channels, raw, o + 1, w * channels);
            }
            byte[] packed;
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x01); // en-tête zlib
                using (var z = new DeflateStream(ms, CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
                uint adler = Adler32(raw);
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                packed = ms.ToArray();
            }
            string tmp = path + ".tmp";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var head = new byte[13];
                Big(head, 0, (uint)w);
                Big(head, 4, (uint)h);
                head[8] = 8;                               // bits par composante
                head[9] = (byte)(channels == 4 ? 6 : channels == 1 ? 0 : 2); // RGBA, gris ou RGB
                Chunk(f, "IHDR", head);
                Chunk(f, "IDAT", packed);
                Chunk(f, "IEND", new byte[0]);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            Big(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);
            byte[] t = Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc(Crc(0xFFFFFFFF, t), data) ^ 0xFFFFFFFF;
            var c = new byte[4];
            Big(c, 0, crc);
            s.Write(c, 0, 4);
        }

        static void Big(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
        }

        static uint[] table;

        static uint Crc(uint crc, byte[] data)
        {
            if (table == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                table = t;
            }
            foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }
    }
}
