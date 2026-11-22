// QR code dessiné en PNG gris pour la TV (rapport de plantage, connexion avec un téléphone) :
// carrés noirs sur fond blanc, marge blanche de 4 carrés comprise, 4 à 10 px par carré selon la
// place. Un PNG affiché à sa taille dans un ImageView reste net (LECONS.md).
using System;
using System.IO;

namespace NativeAIO.Core
{
    static class QrImage
    {
        const int Quiet = 4;

        // Écrit l'image dans le cache, dossier « folder » : un nouveau nom à chaque fois (la TV
        // garde les images déjà lues par leur nom), les anciennes effacées. Rend le fichier et le côté.
        public static (string File, int Side) Write(string text, int max, string folder)
        {
            QrCode qr = QrCode.FromText(text);
            int n = qr.Size + 2 * Quiet;
            int scale = Math.Max(4, Math.Min(10, max / n));
            int side = n * scale;
            var px = new byte[side * side];
            for (int i = 0; i < px.Length; i++) px[i] = 255;
            for (int y = 0; y < qr.Size; y++)
                for (int x = 0; x < qr.Size; x++)
                {
                    if (!qr[x, y]) continue;
                    for (int dy = 0; dy < scale; dy++)
                    {
                        int row = ((y + Quiet) * scale + dy) * side + (x + Quiet) * scale;
                        for (int dx = 0; dx < scale; dx++) px[row + dx] = 0;
                    }
                }
            string dir = Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.Cache, folder);
            Directory.CreateDirectory(dir);
            foreach (string old in Directory.GetFiles(dir)) { try { File.Delete(old); } catch (Exception) { } }
            string file = Path.Combine(dir, "qr-" + DateTime.UtcNow.Ticks + ".png");
            Png.Write(file, side, side, 1, px);
            Probe.Log(folder, $"QR code version {qr.Version}, {text.Length} caractères, {side} px");
            return (file, side);
        }
    }
}
