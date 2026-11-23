using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JsonObject = System.Collections.Generic.Dictionary<string, object>;

namespace AtelierPC
{
    internal sealed class PdfWatermark : WatermarkSource
    {
        readonly PdfMetadataEngine engine = new PdfMetadataEngine();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        readonly List<Page> pages = new List<Page>();
        readonly JsonObject header, objects;

        sealed class Page
        {
            internal string Reference;
            internal JsonObject Dictionary, Resources;
            internal double Left, Bottom, Width, Height;
            internal int Rotation;
            internal bool Swapped { get { return Rotation == 90 || Rotation == 270; } }
            internal double ViewWidth { get { return Swapped ? Height : Width; } }
            internal double ViewHeight { get { return Swapped ? Width : Height; } }

            internal string Matrix()
            {
                double x = Left, y = Bottom, w = Width, h = Height;
                double[] matrix;
                switch (Rotation)
                {
                    case 90: matrix = new [] { 0, h, -w, 0, x + w, y }; break;
                    case 180: matrix = new [] { -w, 0, 0, -h, x + w, y + h }; break;
                    case 270: matrix = new [] { 0, -h, w, 0, x, y + h }; break;
                    default: matrix = new [] { w, 0, 0, h, x, y }; break;
                }
                return String.Join(" ", matrix.Select(n => n.ToString("0.##########", CultureInfo.InvariantCulture)));
            }
        }

        internal PdfWatermark(string path, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                string snapshot = Path.Combine(engine.DirectoryPath, "document.pdf");
                using (var source = File.OpenRead(path))
                using (var copy = File.Create(snapshot))
                    source.CopyToAsync(copy, 81920, token).GetAwaiter().GetResult();
                if (engine.Run(token, true, "--is-encrypted", snapshot) == 0)
                    throw new InvalidDataException("Ce PDF est chiffré. Ouvrez une copie déchiffrée pour y ajouter un filigrane.");
                engine.Run(token, false, "--json-output", "--json-stream-data=none", snapshot, "document.json");
                var root = (JsonObject)json.DeserializeObject(File.ReadAllText(Path.Combine(engine.DirectoryPath, "document.json")));
                var data = (object[])root["qpdf"];
                header = (JsonObject)data[0];
                objects = (JsonObject)data[1];
                foreach (var entry in objects.Values)
                {
                    token.ThrowIfCancellationRequested();
                    CheckSignatures(entry);
                }
                var trailer = (JsonObject)Field((JsonObject)objects["trailer"], "value");
                var catalog = (JsonObject)Resolve(Field(trailer, "/Root"));
                ReadPages(Field(catalog, "/Pages") as string, token);
                if (pages.Count == 0) throw InvalidPdf();
                Width = pages[0].ViewWidth;
                Height = pages[0].ViewHeight;
                Preview = PdfWatermarkPreview.Read(engine.DirectoryPath, token);
                Extension = ".pdf";
                Notice = "Aperçu de la première page. La copie porte le filigrane sur chaque page.";
            }
            catch { Dispose(); throw; }
        }

        static InvalidDataException InvalidPdf() { return new InvalidDataException("La structure des pages du PDF est illisible."); }

        static object Field(JsonObject dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null;
        }

        object Resolve(object value)
        {
            var seen = new HashSet<string>();
            while (value is string)
            {
                string key = "obj:" + value;
                var entry = Field(objects, key) as JsonObject;
                if (entry == null) break;
                if (!seen.Add(key)) throw InvalidPdf();
                value = Field(entry, "value");
            }
            return value;
        }

        void CheckSignatures(object node)
        {
            var dictionary = node as JsonObject;
            if (dictionary != null)
            {
                var type = Resolve(Field(dictionary, "/Type")) as string;
                if (type == "/Sig" || type == "/DocTimeStamp" || dictionary.ContainsKey("/DocMDP")
                    || dictionary.ContainsKey("/ByteRange") && dictionary.ContainsKey("/Contents")
                    || (Resolve(Field(dictionary, "/FT")) as string) == "/Sig" && Resolve(Field(dictionary, "/V")) != null)
                    throw new InvalidDataException("Ce PDF contient une signature numérique. Utilisez une version non signée.");
                foreach (object child in dictionary.Values) CheckSignatures(child);
            }
            var array = node as object[];
            if (array != null) foreach (object child in array) CheckSignatures(child);
        }

        double[] Box(object value)
        {
            var array = Resolve(value) as object[];
            if (array == null || array.Length != 4) throw InvalidPdf();
            return array.Select(n => Convert.ToDouble(Resolve(n), CultureInfo.InvariantCulture)).ToArray();
        }

        void ReadPages(string root, CancellationToken token)
        {
            var visited = new HashSet<string>();
            var pending = new Stack<KeyValuePair<string, JsonObject>>();
            pending.Push(new KeyValuePair<string, JsonObject>(root, new JsonObject()));
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var item = pending.Pop();
                var dictionary = Resolve(item.Key) as JsonObject;
                if (dictionary == null || !visited.Add(item.Key)) throw InvalidPdf();
                var attributes = new JsonObject(item.Value);
                foreach (string key in new [] { "/Resources", "/Rotate", "/MediaBox", "/CropBox" })
                    if (Field(dictionary, key) != null) attributes[key] = dictionary[key];
                if ((Resolve(Field(dictionary, "/Type")) as string) == "/Page")
                {
                    AddPage(item.Key, dictionary, attributes);
                    continue;
                }
                var kids = Resolve(Field(dictionary, "/Kids")) as object[];
                if (kids == null) throw InvalidPdf();
                for (int i = kids.Length - 1; i >= 0; i--)
                    pending.Push(new KeyValuePair<string, JsonObject>(kids[i] as string, attributes));
            }
        }

        void AddPage(string reference, JsonObject dictionary, JsonObject attributes)
        {
            var media = Box(Field(attributes, "/MediaBox"));
            var crop = Box(Field(attributes, "/CropBox") ?? Field(attributes, "/MediaBox"));
            double left = Math.Max(Math.Min(media[0], media[2]), Math.Min(crop[0], crop[2]));
            double right = Math.Min(Math.Max(media[0], media[2]), Math.Max(crop[0], crop[2]));
            double bottom = Math.Max(Math.Min(media[1], media[3]), Math.Min(crop[1], crop[3]));
            double top = Math.Min(Math.Max(media[1], media[3]), Math.Max(crop[1], crop[3]));
            int rotate = Convert.ToInt32(Resolve(Field(attributes, "/Rotate")), CultureInfo.InvariantCulture) % 360;
            if (rotate % 90 != 0 || !(left < right && bottom < top)) throw InvalidPdf();
            pages.Add(new Page { Reference = reference, Dictionary = dictionary, Rotation = (rotate + 360) % 360,
                Left = left, Bottom = bottom, Width = right - left, Height = top - bottom,
                Resources = Resolve(Field(attributes, "/Resources")) as JsonObject ?? new JsonObject() });
        }

        internal override void WriteCopy(string temporary, WatermarkOptions options, CancellationToken token)
        {
            string stage = Directory.CreateDirectory(Path.Combine(engine.DirectoryPath, Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                var update = new JsonObject();
                var stamps = new Dictionary<string, string>();
                int id = Convert.ToInt32(header["maxobjectid"], CultureInfo.InvariantCulture);
                Func<JsonObject, string> add = entry => {
                    string reference = checked(++id).ToString(CultureInfo.InvariantCulture) + " 0 R";
                    update.Add("obj:" + reference, entry);
                    return reference;
                };
                string saveState = add(Content("q\n"));
                foreach (Page page in pages)
                {
                    token.ThrowIfCancellationRequested();
                    double scale = 1600 / Math.Max(page.ViewWidth, page.ViewHeight);
                    int w = Math.Max(1, (int)Math.Round(page.ViewWidth * scale)), h = Math.Max(1, (int)Math.Round(page.ViewHeight * scale));
                    string stamp, key = w + "x" + h;
                    if (!stamps.TryGetValue(key, out stamp))
                    {
                        stamp = CreateStamp(Path.Combine(stage, key), w, h, options, add, token);
                        stamps.Add(key, stamp);
                    }
                    var resources = new JsonObject(page.Resources);
                    var existing = Resolve(Field(resources, "/XObject")) as JsonObject;
                    var xobjects = existing == null ? new JsonObject() : new JsonObject(existing);
                    string name = "/AtelierPC";
                    while (xobjects.ContainsKey(name)) name += "_";
                    xobjects[name] = stamp;
                    resources["/XObject"] = xobjects;
                    var contents = new List<object> { saveState };
                    object old = Field(page.Dictionary, "/Contents");
                    var array = Resolve(old) as object[];
                    if (array != null) contents.AddRange(array);
                    else if (old != null) contents.Add(old);
                    contents.Add(add(Content("\nQ\nq " + page.Matrix() + " cm " + name + " Do Q\n")));
                    var changed = new JsonObject(page.Dictionary);
                    changed["/Resources"] = resources;
                    changed["/Contents"] = contents;
                    update["obj:" + page.Reference] = new JsonObject { { "value", changed } };
                }
                string patch = Path.Combine(stage, "update.json");
                File.WriteAllText(patch, json.Serialize(new JsonObject { { "qpdf", new object[] { header, update } } }), new UTF8Encoding(false));
                engine.Run(token, false, "--min-version=1.4", "--update-from-json=" + patch, "document.pdf", Path.GetFullPath(temporary));
                engine.Run(token, false, "--check", Path.GetFullPath(temporary));
            }
            finally { Directory.Delete(stage, true); }
        }

        static JsonObject Content(string commands)
        {
            return new JsonObject { { "stream", new JsonObject { { "dict", new JsonObject() },
                { "data", Convert.ToBase64String(Encoding.ASCII.GetBytes(commands)) } } } };
        }

        static string CreateStamp(string path, int width, int height, WatermarkOptions options,
            Func<JsonObject, string> add, CancellationToken token)
        {
            var bitmap = new FormatConvertedBitmap(WatermarkArt.Render(width, height, options), PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            var rgb = new byte[width * 3];
            var alpha = new byte[width];
            using (var colors = File.Create(path + ".rgb"))
            using (var mask = File.Create(path + ".alpha"))
                for (int y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 4;
                        rgb[x * 3] = pixels[i + 2];
                        rgb[x * 3 + 1] = pixels[i + 1];
                        rgb[x * 3 + 2] = pixels[i];
                        alpha[x] = pixels[i + 3];
                    }
                    colors.Write(rgb, 0, rgb.Length);
                    mask.Write(alpha, 0, alpha.Length);
                }
            var image = new JsonObject { { "/Type", "/XObject" }, { "/Subtype", "/Image" }, { "/Width", width },
                { "/Height", height }, { "/ColorSpace", "/DeviceGray" }, { "/BitsPerComponent", 8 } };
            string softMask = add(new JsonObject { { "stream", new JsonObject { { "dict", new JsonObject(image) }, { "datafile", path + ".alpha" } } } });
            image["/ColorSpace"] = "/DeviceRGB";
            image["/SMask"] = softMask;
            return add(new JsonObject { { "stream", new JsonObject { { "dict", image }, { "datafile", path + ".rgb" } } } });
        }

        public override void Dispose() { engine.Dispose(); }
    }
}
