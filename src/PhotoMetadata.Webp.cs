using System;
using System.IO;
using System.Threading;

namespace AtelierPC
{
    internal static partial class PhotoMetadata
    {
        static void WebpChunk(Stream output, string tag, byte[] data, int payloadOffset, int length)
        {
            Put(output, Ascii(tag));
            Write32(output, (uint)length, true);
            output.Write(data, payloadOffset, length);
            if ((length & 1) != 0)
                output.WriteByte(0);
        }

        static int WebpLength(byte[] data, int position, int end)
        {
            Require(position <= end - 8);
            int length = Size(data, position + 4, true);
            Require(length <= end - position - 8 && (length & 1) <= end - position - 8 - length);
            return length;
        }

        static byte[] WebpFrame(byte[] data, int payloadOffset, int payloadLength, ExifDisplay display)
        {
            Require(payloadLength >= 16 && (data[payloadOffset + 15] & 0xfc) == 0);
            using (var frame = new MemoryStream(payloadLength))
            {
                frame.Write(data, payloadOffset, 16);
                int position = payloadOffset + 16, frameEnd = payloadOffset + payloadLength;
                bool hasPixels = false, hasAlpha = false;
                while (position < frameEnd)
                {
                    int length = WebpLength(data, position, frameEnd);
                    string tag = Text(data, position, 4);
                    if (tag == "ALPH" || tag == "VP8L" || tag == "VP8 ")
                    {
                        Require(!hasPixels);
                        if (tag == "ALPH")
                        {
                            Require(!hasAlpha);
                            hasAlpha = true;
                        }
                        else
                        {
                            Require(!hasAlpha || tag == "VP8 ");
                            hasPixels = true;
                        }

                        WebpChunk(frame, tag, data, position + 8, length);
                    }
                    else if (tag == "ANMF" || tag == "EXIF" || tag == "ICCP")
                        throw new InvalidDataException(Unsupported);
                    else if (tag == "XMP ")
                        DescribeXmp(data, position + 8, length, display.Report);
                    else
                        display.Removed("Bloc annexe de trame WebP");
                    position += 8 + length + (length & 1);
                }

                Require(hasPixels);
                return frame.ToArray();
            }
        }

        static byte[] Webp(byte[] data, CancellationToken token, ExifDisplay display, out int frames)
        {
            Require(Text(data, 0, 4) == "RIFF" && Text(data, 8, 4) == "WEBP");
            int size = Size(data, 4, true);
            Require(size >= 4 && size <= data.Length - 8);
            int riffEnd = size + 8, position = 12, flags = 0;
            if (riffEnd < data.Length) display.Removed("Données après l’image");
            bool hasExtendedHeader = false, hasAnimation = false, hasPixels = false, hasAlpha = false, hasProfile = false;
            frames = 0;
            using (var output = new MemoryStream(riffEnd))
            {
                output.Write(data, 0, 12);
                while (position < riffEnd)
                {
                    token.ThrowIfCancellationRequested();
                    int payloadLength = WebpLength(data, position, riffEnd), payloadOffset = position + 8;
                    string tag = Text(data, position, 4);
                    switch (tag)
                    {
                        case "VP8X":
                            Require(position == 12 && payloadLength == 10 && (data[payloadOffset] & 0xc1) == 0 && data[payloadOffset + 1] == 0 && data[payloadOffset + 2] == 0 && data[payloadOffset + 3] == 0);
                            hasExtendedHeader = true;
                            flags = data[payloadOffset];
                            WebpChunk(output, tag, data, payloadOffset, payloadLength);
                            break;
                        case "ANIM":
                            Require(hasExtendedHeader && !hasAnimation && !hasPixels && payloadLength == 6 && (flags & 2) != 0);
                            hasAnimation = true;
                            WebpChunk(output, tag, data, payloadOffset, payloadLength);
                            break;
                        case "ANMF":
                            Require(hasAnimation && !hasPixels);
                            byte[] frame = WebpFrame(data, payloadOffset, payloadLength, display);
                            frames++;
                            WebpChunk(output, tag, frame, 0, frame.Length);
                            break;
                        case "ALPH":
                            Require(hasExtendedHeader && !hasAnimation && !hasPixels && !hasAlpha);
                            hasAlpha = true;
                            WebpChunk(output, tag, data, payloadOffset, payloadLength);
                            break;
                        case "VP8 ":
                        case "VP8L":
                            Require(!hasAnimation && !hasPixels && (!hasAlpha || tag == "VP8 "));
                            hasPixels = true;
                            WebpChunk(output, tag, data, payloadOffset, payloadLength);
                            break;
                        case "ICCP":
                            Require(hasExtendedHeader && !hasProfile && !hasPixels && !hasAnimation);
                            hasProfile = true;
                            var profile = CleanIcc(Slice(data, payloadOffset, payloadLength), display.Report);
                            WebpChunk(output, tag, profile, 0, profile.Length);
                            break;
                        case "XMP ":
                            Require(hasExtendedHeader);
                            ReadXmp(data, payloadOffset, payloadLength, display);
                            break;
                        case "EXIF":
                            Require(hasExtendedHeader);
                            ReadExif(data, payloadOffset, payloadLength, display);
                            break;
                        case "C2PA":
                            throw new InvalidDataException(Unsupported);
                        default:
                            display.Removed("Bloc annexe WebP");
                            break;
                    }

                    position = payloadOffset + payloadLength + (payloadLength & 1);
                }

                Require(hasAnimation ? frames > 0 : hasPixels && (flags & 2) == 0);
                if (!hasAnimation)
                    frames = 1;
                if (display.Needed)
                {
                    var exif = Exif(display);
                    WebpChunk(output, "EXIF", exif, 0, exif.Length);
                }

                if (hasExtendedHeader)
                {
                    output.Position = 20;
                    output.WriteByte((byte)((flags & ~0x2c) | (hasProfile ? 0x20 : 0) | (display.Needed ? 8 : 0)));
                }

                output.Position = 4;
                Write32(output, (uint)(output.Length - 8), true);
                return output.ToArray();
            }
        }
    }
}
