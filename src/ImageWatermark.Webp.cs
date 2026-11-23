using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AtelierPC
{
    internal sealed partial class ImageWatermark
    {
        // libwebp can merge identical consecutive frames during encoding. In that case
        // keep the source's frame boundaries with full-canvas, independently encoded frames.
        void WriteSeparateWebpFrames(string folder, string output, WatermarkOptions options, CancellationToken token)
        {
            engine.Run(input + " -coalesce -auto-orient +repage" + DrawCommand(folder, frames[0], options) +
                " -depth 8 -define webp:lossless=true +adjoin WEBP:" + folder + "/frame-%05d.webp", WriteError, token);
            using (var source = new BinaryReader(File.OpenRead(Path.Combine(engine.DirectoryPath, input))))
            using (var writer = new BinaryWriter(File.Create(Path.Combine(engine.DirectoryPath, output))))
            {
                int profile = FindChunk(source, "ICCP");
                long profileStart = source.BaseStream.Position;
                int animation = FindChunk(source, "ANIM");
                if (animation != 6) throw new InvalidDataException(WriteError);
                byte[] playback = source.ReadBytes(6);
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(0);
                writer.Write(Encoding.ASCII.GetBytes("WEBPVP8X"));
                writer.Write(10);
                writer.Write(profile < 0 ? 0x12 : 0x32); // animation, alpha, optional ICC
                Write24(writer, frames[0].Width - 1);
                Write24(writer, frames[0].Height - 1);
                if (profile >= 0)
                {
                    source.BaseStream.Position = profileStart - 8;
                    CopyBytes(source, writer, 8L + profile + (profile & 1), token);
                }
                writer.Write(Encoding.ASCII.GetBytes("ANIM"));
                writer.Write(playback.Length);
                writer.Write(playback);
                for (int i = 0; i < frames.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var name = String.Format(CultureInfo.InvariantCulture, "{0}/frame-{1:D5}.webp", folder, i);
                    using (var still = new BinaryReader(File.OpenRead(Path.Combine(engine.DirectoryPath, name))))
                    {
                        int size = FindChunk(still, "VP8L");
                        if (size < 0) throw new InvalidDataException(WriteError);
                        int padding = size & 1;
                        writer.Write(Encoding.ASCII.GetBytes("ANMF"));
                        writer.Write(checked(24 + size + padding));
                        Write24(writer, 0);
                        Write24(writer, 0);
                        Write24(writer, frames[i].Width - 1);
                        Write24(writer, frames[i].Height - 1);
                        Write24(writer, frames[i].Delay); // WebP decoder ticks are milliseconds.
                        writer.Write((byte)2); // Replace the previous canvas, including its alpha.
                        still.BaseStream.Position -= 8;
                        CopyBytes(still, writer, 8L + size + padding, token);
                    }
                }
                long end = writer.BaseStream.Position;
                if (end - 8 > UInt32.MaxValue) throw new InvalidDataException("La copie animée dépasse la capacité du format WebP.");
                writer.BaseStream.Position = 4;
                writer.Write((uint)(end - 8));
            }
        }

        static int FindChunk(BinaryReader reader, string tag)
        {
            var stream = reader.BaseStream;
            stream.Position = 12;
            while (stream.Length - stream.Position >= 8)
            {
                string name = Encoding.ASCII.GetString(reader.ReadBytes(4));
                int size = reader.ReadInt32();
                if (size < 0 || stream.Length - stream.Position < (long)size + (size & 1))
                    throw new InvalidDataException(WriteError);
                if (name == tag) return size;
                stream.Seek((long)size + (size & 1), SeekOrigin.Current);
            }
            return -1;
        }

        static void Write24(BinaryWriter writer, int value)
        {
            if (value < 0 || value > 0xffffff) throw new InvalidDataException(WriteError);
            writer.Write((byte)value);
            writer.Write((byte)(value >> 8));
            writer.Write((byte)(value >> 16));
        }

        static void CopyBytes(BinaryReader source, BinaryWriter destination, long length, CancellationToken token)
        {
            var buffer = new byte[65536];
            while (length > 0)
            {
                token.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
                if (read == 0) throw new InvalidDataException(WriteError);
                destination.Write(buffer, 0, read);
                length -= read;
            }
        }
    }
}
