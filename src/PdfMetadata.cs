using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace AtelierPC
{
    internal sealed partial class PdfMetadata
    {
        readonly string directory;
        readonly MetadataResult result;
        readonly CancellationToken token;
        readonly Dictionary<string, XmlElement> objects = new Dictionary<string, XmlElement>();
        readonly HashSet<string> discarded = new HashSet<string>();
        readonly HashSet<string> contentStreams = new HashSet<string>();
        readonly HashSet<XmlElement> images = new HashSet<XmlElement>();
        readonly Dictionary<string, bool> colorProfiles = new Dictionary<string, bool>();
        readonly XmlDocument json;

        PdfMetadata(string directory, MetadataResult result, CancellationToken token)
        {
            this.directory = directory;
            this.result = result;
            this.token = token;
            json = ReadJson(Path.Combine(directory, "objects.json"));
            foreach (XmlElement entry in Entries(json).ChildNodes) objects.Add(Key(entry), entry);
            foreach (XmlElement entry in objects.Values)
            {
                var stream = Field(entry, "stream");
                if (Scalar(Field(Field(stream, "dict"), "/Subtype")) == "/Image") images.Add(stream);
            }
            // /Thumb peut désigner une image qui omet complètement /Type et /Subtype (ISO 32000-1, 12.3.4).
            foreach (XmlElement reference in json.SelectNodes("//*[@item='/Thumb']"))
            {
                XmlElement entry;
                if (!objects.TryGetValue("obj:" + reference.InnerText, out entry) || Field(entry, "stream") == null) throw ImageError();
                images.Add(Field(entry, "stream"));
            }
        }

        internal static void Clean(string input, string output, MetadataResult result, CancellationToken token)
        {
            input = Path.GetFullPath(input);
            output = Path.GetFullPath(output);
            using (var engine = new PdfMetadataEngine())
            {
                if (engine.Run(token, true, "--is-encrypted", input) == 0)
                    throw new InvalidDataException("Ce PDF est chiffré. Une copie déchiffrée est nécessaire.");
                engine.Run(token, false, "--check", input);
                engine.Run(token, false, "--json-output", "--json-object=trailer", "--json-stream-data=none", input, "trailer.json");
                var trailer = Entries(ReadJson(Path.Combine(engine.DirectoryPath, "trailer.json")));
                var previous = Field(Field(Field(trailer, "trailer"), "value"), "/Prev");
                // Dans un PDF linéarisé, le premier xref renvoie au second, situé plus loin dans le fichier.
                if (previous != null) result.PreviousRevisions = IsEarlierXref(input, previous.InnerText);
                engine.Run(token, false, "--externalize-inline-images", "--ii-min-bytes=0", "--object-streams=disable",
                    "--stream-data=preserve", input, "current.pdf");
                engine.Run(token, false, "--json-output", "--decode-level=generalized", "--json-stream-data=file",
                    "--json-stream-prefix=s", "current.pdf", "objects.json");
                var document = new PdfMetadata(engine.DirectoryPath, result, token);
                document.UnwrapJpegs(engine);
                document.Scrub();
                string temporary = Path.Combine(engine.DirectoryPath, "clean.pdf");
                engine.Run(token, false, "--json-input", "objects.json", temporary);
                engine.Run(token, false, "--check", temporary);
                token.ThrowIfCancellationRequested();
                File.Copy(temporary, output);
            }
        }

        static bool IsEarlierXref(string input, string offset)
        {
            using (var file = File.OpenRead(input))
            {
                var tail = new byte[(int)Math.Min(file.Length, 8192L)];
                file.Seek(-tail.Length, SeekOrigin.End);
                using (var reader = new BinaryReader(file)) tail = reader.ReadBytes(tail.Length);
                var matches = Regex.Matches(Encoding.ASCII.GetString(tail), @"startxref\s+(\d+)\s+%%EOF");
                long current, previous;
                if (matches.Count == 0 || !Int64.TryParse(matches[matches.Count - 1].Groups[1].Value, out current)
                    || !Int64.TryParse(offset, out previous)) throw Unreadable();
                return previous < current;
            }
        }

        // L'infoset JSON de .NET conserve les nombres PDF sous forme de texte, même pour une matrice très précise.
        static XmlDocument ReadJson(string path)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var file = File.OpenRead(path))
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(file, XmlDictionaryReaderQuotas.Max))
                doc.Load(reader);
            return doc;
        }

        static XmlElement Entries(XmlDocument doc) { return (XmlElement)Field(doc.DocumentElement, "qpdf").LastChild; }
        static string Key(XmlElement e) { return e.HasAttribute("item") ? e.GetAttribute("item") : e.LocalName; }
        static bool Is(XmlElement e, string type) { return e != null && e.GetAttribute("type") == type; }

        static XmlElement Field(XmlElement dictionary, string key)
        {
            if (!Is(dictionary, "object")) return null;
            return dictionary.ChildNodes.Cast<XmlElement>().FirstOrDefault(e => Key(e) == key);
        }

        XmlElement Value(XmlElement e)
        {
            var seen = new HashSet<string>();
            while (Is(e, "string"))
            {
                XmlElement entry;
                if (!objects.TryGetValue("obj:" + e.InnerText, out entry)) break;
                if (!seen.Add(e.InnerText)) throw Unreadable();
                e = Field(entry, "value") ?? Field(Field(entry, "stream"), "dict");
            }
            return e;
        }

        string Scalar(XmlElement e)
        {
            e = Value(e);
            return e != null && !Is(e, "object") && !Is(e, "array") ? e.InnerText : "";
        }

        static InvalidDataException Unreadable() { return new InvalidDataException("La structure des objets PDF ne peut pas être vérifiée."); }
        static InvalidDataException ImageError() { return new InvalidDataException("Une image intégrée du PDF utilise un codage ou des données qui ne peuvent pas être vérifiés."); }

        void DropField(XmlElement e)
        {
            if (Is(e, "string") && objects.ContainsKey("obj:" + e.InnerText)) discarded.Add("obj:" + e.InnerText);
            e.ParentNode.RemoveChild(e);
            result.DocumentProperties = true;
        }

        void DescribeInfo(XmlElement reference)
        {
            var dictionary = Value(reference);
            if (!Is(dictionary, "object")) return;
            foreach (XmlElement field in dictionary.ChildNodes)
            {
                string label;
                switch (Key(field))
                {
                    case "/Title": label = "Titre"; break;
                    case "/Author": label = "Auteur"; break;
                    case "/Subject": label = "Objet"; break;
                    case "/Keywords": label = "Mots-clés"; break;
                    case "/Creator": label = "Logiciel de création"; break;
                    case "/Producer": label = "Logiciel de production du PDF"; break;
                    case "/CreationDate": label = "Date de création"; break;
                    case "/ModDate": label = "Date de modification"; break;
                    case "/Trapped": label = "État de recouvrement à l’impression"; break;
                    default: result.Removed("Propriétés personnalisées"); continue;
                }
                var value = Value(field);
                if (value == null || Is(value, "null")) continue;
                if (Is(value, "string"))
                {
                    string text = value.InnerText;
                    if (text == "u:" || text == "b:") continue;
                }
                result.Removed(label);
            }
        }

        void DiscardMetadataStream(string key, XmlElement stream)
        {
            if (!discarded.Add(key)) return;
            result.DocumentProperties = true;
            var dictionary = Field(stream, "dict");
            if (Scalar(Field(dictionary, "/Subtype")) != "/XML")
            {
                result.Removed("Métadonnées du PDF");
                return;
            }
            if (Field(dictionary, "/Filter") != null || Field(dictionary, "/F") != null || Field(stream, "datafile") == null)
                result.Removed("Métadonnées XMP");
            else
                using (var data = File.OpenRead(StreamPath(stream))) PhotoMetadata.DescribeXmp(data, result, token);
        }

        void CollectContents(XmlElement value)
        {
            if (value == null) return;
            if (Is(value, "string"))
            {
                var key = "obj:" + value.InnerText;
                XmlElement obj;
                if (!objects.TryGetValue(key, out obj)) throw Unreadable();
                if (Field(obj, "stream") != null) { contentStreams.Add(key); return; }
                value = Value(value);
            }
            if (!Is(value, "array")) throw Unreadable();
            foreach (XmlElement element in value.ChildNodes)
            {
                if (!Is(element, "string") || Field(objects.ContainsKey("obj:" + element.InnerText) ? objects["obj:" + element.InnerText] : null, "stream") == null)
                    throw Unreadable();
                contentStreams.Add("obj:" + element.InnerText);
            }
        }

        void Visit(XmlElement dictionary)
        {
            if (Is(dictionary, "array"))
            {
                if (CleanColorSpace(dictionary)) result.DocumentProperties = true;
                foreach (XmlElement item in dictionary.ChildNodes) Visit(item);
                return;
            }
            if (!Is(dictionary, "object")) return;
            string type = Scalar(Field(dictionary, "/Type")), subtype = Scalar(Field(dictionary, "/Subtype"));
            if (subtype == "/GEO" || type == "/GEOGCS" || type == "/PROJCS" || type == "/LGIDict"
                || Field(dictionary, "/GPTS") != null || Field(dictionary, "/LGIDict") != null)
                throw new InvalidDataException("Ce PDF est géoréférencé. Retirer ses coordonnées supprimerait le positionnement de la carte.");
            if (type == "/Sig" || type == "/DocTimeStamp" || Scalar(Field(dictionary, "/FT")) == "/Sig"
                || Field(dictionary, "/ByteRange") != null || Field(dictionary, "/DocMDP") != null)
                throw new InvalidDataException("Ce PDF contient une signature ou un champ de signature.");
            if (type == "/EmbeddedFile" || subtype == "/FileAttachment" || Field(dictionary, "/EF") != null
                || Field(dictionary, "/EmbeddedFiles") != null || Field(dictionary, "/AF") != null)
                throw new InvalidDataException("Ce PDF contient une pièce jointe ou des fichiers associés.");
            if (Field(dictionary, "/XFA") != null || Field(dictionary, "/OPI") != null || Field(dictionary, "/DestOutputProfileRef") != null || Field(dictionary, "/Ref") != null && subtype == "/Form"
                || subtype == "/RichMedia" || subtype == "/3D" || subtype == "/Movie" || subtype == "/Sound" || subtype == "/PS")
                throw new InvalidDataException("Ce PDF contient un objet multimédia ou externe dont les images ne peuvent pas être vérifiées.");
            if (type == "/Page") CollectContents(Field(dictionary, "/Contents"));
            var outputProfile = Field(dictionary, "/DestOutputProfile");
            if (outputProfile != null && CleanProfile(outputProfile)) result.DocumentProperties = true;
            var charProcs = Value(Field(dictionary, "/CharProcs"));
            if (Is(charProcs, "object"))
                foreach (XmlElement proc in charProcs.ChildNodes) CollectContents(proc);
            foreach (var field in dictionary.ChildNodes.Cast<XmlElement>().ToArray())
            {
                if (Key(field) == "/Metadata")
                {
                    XmlElement entry;
                    string key = "obj:" + field.InnerText;
                    if (Is(field, "string") && objects.TryGetValue(key, out entry) && Field(entry, "stream") != null)
                        DiscardMetadataStream(key, Field(entry, "stream"));
                    else result.Removed("Métadonnées du PDF");
                    DropField(field);
                }
                else if (Key(field) == "/PieceInfo")
                {
                    result.Removed("Données d’application du PDF");
                    DropField(field);
                }
                else if (Key(field) == "/PTEX.FileName" || Key(field) == "/PTEX.InfoDict")
                {
                    if (Key(field) == "/PTEX.FileName") result.Removed("Chemin d’origine");
                    else DescribeInfo(field);
                    // qpdf écarte les orphelins à l’écriture. Une chaîne également citée dans une annotation reste disponible.
                    dictionary.RemoveChild(field);
                    result.DocumentProperties = true;
                }
                else Visit(field);
            }
        }

        void Scrub()
        {
            var info = Field(Field(objects["trailer"], "value"), "/Info");
            if (info != null) { DescribeInfo(info); DropField(info); }
            foreach (var pair in objects)
            {
                token.ThrowIfCancellationRequested();
                var stream = Field(pair.Value, "stream");
                var dictionary = Field(stream, "dict");
                if (dictionary == null) { Visit(Field(pair.Value, "value")); continue; }
                var type = Scalar(Field(dictionary, "/Type"));
                var subtype = Scalar(Field(dictionary, "/Subtype"));
                if (type == "/Metadata") { DiscardMetadataStream(pair.Key, stream); continue; }
                if (Field(dictionary, "/F") != null) throw new InvalidDataException("Ce PDF utilise un flux dans un fichier externe.");
                if (subtype == "/Form" || Field(dictionary, "/PatternType") != null) contentStreams.Add(pair.Key);
                bool hasImageMetadata = Field(dictionary, "/Metadata") != null;
                Visit(dictionary);
                if (images.Contains(stream)) CleanImage(stream, dictionary, hasImageMetadata);
            }
            foreach (string key in contentStreams)
            {
                var stream = Field(objects[key], "stream");
                if (Field(Field(stream, "dict"), "/Filter") != null) throw ImageError();
                using (var file = File.OpenRead(StreamPath(stream)))
                    if (ContainsInlineImage(file, token)) throw ImageError();
            }
            // Une référence secondaire à un ancien dictionnaire Info ou à un flux XMP doit aussi perdre son contenu.
            foreach (string key in discarded)
            {
                var obj = objects[key];
                obj.RemoveChild(obj.FirstChild);
                var nil = json.CreateElement("value");
                nil.SetAttribute("type", "null");
                obj.AppendChild(nil);
            }
            using (var file = File.Create(Path.Combine(directory, "objects.json")))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(file, Encoding.UTF8, false))
                json.WriteTo(writer);
        }

        string StreamPath(XmlElement stream)
        {
            var name = Field(stream, "datafile");
            if (name == null || !Regex.IsMatch(name.InnerText, @"^s-[0-9]+$")) throw Unreadable();
            return Path.Combine(directory, name.InnerText);
        }

        bool CleanColorSpace(XmlElement color)
        {
            color = Value(color);
            if (!Is(color, "array") || Scalar(color.FirstChild as XmlElement) != "/ICCBased") return false;
            return CleanProfile(color.ChildNodes[1] as XmlElement);
        }

        bool CleanProfile(XmlElement reference)
        {
            XmlElement obj;
            if (!Is(reference, "string") || !objects.TryGetValue("obj:" + reference.InnerText, out obj)) throw ImageError();
            bool changed;
            if (colorProfiles.TryGetValue(reference.InnerText, out changed)) return changed;
            var stream = Field(obj, "stream");
            if (stream == null || Field(Field(stream, "dict"), "/Filter") != null) throw ImageError();
            string path = StreamPath(stream);
            byte[] data = File.ReadAllBytes(path);
            var cleaned = PhotoMetadata.CleanIcc(data, result);
            changed = !data.SequenceEqual(cleaned);
            if (changed) File.WriteAllBytes(path, cleaned);
            colorProfiles.Add(reference.InnerText, changed);
            return changed;
        }

        int Components(XmlElement color)
        {
            color = Value(color);
            if (color == null) throw ImageError();
            var space = Is(color, "array") ? Scalar((XmlElement)color.FirstChild) : Scalar(color);
            switch (space)
            {
                case "/DeviceGray": case "/CalGray": case "/Indexed": case "/Separation": return 1;
                case "/DeviceRGB": case "/CalRGB": case "/Lab": return 3;
                case "/DeviceCMYK": return 4;
                case "/DeviceN":
                    var colors = Value(color.ChildNodes[1] as XmlElement);
                    if (Is(colors, "array") && colors.ChildNodes.Count > 0) return colors.ChildNodes.Count;
                    break;
                case "/ICCBased":
                    int n;
                    if (Int32.TryParse(Scalar(Field(Value(color.ChildNodes[1] as XmlElement), "/N")), out n)
                        && (n == 1 || n == 3 || n == 4)) return n;
                    break;
            }
            throw ImageError();
        }

        void CleanImage(XmlElement stream, XmlElement dict, bool changed)
        {
            if (CleanColorSpace(Field(dict, "/ColorSpace"))) changed = true;
            var filter = Value(Field(dict, "/Filter"));
            if (Is(filter, "array") && filter.ChildNodes.Count == 1) filter = Value((XmlElement)filter.FirstChild);
            string path = StreamPath(stream);
            if (filter == null || Is(filter, "null"))
            {
                int width, height, bits;
                if (!Int32.TryParse(Scalar(Field(dict, "/Width")), out width) || !Int32.TryParse(Scalar(Field(dict, "/Height")), out height)
                    || width < 1 || height < 1) throw ImageError();
                bool mask = Scalar(Field(dict, "/ImageMask")) == "true";
                int components = mask ? 1 : Components(Field(dict, "/ColorSpace"));
                if (mask) bits = 1;
                else if (!Int32.TryParse(Scalar(Field(dict, "/BitsPerComponent")), out bits)
                    || !(bits == 1 || bits == 2 || bits == 4 || bits == 8 || bits == 16)) throw ImageError();
                long row = ((long)width * components * bits + 7) / 8;
                long length = new FileInfo(path).Length;
                if (length % height != 0 || length / height != row) throw ImageError();
            }
            else if (Scalar(filter) == "/DCTDecode")
            {
                bool photoChanged;
                byte[] cleaned;
                try { cleaned = PhotoMetadata.Clean(File.ReadAllBytes(path), ".jpg", token, out photoChanged, result); }
                catch (InvalidDataException e) { throw new InvalidDataException("Les métadonnées d’une image JPEG du PDF ne peuvent pas être vérifiées.", e); }
                if (photoChanged) { File.WriteAllBytes(path, cleaned); changed = true; }
            }
            else throw ImageError();
            if (changed) result.EmbeddedPhotos++;
        }

        // qpdf extrait les images inline des pages et formulaires. Les apparences, motifs et glyphes
        // peuvent aussi contenir des opérateurs graphiques : refuser un BI résiduel dans ces flux.
        static bool ContainsInlineImage(Stream stream, CancellationToken token)
        {
            int depth = 0, word = 0, c;
            bool comment = false, hex = false, name = false;
            while ((c = stream.ReadByte()) >= 0)
            {
                if ((stream.Position & 65535) == 0) token.ThrowIfCancellationRequested();
                if (comment) { if (c == 10 || c == 13) comment = false; continue; }
                if (depth > 0)
                {
                    if (c == '\\') stream.ReadByte();
                    else if (c == '(') depth++;
                    else if (c == ')') depth--;
                    continue;
                }
                if (hex) { if (c == '>') hex = false; continue; }
                if (c > 32 && "()<>[]{}/%".IndexOf((char)c) < 0)
                {
                    if (!name) word = word == 0 && c == 'B' ? 1 : word == 1 && c == 'I' ? 2 : 3;
                    continue;
                }
                if (word == 2) return true;
                word = 0;
                name = c == '/';
                if (c == '%') comment = true;
                if (c == '(') depth = 1;
                if (c == '<')
                {
                    int next = stream.ReadByte();
                    if (next != '<' && next != '>') hex = true;
                }
            }
            return word == 2;
        }
    }
}
