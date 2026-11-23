using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace AtelierPC
{
    internal sealed partial class PdfMetadata
    {
        // qpdf conserve une chaîne complète de filtres si le dernier (JPEG) est avec perte.
        // Un document JSON auxiliaire lui fait décoder uniquement les enveloppes, en une invocation.
        void UnwrapJpegs(PdfMetadataEngine engine)
        {
            var targets = new List<string>();
            var sources = new List<string>();
            var streams = new StringBuilder();
            foreach (var stream in images)
            {
                token.ThrowIfCancellationRequested();
                var dict = Field(stream, "dict");
                var filterField = Field(dict, "/Filter");
                var filters = Value(filterField);
                if (!Is(filters, "array") || filters.ChildNodes.Count < 2 || Scalar((XmlElement)filters.LastChild) != "/DCTDecode") continue;
                var names = filters.ChildNodes.Cast<XmlElement>().Select(Scalar).ToArray();
                string[] supported = { "/ASCII85Decode", "/ASCIIHexDecode", "/FlateDecode", "/LZWDecode", "/RunLengthDecode" };
                if (names.Take(names.Length - 1).Any(n => !supported.Contains(n))) throw ImageError();
                var parmsField = Field(dict, "/DecodeParms");
                var parms = Value(parmsField);
                if (parms != null && !Is(parms, "null"))
                {
                    if (!Is(parms, "array") || parms.ChildNodes.Count != names.Length ||
                        parms.ChildNodes.Cast<XmlElement>().Take(names.Length - 1).Any(n => !Is(Value(n), "null"))) throw ImageError();
                    var jpegParms = (XmlElement)parms.LastChild.CloneNode(true);
                    while (parmsField.HasChildNodes) parmsField.RemoveChild(parmsField.FirstChild);
                    parmsField.SetAttribute("type", jpegParms.GetAttribute("type"));
                    foreach (XmlNode child in jpegParms.ChildNodes) parmsField.AppendChild(child.CloneNode(true));
                }
                filterField.SetAttribute("type", "string");
                filterField.InnerText = "/DCTDecode";
                string path = StreamPath(stream);
                string id = (3 + targets.Count).ToString(CultureInfo.InvariantCulture) + " 0 R";
                targets.Add(path);
                sources.Add("\"" + id + "\"");
                streams.Append(",\"obj:" + id + "\":{\"stream\":{\"dict\":{\"/Filter\":[" +
                    String.Join(",", names.Take(names.Length - 1).Select(n => "\"" + n + "\"")) +
                    "]},\"datafile\":\"" + Path.GetFileName(path) + "\"}}");
            }
            if (targets.Count == 0) return;
            string batch = "{\"qpdf\":[{\"jsonversion\":2,\"pdfversion\":\"1.7\"},{" +
                "\"trailer\":{\"value\":{\"/Root\":\"1 0 R\"}}," +
                "\"obj:1 0 R\":{\"value\":{\"/Type\":\"/Catalog\",\"/Pages\":\"2 0 R\",\"/Sources\":[" + String.Join(",", sources) + "]}}," +
                "\"obj:2 0 R\":{\"value\":{\"/Type\":\"/Pages\",\"/Count\":0,\"/Kids\":[]}}" + streams + "}]}";
            File.WriteAllText(Path.Combine(directory, "wrapped.json"), batch);
            engine.Run(token, false, "--json-input", "--json-output", "--decode-level=specialized",
                "--json-stream-data=file", "--json-stream-prefix=unwrapped", "wrapped.json", "unwrapped.json");
            var decoded = Entries(ReadJson(Path.Combine(directory, "unwrapped.json")));
            foreach (XmlElement entry in decoded.ChildNodes)
            {
                var stream = Field(entry, "stream");
                if (stream == null) continue;
                string id = Key(entry).Substring(4).Split(' ')[0];
                string name = "unwrapped-" + id;
                if (Field(Field(stream, "dict"), "/Filter") != null || Field(stream, "datafile").InnerText != name) throw ImageError();
                File.Copy(Path.Combine(directory, name), targets[Int32.Parse(id, CultureInfo.InvariantCulture) - 3], true);
            }
        }
    }
}
