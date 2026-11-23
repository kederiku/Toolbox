using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal static partial class Tests
    {
        const string RedJpeg = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8QEBEQCgwSExIQEw8QEBD/2wBDAQMDAwQDBAgEBAgQCwkLEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBD/wAARCAACAAMDAREAAhEBAxEB/8QAFAABAAAAAAAAAAAAAAAAAAAACP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/xAAVAQEBAAAAAAAAAAAAAAAAAAAHCf/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/ADoDFU3/2Q==";
        const string RedPng = "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAYAAACddGYaAAAAE0lEQVQI12P8z8DwnwEKmBiQAAAvHwICniTpQwAAAABJRU5ErkJggg==";
        // Two lossless frames (red, blue), 70 and 190 ms, four loops.
        const string AnimatedWebp = "UklGRoQAAABXRUJQVlA4WAoAAAACAAAAAgAAAQAAQU5JTQYAAAD/////BABBTk1GKAAAAAAAAAAAAAIAAAEAAEYAAAJWUDhMDwAAAC8CQAAABxD9j/4HIqL/AQBBTk1GKAAAAAAAAAAAAAIAAAEAAL4AAABWUDhMDwAAAC8CQAAABxDR//4HIqL/AQA=";

        static byte[] PhotoJoin(params byte[][] arrays) { return arrays.SelectMany(b => b).ToArray(); }
        static byte[] PhotoText(string text) { return Encoding.ASCII.GetBytes(text); }
        static byte[] PhotoU32(int n) { return new byte[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n }; }

        static byte[] PhotoTiff(string marker)
        {
            byte[] model = PhotoText(marker + "-model\0"), artist = PhotoText(marker + "-artist\0");
            const int strings = 62;
            int gps = strings + model.Length + artist.Length;
            if ((gps & 1) != 0) gps++;
            using (var data = new MemoryStream())
            using (var w = new BinaryWriter(data))
            {
                w.Write(PhotoText("II")); w.Write((ushort)42); w.Write(8); w.Write((ushort)4);
                Action<ushort, ushort, int, int> entry = (tag, type, count, value) => {
                    w.Write(tag); w.Write(type); w.Write(count); w.Write(value);
                };
                entry(0x0110, 2, model.Length, strings); entry(0x0112, 3, 1, 6);
                entry(0x013b, 2, artist.Length, strings + model.Length); entry(0x8825, 4, 1, gps);
                w.Write(0); w.Write(model); w.Write(artist);
                if (data.Position < gps) w.Write((byte)0);
                w.Write((ushort)2); entry(1, 2, 2, 'N'); entry(2, 5, 3, gps + 30); w.Write(0);
                foreach (int n in new[] { 48, 1, 51, 1, 29, 1 }) w.Write(n);
                return data.ToArray();
            }
        }

        static byte[] PhotoJpegSegment(int marker, byte[] payload)
        {
            int n = payload.Length + 2;
            return PhotoJoin(new byte[] { 255, (byte)marker, (byte)(n >> 8), (byte)n }, payload);
        }

        internal static byte[] MetadataTestJpeg(string marker)
        {
            byte[] jpg = Convert.FromBase64String(RedJpeg);
            return PhotoJoin(jpg.Take(20).ToArray(), PhotoJpegSegment(225, PhotoJoin(PhotoText("Exif\0\0"), PhotoTiff(marker))),
                PhotoJpegSegment(254, PhotoText(marker + "-comment")), jpg.Skip(20).ToArray());
        }

        static byte[] PhotoPngChunk(string tag, byte[] payload)
        {
            byte[] bytes = PhotoJoin(PhotoText(tag), payload);
            uint crc = 0xffffffff;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            return PhotoJoin(PhotoU32(payload.Length), bytes, PhotoU32((int)~crc));
        }

        static byte[] PhotoCleaned(byte[] source, string extension, string marker, params string[] categories)
        {
            byte[] original = (byte[])source.Clone();
            var result = new MetadataResult();
            byte[] cleaned = PhotoMetadata.Clean(source, extension, CancellationToken.None, out result.PhotoProperties, result);
            Check(result.PhotoProperties && original.SequenceEqual(source), "Original intact et nettoyage signalé pour " + extension);
            Check(!Encoding.ASCII.GetString(cleaned).Contains(marker), "Marqueurs privés retirés de " + extension);
            string summary = result.Summary();
            foreach (string category in categories)
                Check(summary.Contains(category), "Bilan de " + extension + " : " + category);
            foreach (string category in new[] { "Coordonnées GPS", "Modèle d’appareil", "Auteur", "Titre", "Informations du profil ICC" })
                Check(summary.Contains(category) == categories.Contains(category), "Présence exacte dans le bilan de " + extension + " : " + category);
            Check(!summary.Contains(marker), "Aucune valeur privée dans le bilan de " + extension);
            var second = new MetadataResult { EmbeddedPhotos = 1 };
            string before = second.Summary();
            bool changed;
            byte[] reread = PhotoMetadata.Clean(cleaned, extension, CancellationToken.None, out changed, second);
            Check(!changed && reread.SequenceEqual(cleaned), "Deuxième passage stable pour " + extension);
            Check(second.Summary() == before, "Aucun retrait ajouté au second passage pour " + extension);
            return cleaned;
        }

        static BitmapFrame PhotoFrame(byte[] data)
        {
            return BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
        }

        static byte[] PhotoPixels(byte[] data)
        {
            var rgb = new FormatConvertedBitmap(PhotoFrame(data), PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[rgb.PixelWidth * rgb.PixelHeight * 4];
            rgb.CopyPixels(pixels, rgb.PixelWidth * 4, 0);
            return pixels;
        }

        static void PhotoMustReject(byte[] data, string ext)
        {
            bool refused = false, changed;
            try { PhotoMetadata.Clean(data, ext, CancellationToken.None, out changed); }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "Image illisible refusée pour " + ext);
        }

        internal static void CheckPhotoMetadata()
        {
            using (PhotoMetadata.BeginBatch()) CheckPhotoFormats();
        }

        static void CheckPhotoFormats()
        {
            const string marker = "PRIVATE-MARQUEUR";
            byte[] jpeg = MetadataTestJpeg(marker);
            var inputTags = (BitmapMetadata)PhotoFrame(jpeg).Metadata;
            Check(inputTags.ContainsQuery("/app1/ifd/gps/{ushort=2}") && inputTags.ContainsQuery("/app1/ifd/{ushort=272}"),
                "Les données EXIF d’essai contiennent un GPS et un modèle d’appareil");
            byte[] cleanJpeg = PhotoCleaned(jpeg, ".jpeg", marker, "Coordonnées GPS", "Modèle d’appareil", "Auteur", "Commentaire");
            var tags = (BitmapMetadata)PhotoFrame(cleanJpeg).Metadata;
            Check(Convert.ToInt32(tags.GetQuery("/app1/ifd/{ushort=274}")) == 6
                && !tags.ContainsQuery("/app1/ifd/gps/{ushort=2}") && !tags.ContainsQuery("/app1/ifd/{ushort=272}")
                && !tags.ContainsQuery("/app1/ifd/{ushort=315}"), "Orientation conservée, GPS, modèle et auteur effacés");
            Check(PhotoPixels(cleanJpeg).SequenceEqual(PhotoPixels(jpeg)), "Pixels JPEG inchangés");
            // Adobe RGB is sometimes signalled solely by ColorSpace=uncalibrated and InteropIndex=R03.
            byte[] colorTiff = {
                73,73,42,0,8,0,0,0, 1,0,105,135,4,0,1,0,0,0,26,0,0,0,0,0,0,0,
                2,0,1,160,3,0,1,0,0,0,255,255,0,0,5,160,4,0,1,0,0,0,56,0,0,0,0,0,0,0,
                1,0,1,0,2,0,4,0,0,0,82,48,51,0,0,0,0,0 };
            byte[] bareJpeg = Convert.FromBase64String(RedJpeg);
            byte[] bigEndianOrientation = {
                77,77,0,42,0,0,0,8, 0,1, 1,18,0,3,0,0,0,1,0,6,0,0, 0,0,0,0 };
            byte[] oriented = PhotoJoin(bareJpeg.Take(20).ToArray(), PhotoJpegSegment(225,
                PhotoJoin(PhotoText("Exif\0\0"), bigEndianOrientation)), bareJpeg.Skip(20).ToArray());
            var rewrite = new MetadataResult();
            var normalized = PhotoMetadata.Clean(oriented, ".jpg", CancellationToken.None, out rewrite.PhotoProperties, rewrite);
            Check(rewrite.PhotoProperties && rewrite.Summary() == new MetadataResult { PhotoProperties = true }.Summary()
                && Convert.ToInt32(((BitmapMetadata)PhotoFrame(normalized).Metadata).GetQuery("/app1/ifd/{ushort=274}")) == 6,
                "La normalisation de l’orientation EXIF seule ne signale aucun retrait");
            byte[] adobe = PhotoJoin(bareJpeg.Take(20).ToArray(), PhotoJpegSegment(225, PhotoJoin(PhotoText("Exif\0\0"), colorTiff)),
                bareJpeg.Skip(20).Take(bareJpeg.Length-22).ToArray(), PhotoJpegSegment(254,PhotoText(marker)),
                new byte[] { 255,217 });
            var colorTags = (BitmapMetadata)PhotoFrame(PhotoCleaned(adobe, ".jpg", marker, "Commentaire")).Metadata;
            Check(Convert.ToInt32(colorTags.GetQuery("/app1/ifd/exif/{ushort=40961}")) == 65535
                && Convert.ToString(colorTags.GetQuery("/app1/ifd/exif/interop/{ushort=1}")) == "R03",
                "Indication EXIF Adobe RGB conservée après un commentaire entre le scan et la fin du JPEG");

            byte[] png = Convert.FromBase64String(RedPng);
            byte[] gpsTimeOnly = PhotoTiff(marker);
            int gpsDirectory = BitConverter.ToInt32(gpsTimeOnly, 54);
            gpsTimeOnly[gpsDirectory + 14] = 7; // Replace GPSLatitude with GPSTimeStamp (also three rationals).
            PhotoCleaned(PhotoJoin(png.Take(33).ToArray(), PhotoPngChunk("eXIf", gpsTimeOnly), png.Skip(33).ToArray()),
                ".png", marker, "Répertoire GPS", "Modèle d’appareil", "Auteur");

            byte[] iptcData = PhotoJoin(new byte[] { 28, 2, 80, 0, (byte)marker.Length }, PhotoText(marker));
            byte[] iptc = PhotoJoin(PhotoText("Photoshop 3.0\0" + "8BIM"), new byte[] { 4, 4, 0, 0 },
                PhotoU32(iptcData.Length), iptcData, new byte[iptcData.Length % 2]);
            PhotoCleaned(PhotoJoin(bareJpeg.Take(20).ToArray(), PhotoJpegSegment(237, iptc), bareJpeg.Skip(20).ToArray()),
                ".jpg", marker, "IPTC", "Auteur");

            byte[] pngText = PhotoPngChunk("tEXt", PhotoText("Author\0" + marker));
            byte[] decorated = PhotoJoin(png.Take(33).ToArray(), pngText, png.Skip(33).ToArray(), PhotoText(marker));
            Check(PhotoCleaned(decorated, ".png", marker, "Auteur").SequenceEqual(png), "Couleur, transparence et flux PNG intacts");
            byte[] emptyXmp = PhotoText("<rdf:Description xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'" +
                " xmlns:exif='http://ns.adobe.com/exif/1.0/' xmlns:dc='http://purl.org/dc/elements/1.1/'" +
                " exif:GPSLatitude='' exif:GPSLongitude='  '><dc:creator><rdf:Seq/></dc:creator>" +
                "<dc:title><rdf:Alt><rdf:li xml:lang='fr'>  </rdf:li></rdf:Alt></dc:title></rdf:Description>");
            byte[] emptyXmpChunk = PhotoPngChunk("iTXt", PhotoJoin(PhotoText("XML:com.adobe.xmp\0\0\0\0\0"), emptyXmp));
            PhotoCleaned(PhotoJoin(png.Take(33).ToArray(), emptyXmpChunk, png.Skip(33).ToArray()), ".png", marker, "XMP");
            byte[] blankTiff = PhotoTiff(marker);
            Array.Clear(blankTiff, 62, BitConverter.ToInt32(blankTiff, 54) - 62);
            byte[] emptyAuthor = PhotoPngChunk("iTXt", PhotoText("Author\0\0\0fr\0Auteur traduit\0   "));
            byte[] emptyTitle = PhotoPngChunk("tEXt", PhotoText("Title\0 "));
            byte[] emptyCompressedAuthor = PhotoPngChunk("zTXt", PhotoJoin(PhotoText("Author\0\0"), new byte[] {120,156,3,0,0,0,0,1}));
            PhotoCleaned(PhotoJoin(png.Take(33).ToArray(), emptyAuthor, emptyTitle, emptyCompressedAuthor,
                PhotoPngChunk("eXIf", blankTiff), png.Skip(33).ToArray()), ".png", marker, "Coordonnées GPS");

            byte[] frame = PhotoJoin(PhotoU32(0), PhotoU32(3), PhotoU32(2), new byte[8], new byte[] { 0, 9, 0, 100, 0, 0 });
            byte[] frame2 = (byte[])frame.Clone(); frame2[3] = 1;
            byte[] apng = PhotoJoin(png.Take(33).ToArray(), PhotoPngChunk("acTL", PhotoJoin(PhotoU32(2), PhotoU32(3))),
                PhotoPngChunk("fcTL", frame), png.Skip(33).Take(31).ToArray(), PhotoPngChunk("fcTL", frame2),
                PhotoPngChunk("fdAT", PhotoJoin(PhotoU32(2), png.Skip(41).Take(19).ToArray())), png.Skip(64).ToArray());
            Check(PhotoCleaned(PhotoJoin(apng.Take(33).ToArray(), pngText, apng.Skip(33).ToArray()), ".png", marker, "Auteur")
                .SequenceEqual(apng), "Deux trames APNG et temporisation intactes");

            byte[] webp = Convert.FromBase64String(AnimatedWebp);
            byte[] xmp = PhotoText("<rdf:Description xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'" +
                " xmlns:dc='http://purl.org/dc/elements/1.1/' xmlns:t='http://ns.adobe.com/tiff/1.0/'" +
                " t:Model='" + marker + "'><dc:creator><rdf:Seq><rdf:li>" + marker + "</rdf:li></rdf:Seq></dc:creator>" +
                "<dc:title><rdf:Alt><rdf:li xml:lang='fr' rdf:resource='urn:test'/></rdf:Alt></dc:title>" +
                "<GPSLatitude>fake</GPSLatitude></rdf:Description>");
            byte[] marked = PhotoJoin(webp, PhotoText("XMP "), BitConverter.GetBytes(xmp.Length), xmp, new byte[xmp.Length % 2]);
            BitConverter.GetBytes(marked.Length - 8).CopyTo(marked, 4); marked[20] |= 4;
            Check(PhotoCleaned(marked, ".webp", marker, "XMP", "Auteur", "Titre", "Modèle d’appareil").SequenceEqual(webp), "Deux trames WebP, délais et boucle intacts");
            byte[] xmpGps = PhotoText("<rdf:Description xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'" +
                " xmlns:exif='http://ns.adobe.com/exif/1.0/' exif:GPSLatitude='48,0N' exif:UserComment='" + marker + "'/>");
            byte[] pngXmp = PhotoPngChunk("iTXt", PhotoJoin(PhotoText("XML:com.adobe.xmp\0\0\0\0\0"), xmpGps));
            PhotoCleaned(PhotoJoin(png.Take(33).ToArray(), pngXmp, png.Skip(33).ToArray()),
                ".png", marker, "Coordonnées GPS", "Commentaire", "XMP");
            // Unknown RIFF chunks can contain descriptions inside individual animation frames.
            byte[] note = PhotoText(marker);
            byte[] nested = PhotoJoin(PhotoText("NOTE"), BitConverter.GetBytes(note.Length), note, new byte[note.Length % 2]);
            byte[] frameNote = PhotoJoin(webp.Take(92).ToArray(), nested, webp.Skip(92).ToArray());
            BitConverter.GetBytes(frameNote.Length - 8).CopyTo(frameNote, 4);
            BitConverter.GetBytes(40 + nested.Length).CopyTo(frameNote, 48);
            Check(PhotoCleaned(frameNote, ".webp", marker).SequenceEqual(webp), "Description privée retirée au sein d’une trame WebP");

            // Hide private device / author text in a real monitor profile. Its TRCs and primaries must survive.
            string profiles = Path.Combine(Environment.SystemDirectory, @"spool\drivers\color");
            byte[] profile = File.ReadAllBytes(Path.Combine(profiles, "sRGB Color Space Profile.icm"));
            int descriptive = Encoding.ASCII.GetString(profile).IndexOf("sRGB IEC61966", StringComparison.Ordinal);
            Check(descriptive >= 0, "Profil sRGB de référence");
            PhotoText(marker).CopyTo(profile, descriptive);
            PhotoText("CAM1MOD1").CopyTo(profile, 48);
            PhotoText("DEV7").CopyTo(profile, 56);
            byte[] withIcc = PhotoJoin(jpeg.Take(20).ToArray(), PhotoJpegSegment(226,
                PhotoJoin(PhotoText("ICC_PROFILE\0"), new byte[] { 1, 1 }, profile)), jpeg.Skip(20).ToArray());
            byte[] iccJpeg = PhotoCleaned(withIcc, ".jpg", marker, "Coordonnées GPS", "Modèle d’appareil", "Auteur", "Informations du profil ICC");
            Check(!Encoding.ASCII.GetString(iccJpeg).Contains("CAM1MOD1") && !Encoding.ASCII.GetString(iccJpeg).Contains("DEV7")
                && PhotoFrame(iccJpeg).ColorContexts.Count == 1
                && PhotoPixels(iccJpeg).SequenceEqual(PhotoPixels(withIcc)), "Profil ICC nettoyé, couleurs conservées");
            PhotoMustReject(jpeg.Take(jpeg.Length - 2).ToArray(), ".jpg");
            byte[] corrupt = (byte[])png.Clone(); corrupt[41] ^= 0xff;
            PhotoMustReject(corrupt, ".png");
            PhotoMustReject(PhotoJoin(png.Take(33).ToArray(), PhotoPngChunk("IDAT", new byte[19]), png.Skip(64).ToArray()), ".png");
            PhotoMustReject(webp.Take(41).ToArray(), ".webp");
            byte[] unreadableFrame = (byte[])webp.Clone();
            int firstFrame = Encoding.ASCII.GetString(webp).IndexOf("VP8L", StringComparison.Ordinal);
            unreadableFrame[firstFrame + 8] = 0;
            PhotoMustReject(unreadableFrame, ".webp");
            PhotoMustReject(png, ".jpg");
        }
    }
}
