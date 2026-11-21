// Générateur de QR code (module 18, rapport de plantage) : texte en octets (UTF-8), correction
// d'erreur faible (L, ~7 %) pour loger le plus de texte, version (taille) la plus petite qui suffit.
// Traduit en C# et réduit (mode octets et niveau L seulement, calcul de pénalité des masques
// simplifié d'après la norme) depuis la bibliothèque de Project Nayuki :
//
//   QR Code generator library
//   Copyright (c) Project Nayuki. (MIT License)
//   https://www.nayuki.io/page/qr-code-generator-library
//
//   Permission is hereby granted, free of charge, to any person obtaining a copy of
//   this software and associated documentation files (the "Software"), to deal in
//   the Software without restriction, including without limitation the rights to
//   use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
//   the Software, and to permit persons to whom the Software is furnished to do so,
//   subject to the following conditions:
//   - The above copyright notice and this permission notice shall be included in
//     all copies or substantial portions of the Software.
//   - The Software is provided "as is", without warranty of any kind, express or
//     implied, including but not limited to the warranties of merchantability,
//     fitness for a particular purpose and noninfringement. In no event shall the
//     authors or copyright holders be liable for any claim, damages or other
//     liability, whether in an action of contract, tort or otherwise, arising from,
//     out of or in connection with the Software or the use or other dealings in the
//     Software.
using System;
using System.Collections.Generic;
using System.Text;

namespace NativeAIO.Core
{
    sealed class QrCode
    {
        public int Version { get; }
        public int Size { get; }
        readonly bool[,] modules;    // [y, x], vrai = carré noir
        readonly bool[,] isFunction; // motifs fixes, jamais masqués

        public bool this[int x, int y] => modules[y, x];

        // Niveau L. Index = version (1 à 40).
        static readonly int[] EccPerBlock = { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 };
        static readonly int[] EccBlocks = { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 };
        const int FormatBitsL = 1;

        // Nombre d'octets que contient au plus un QR code (version 40, niveau L).
        public const int MaxBytes = 2953;

        public static QrCode FromText(string text) => FromBytes(Encoding.UTF8.GetBytes(text));

        public static QrCode FromBytes(byte[] data)
        {
            int version = 1;
            for (; ; version++)
            {
                if (version > 40) throw new ArgumentException("texte trop long pour un QR code");
                int used = 4 + (version < 10 ? 8 : 16) + data.Length * 8;
                if (used <= DataCodewords(version) * 8) break;
            }
            int capacity = DataCodewords(version) * 8;
            var bits = new List<bool>();
            Append(bits, 0x4, 4); // mode octets
            Append(bits, data.Length, version < 10 ? 8 : 16);
            foreach (byte b in data) Append(bits, b, 8);
            Append(bits, 0, Math.Min(4, capacity - bits.Count)); // fin
            Append(bits, 0, (8 - bits.Count % 8) % 8);
            for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(bits, pad, 8);

            var codewords = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            return new QrCode(version, codewords);
        }

        static void Append(List<bool> bits, int value, int length)
        {
            for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        QrCode(int version, byte[] dataCodewords)
        {
            Version = version;
            Size = version * 4 + 17;
            modules = new bool[Size, Size];
            isFunction = new bool[Size, Size];
            DrawFunctionPatterns();
            DrawCodewords(AddEccAndInterleave(dataCodewords));

            int best = 0, bestPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(mask);
                DrawFormatBits(mask);
                int p = Penalty();
                if (p < bestPenalty) { best = mask; bestPenalty = p; }
                ApplyMask(mask); // le masque s'annule en le réappliquant
            }
            ApplyMask(best);
            DrawFormatBits(best);
        }

        // ---- Motifs fixes ----

        void DrawFunctionPatterns()
        {
            for (int i = 0; i < Size; i++)
            {
                SetFunction(6, i, i % 2 == 0);
                SetFunction(i, 6, i % 2 == 0);
            }
            DrawFinder(3, 3);
            DrawFinder(Size - 4, 3);
            DrawFinder(3, Size - 4);

            int[] pos = AlignmentPositions();
            int n = pos.Length;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue;
                    DrawAlignment(pos[i], pos[j]);
                }
            DrawFormatBits(0); // réservé, réécrit après le choix du masque
            DrawVersion();
        }

        void DrawFormatBits(int mask)
        {
            int data = FormatBitsL << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
            SetFunction(8, 7, Bit(bits, 6));
            SetFunction(8, 8, Bit(bits, 7));
            SetFunction(7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

            for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
            SetFunction(8, Size - 8, true); // toujours noir
        }

        void DrawVersion()
        {
            if (Version < 7) return;
            int rem = Version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = Version << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = Bit(bits, i);
                int a = Size - 11 + i % 3, b = i / 3;
                SetFunction(a, b, bit);
                SetFunction(b, a, bit);
            }
        }

        void DrawFinder(int x, int y)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int xx = x + dx, yy = y + dy;
                    if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) SetFunction(xx, yy, dist != 2 && dist != 4);
                }
        }

        void DrawAlignment(int x, int y)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        void SetFunction(int x, int y, bool dark)
        {
            modules[y, x] = dark;
            isFunction[y, x] = true;
        }

        int[] AlignmentPositions()
        {
            if (Version == 1) return new int[0];
            int n = Version / 7 + 2;
            int step = (Version * 8 + n * 3 + 5) / (n * 4 - 4) * 2;
            var result = new int[n];
            result[0] = 6;
            for (int i = n - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
            return result;
        }

        // ---- Données et correction d'erreur ----

        static int RawDataModules(int ver)
        {
            int result = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int n = ver / 7 + 2;
                result -= (25 * n - 10) * n - 55;
                if (ver >= 7) result -= 36;
            }
            return result;
        }

        static int DataCodewords(int ver) => RawDataModules(ver) / 8 - EccPerBlock[ver] * EccBlocks[ver];

        byte[] AddEccAndInterleave(byte[] data)
        {
            int numBlocks = EccBlocks[Version], eccLen = EccPerBlock[Version];
            int raw = RawDataModules(Version) / 8;
            int numShort = numBlocks - raw % numBlocks;
            int shortLen = raw / numBlocks;

            byte[] divisor = RsDivisor(eccLen);
            var blocks = new byte[numBlocks][];
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int datLen = shortLen - eccLen + (i < numShort ? 0 : 1);
                var dat = new byte[datLen];
                Array.Copy(data, k, dat, 0, datLen);
                k += datLen;
                byte[] ecc = RsRemainder(dat, divisor);
                var block = new byte[shortLen + 1];
                Array.Copy(dat, block, datLen);
                Array.Copy(ecc, 0, block, block.Length - eccLen, eccLen);
                blocks[i] = block;
            }

            var result = new byte[raw];
            int r = 0;
            for (int i = 0; i < blocks[0].Length; i++)
                for (int j = 0; j < numBlocks; j++)
                    if (i != shortLen - eccLen || j >= numShort) result[r++] = blocks[j][i];
            return result;
        }

        static byte[] RsDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = (byte)RsMultiply(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = RsMultiply(root, 0x02);
            }
            return result;
        }

        static byte[] RsRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= (byte)RsMultiply(divisor[i], factor);
            }
            return result;
        }

        static int RsMultiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z;
        }

        void DrawCodewords(byte[] data)
        {
            int i = 0;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < Size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vert : vert;
                        if (!isFunction[y, x] && i < data.Length * 8)
                        {
                            modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                            i++;
                        }
                    }
            }
        }

        // ---- Masques ----

        void ApplyMask(int mask)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    if (isFunction[y, x]) continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (invert) modules[y, x] = !modules[y, x];
                }
        }

        // Pénalité de la norme : suites de 5 carrés ou plus, blocs 2×2, motifs semblables aux
        // repères, équilibre noir/blanc. Seul le choix du masque en dépend (lisibilité).
        int Penalty()
        {
            int result = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < Size; a++)
                {
                    int run = 1;
                    for (int b = 1; b < Size; b++)
                    {
                        if (Get(pass, a, b) == Get(pass, a, b - 1)) run++;
                        else { if (run >= 5) result += run - 2; run = 1; }
                    }
                    if (run >= 5) result += run - 2;
                    for (int b = 0; b + 11 <= Size; b++)
                        if (Matches(pass, a, b, Finder1) || Matches(pass, a, b, Finder2)) result += 40;
                }
            for (int y = 0; y < Size - 1; y++)
                for (int x = 0; x < Size - 1; x++)
                {
                    bool c = modules[y, x];
                    if (c == modules[y, x + 1] && c == modules[y + 1, x] && c == modules[y + 1, x + 1]) result += 3;
                }
            int dark = 0;
            foreach (bool m in modules) if (m) dark++;
            int total = Size * Size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            result += Math.Max(0, k) * 10;
            return result;
        }

        static readonly bool[] Finder1 = { true, false, true, true, true, false, true, false, false, false, false };
        static readonly bool[] Finder2 = { false, false, false, false, true, false, true, true, true, false, true };

        // pass 0 : ligne a, colonne b ; pass 1 : colonne a, ligne b.
        bool Get(int pass, int a, int b) => pass == 0 ? modules[a, b] : modules[b, a];

        bool Matches(int pass, int a, int b, bool[] pattern)
        {
            for (int i = 0; i < pattern.Length; i++) if (Get(pass, a, b + i) != pattern[i]) return false;
            return true;
        }

        static bool Bit(int x, int i) => ((x >> i) & 1) != 0;
    }
}
