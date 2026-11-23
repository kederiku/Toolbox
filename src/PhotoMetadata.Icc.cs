using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AtelierPC
{
    internal static partial class PhotoMetadata
    {
        const string IccUnsupported = "Ce profil de couleurs ICC ne peut pas être nettoyé tout en conservant sa transformation des couleurs.";
        const int MaxProfile = 16 * 1024 * 1024;
        static byte[] IccLabel(bool modern, bool copyright)
        {
            // desc and cprt are required tags. Rebuild their values without device or author names.
            if (modern)
            {
                // One en-US UTF-16 record in a v4 multiLocalizedUnicodeType.
                return new byte[] {
                    109, 108, 117, 99, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 12,
                    101, 110, 85, 83, 0, 0, 0, 2, 0, 0, 0, 28, 0, 45
                };
            }
            if (copyright) return Ascii("text\0\0\0\0-\0");
            var label = new byte[92];
            Ascii("desc").CopyTo(label, 0);
            label[11] = 2;
            label[12] = 45;
            return label;
        }

        static int IccDataLength(byte[] data, string tag, int tagOffset, int tagLength)
        {
            string type = Text(data, tagOffset, 4);
            switch (tag)
            {
                case "wtpt":
                case "bkpt":
                case "lumi":
                case "rXYZ":
                case "gXYZ":
                case "bXYZ":
                    if (type == "XYZ ")
                        return 20;
                    break;
                case "chad":
                    if (type == "sf32")
                        return 44;
                    break;
                case "view":
                    if (type == "view")
                        return 36;
                    break;
                case "meas":
                    if (type == "meas")
                        return 36;
                    break;
                case "tech":
                    if (type == "sig ")
                        return 12;
                    break;
                case "chrm":
                    if (type == "chrm" && tagLength >= 12)
                        return 12 + 8 * U16(data, tagOffset + 8, false);
                    break;
                case "cicp":
                    if (type == "cicp")
                        return 12;
                    break;
                case "rTRC":
                case "gTRC":
                case "bTRC":
                case "kTRC":
                    if (type == "curv" && tagLength >= 12)
                    {
                        uint count = U32(data, tagOffset + 8, false);
                        Require(count <= (tagLength - 12) / 2);
                        return 12 + 2 * (int)count;
                    }

                    if (type == "para" && tagLength >= 12)
                    {
                        int functionType = U16(data, tagOffset + 8, false);
                        int[] parameters = { 1, 3, 4, 5, 7 };
                        if (functionType < parameters.Length)
                            return 12 + 4 * parameters[functionType];
                    }

                    break;
                case "A2B0":
                case "A2B1":
                case "A2B2":
                case "B2A0":
                case "B2A1":
                case "B2A2":
                case "gamt":
                case "pre0":
                case "pre1":
                case "pre2":
                    if ((type == "mft1" || type == "mft2") && tagLength >= 52)
                    {
                        int channels = data[tagOffset + 8], targets = data[tagOffset + 9], grid = data[tagOffset + 10];
                        Require(channels >= 1 && channels <= 15 && targets >= 1 && targets <= 15 && grid >= 2);
                        long cells = targets;
                        for (int channel = 0; channel < channels; channel++)
                        {
                            cells *= grid;
                            Require(cells <= tagLength);
                        }

                        if (type == "mft1")
                            return 48 + 256 * (channels + targets) + (int)cells;
                        return 52 + 2 * (U16(data, tagOffset + 48, false) * channels + (int)cells + U16(data, tagOffset + 50, false) * targets);
                    }

                    break;
            }

            throw new InvalidDataException(IccUnsupported);
        }

        internal static byte[] CleanIcc(byte[] data, MetadataResult report = null)
        {
            Require(data.Length >= 132 && data.Length <= MaxProfile && U32(data, 0, false) == data.Length && Text(data, 36, 4) == "acsp");
            if (data[8] != 2 && data[8] != 4)
                throw new InvalidDataException(IccUnsupported);
            string profileClass = Text(data, 12, 4);
            if (profileClass != "scnr" && profileClass != "mntr" && profileClass != "prtr" && profileClass != "spac")
                throw new InvalidDataException(IccUnsupported);
            int count = Size(data, 128, false);
            Require(count > 0 && count <= (data.Length - 132) / 12);
            var tags = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int slot = 132 + 12 * i, tagOffset = Size(data, slot + 4, false), tagLength = Size(data, slot + 8, false);
                Require(tagOffset >= 132 + 12 * count && tagLength >= 8 && tagOffset <= data.Length - tagLength);
                string tag = Text(data, slot, 4);
                switch (tag)
                {
                    case "desc":
                    case "cprt":
                    case "dmnd":
                    case "dmdd":
                    case "vued":
                    case "meta":
                    case "targ":
                    case "pseq":
                    case "psid":
                    case "mmod":
                    case "dscm":
                    case "calt":
                        continue;
                }

                int length = IccDataLength(data, tag, tagOffset, tagLength);
                Require(length <= tagLength && !tags.ContainsKey(tag));
                var value = Slice(data, tagOffset, length);
                Array.Clear(value, 4, 4); // Reserved type-header bytes.
                tags.Add(tag, value);
            }

            tags.Add("desc", IccLabel(data[8] == 4, false));
            tags.Add("cprt", IccLabel(data[8] == 4, true));
            // Start with a zeroed header. Keep only fields that determine color interpretation.
            var header = new byte[128];
            Buffer.BlockCopy(data, 8, header, 8, 2);
            Buffer.BlockCopy(data, 12, header, 12, 12);
            header[24] = 7;
            header[25] = 208;
            header[27] = 1;
            header[29] = 1;
            Ascii("acsp").CopyTo(header, 36);
            header[47] = (byte)(data[47] & 3);
            header[63] = (byte)(data[63] & 15);
            Require(U32(data, 64, false) <= 3);
            Buffer.BlockCopy(data, 64, header, 64, 16);
            using (var output = new MemoryStream())
            {
                Put(output, header);
                Write32(output, (uint)tags.Count, false);
                output.SetLength(132 + tags.Count * 12);
                output.Position = output.Length;
                var locations = new List<KeyValuePair<byte[], int>>();
                int slot = 132;
                foreach (var entry in tags)
                {
                    int tagOffset = 0;
                    // Many profiles share a curve between their red, green and blue tags.
                    if (entry.Key.EndsWith("TRC", StringComparison.Ordinal))
                    {
                        foreach (var curve in locations)
                            if (curve.Key.SequenceEqual(entry.Value))
                            {
                                tagOffset = curve.Value;
                                break;
                            }
                    }

                    if (tagOffset == 0)
                    {
                        tagOffset = (int)output.Length;
                        output.Position = tagOffset;
                        Put(output, entry.Value);
                        while (output.Position % 4 != 0)
                            output.WriteByte(0);
                        if (entry.Key.EndsWith("TRC", StringComparison.Ordinal))
                            locations.Add(new KeyValuePair<byte[], int>(entry.Value, tagOffset));
                    }

                    output.Position = slot;
                    Put(output, Ascii(entry.Key));
                    Write32(output, (uint)tagOffset, false);
                    Write32(output, (uint)entry.Value.Length, false);
                    slot += 12;
                }

                output.Position = 0;
                Write32(output, (uint)output.Length, false);
                var cleaned = output.ToArray();
                if (report != null && !cleaned.SequenceEqual(data))
                    report.Removed("Informations du profil ICC");
                return cleaned;
            }
        }
    }
}
