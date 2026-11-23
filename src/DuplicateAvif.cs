using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace AtelierPC
{
    internal static class DuplicateAvif
    {
        internal enum ImageKind { Unsupported, Still, Standard8Bit }
        sealed class Box { internal string Type; internal long Start, End; }

        static uint Read32(BinaryReader r)
        {
            return ((uint)r.ReadByte() << 24) | ((uint)r.ReadByte() << 16) | ((uint)r.ReadByte() << 8) | r.ReadByte();
        }

        static int Read16(BinaryReader r) { return (r.ReadByte() << 8) | r.ReadByte(); }
        static string Tag(BinaryReader r) { return Encoding.ASCII.GetString(r.ReadBytes(4)); }

        static IEnumerable<Box> Boxes(BinaryReader r, long start, long end, CancellationToken token)
        {
            while (start < end)
            {
                token.ThrowIfCancellationRequested();
                r.BaseStream.Position = start;
                if (end - start < 8) throw new InvalidDataException("Boîte AVIF tronquée.");
                long size = Read32(r);
                string type = Tag(r);
                if (size == 1)
                {
                    if (end - start < 16 || Read32(r) != 0) throw new InvalidDataException("Taille de boîte AVIF non prise en charge.");
                    size = Read32(r);
                }
                if (size == 0) size = end - start;
                if (size < r.BaseStream.Position - start || size > end - start) throw new InvalidDataException("Taille de boîte AVIF incorrecte.");
                yield return new Box { Type = type, Start = r.BaseStream.Position, End = start + size };
                start += size;
            }
        }

        internal static ImageKind Inspect(Stream source, CancellationToken token)
        {
            using (var r = new BinaryReader(source, Encoding.ASCII, true))
            {
                Box meta = null, media = null;
                bool stillBrand = false, simple = true;
                foreach (var box in Boxes(r, 0, source.Length, token))
                    switch (box.Type)
                    {
                        case "ftyp":
                            if (stillBrand || box.End - box.Start < 8 || box.End - box.Start > 1024 || Tag(r) != "avif") return ImageKind.Unsupported;
                            Read32(r);
                            while (r.BaseStream.Position + 4 <= box.End)
                            {
                                string brand = Tag(r);
                                if (brand == "avis" || brand == "msf1") return ImageKind.Unsupported;
                            }
                            stillBrand = true;
                            break;
                        case "moov": case "moof": return ImageKind.Unsupported;
                        case "meta": if (meta != null) return ImageKind.Unsupported; meta = box; break;
                        case "mdat": if (media != null) simple = false; media = box; break;
                        case "free": case "skip": break;
                        default: simple = false; break;
                    }
                if (!stillBrand || meta == null || media == null || meta.End - meta.Start < 4) return ImageKind.Unsupported;
                source.Position = meta.Start;
                if (Read32(r) != 0) return ImageKind.Unsupported;
                bool items = false, properties = false, location = false, sawLocation = false, sawProperties = false;
                foreach (var box in Boxes(r, meta.Start + 4, meta.End, token))
                    switch (box.Type)
                    {
                        case "iinf":
                            if (items || box.End - box.Start < 6) return ImageKind.Unsupported;
                            uint version = Read32(r);
                            if (version != 0 && version != 0x01000000) return ImageKind.Unsupported;
                            long count = version == 0 ? (long)Read16(r) : Read32(r);
                            simple &= count == 1;
                            int images = 0, entries = 0;
                            foreach (var item in Boxes(r, source.Position, box.End, token))
                            {
                                if (item.Type != "infe" || item.End - item.Start < 12) return ImageKind.Unsupported;
                                int v = (int)(Read32(r) >> 24);
                                if ((v != 2 && v != 3) || (v == 3 && item.End - item.Start < 14)) return ImageKind.Unsupported;
                                source.Position += v == 2 ? 2 : 4;
                                if (Read16(r) != 0) return ImageKind.Unsupported;
                                string type = Tag(r);
                                if (type == "av01") images++;
                                else if (type != "Exif" && !(type == "mime" && IsXml(r, item.End))) return ImageKind.Unsupported;
                                entries++;
                            }
                            if (images != 1 || entries != count) return ImageKind.Unsupported;
                            items = true;
                            break;
                        case "iloc":
                            if (sawLocation) return ImageKind.Unsupported;
                            sawLocation = true;
                            location = EntireMediaItem(r, box, media);
                            break;
                        case "iprp":
                            foreach (var container in Boxes(r, box.Start, box.End, token))
                                if (container.Type == "ipco")
                                {
                                    if (sawProperties) return ImageKind.Unsupported;
                                    sawProperties = true;
                                    properties = StandardProperties(r, container, token);
                                }
                                else if (container.Type != "ipma") simple = false;
                            break;
                        case "hdlr": case "pitm": break;
                        default: simple = false; break;
                    }
                if (!items) return ImageKind.Unsupported;
                return simple && properties && location && StandardBitstream(r, media)
                    ? ImageKind.Standard8Bit : ImageKind.Still;
            }
        }

        static bool IsXml(BinaryReader r, long end)
        {
            // A MIME item can itself be a JPEG. Only recognize XML metadata as a non-picture.
            int length = (int)Math.Min(end - r.BaseStream.Position, 512L);
            string[] fields = Encoding.ASCII.GetString(r.ReadBytes(length)).Split('\0');
            return fields.Length >= 3 && (fields[1] == "application/rdf+xml" || fields[1] == "application/xml" || fields[1] == "text/xml");
        }

        // Only a single, local, contiguous item covering mdat can be certified by the test below.
        static bool EntireMediaItem(BinaryReader r, Box box, Box media)
        {
            long size = box.End - box.Start;
            if ((size != 22 && size != 26) || Read32(r) != 0 || r.ReadByte() != 0x44) return false;
            int baseSize = r.ReadByte();
            if (baseSize != (size == 26 ? 0x40 : 0) || Read16(r) != 1) return false;
            Read16(r); // item_ID
            if (Read16(r) != 0) return false;
            long offset = baseSize == 0 ? 0 : Read32(r);
            return Read16(r) == 1 && offset + Read32(r) == media.Start && Read32(r) == media.End - media.Start;
        }

        static bool StandardProperties(BinaryReader r, Box container, CancellationToken token)
        {
            bool codec = false, pixels = false, dimensions = false;
            foreach (var p in Boxes(r, container.Start, container.End, token))
            {
                long size = p.End - p.Start;
                switch (p.Type)
                {
                    case "av1C":
                        if (codec || size != 4 || r.ReadByte() != 0x81) return false;
                        r.ReadByte();
                        if ((r.ReadByte() & 0x60) != 0) return false; // high_bitdepth, twelve_bit
                        codec = true;
                        break;
                    case "pixi":
                        if (pixels || size != 8 || Read32(r) != 0 || Read32(r) != 0x03080808) return false;
                        pixels = true;
                        break;
                    case "ispe": dimensions = true; break;
                    default: return false; // Color overrides, HDR, transformations or auxiliary properties.
                }
            }
            return codec && pixels && dimensions;
        }

        // AV1 section 5.5. Accept only a reduced still header with explicit BT.709 primaries,
        // sRGB transfer and 8-bit color. The Q8 decoder alone cannot certify source precision or HDR.
        static bool StandardSequence(byte[] data)
        {
            int bit = 0;
            Func<int, int> get = count => {
                int value = 0;
                if (bit + count > data.Length * 8) throw new EndOfStreamException();
                while (count-- > 0) { value = (value << 1) | ((data[bit / 8] >> (7 - bit % 8)) & 1); bit++; }
                return value;
            };
            try
            {
                if (get(3) != 0 || get(2) != 3) return false;
                get(5); // seq_level_idx
                int w = get(4) + 1, h = get(4) + 1;
                get(w); get(h);
                get(6); // superblock size, intra filters, superres, CDEF, restoration
                if (get(1) != 0 || get(1) != 0 || get(1) != 1 || get(8) != 1 || get(8) != 13) return false;
                int matrix = get(8);
                return matrix == 1 || matrix == 6;
            }
            catch (EndOfStreamException) { return false; }
        }

        static bool StandardBitstream(BinaryReader r, Box media)
        {
            r.BaseStream.Position = media.Start;
            bool sequence = false;
            // One temporal delimiter (optional), one sequence header and one self-contained frame.
            for (int i = 0; i < 3 && r.BaseStream.Position < media.End; i++)
            {
                int header = r.ReadByte();
                long size = 0;
                for (int shift = 0; ; shift += 7)
                {
                    if (shift > 28 || r.BaseStream.Position >= media.End) return false;
                    int b = r.ReadByte();
                    size |= (long)(b & 127) << shift;
                    if ((b & 128) == 0) break;
                }
                if (size > media.End - r.BaseStream.Position) return false;
                if (header == 0x12 && size == 0 && i == 0) continue;
                if (header == 0x32) return sequence && size > 0 && size == media.End - r.BaseStream.Position;
                if (header != 0x0A || sequence || size > 64 || !StandardSequence(r.ReadBytes((int)size))) return false;
                sequence = true;
            }
            return false;
        }
    }
}
