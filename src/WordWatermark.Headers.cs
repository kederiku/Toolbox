using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Xml;

namespace AtelierPC
{
    internal sealed partial class WordWatermark
    {
        sealed class HeaderWriter
        {
            const string Vml = "urn:schemas-microsoft-com:vml";
            readonly string w, r, wp, a, pic;
            readonly Package original, copy;
            readonly PackagePart main, originalMain;
            readonly WatermarkOptions options;
            readonly CancellationToken token;
            readonly Dictionary<string, string> inherited = new Dictionary<string, string>();
            readonly Dictionary<string, string> previousOutput = new Dictionary<string, string>();
            readonly Dictionary<string, string> variants = new Dictionary<string, string>();
            readonly HashSet<Uri> usedHeaders = new HashSet<Uri>();
            readonly Dictionary<string, PackagePart> images = new Dictionary<string, PackagePart>();
            readonly HashSet<uint> usedIds = new HashSet<uint>();
            readonly bool evenHeaders;
            uint nextId = 1;
            uint topLayer = 251658240;

            internal HeaderWriter(WordWatermark owner, Package original, Package copy, PackagePart main, WatermarkOptions options, CancellationToken token)
            {
                w = owner.w;
                r = owner.r;
                string drawingBase = w == StrictWord ? "http://purl.oclc.org/ooxml/drawingml/" : "http://schemas.openxmlformats.org/drawingml/2006/";
                wp = drawingBase + "wordprocessingDrawing";
                a = drawingBase + "main";
                pic = drawingBase + "picture";
                this.original = original;
                this.copy = copy;
                this.main = main;
                this.originalMain = original.GetPart(main.Uri);
                this.options = options;
                this.token = token;
                foreach (var rel in originalMain.GetRelationshipsByType(r + "/settings"))
                {
                    var settings = ReadXml(InternalPart(originalMain, rel, "settings"));
                    evenHeaders = Enabled(settings.DocumentElement["evenAndOddHeaders", w]);
                }
                foreach (var part in original.GetParts())
                {
                    token.ThrowIfCancellationRequested();
                    if (!part.ContentType.StartsWith(WordType, StringComparison.Ordinal) || !part.ContentType.EndsWith("+xml", StringComparison.Ordinal)) continue;
                    using (var stream = part.GetStream(FileMode.Open, FileAccess.Read))
                    using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    {
                        while (reader.Read())
                        {
                            if (reader.NodeType != XmlNodeType.Element) continue;
                            uint number;
                            if (reader.NamespaceURI == Vml && reader.LocalName == "shape"
                                && UInt32.TryParse(StyleValue(reader.GetAttribute("style"), "z-index"), out number))
                                RaiseLayerAbove(number);
                            if (reader.NamespaceURI != wp) continue;
                            if (reader.LocalName == "docPr" && UInt32.TryParse(reader.GetAttribute("id"), out number)) usedIds.Add(number);
                            if (reader.LocalName == "anchor" && UInt32.TryParse(reader.GetAttribute("relativeHeight"), out number)) RaiseLayerAbove(number);
                        }
                    }
                }
            }

            void RaiseLayerAbove(uint layer)
            {
                if (layer >= topLayer) topLayer = layer == UInt32.MaxValue ? layer : layer + 1;
            }

            static string StyleValue(string style, string name)
            {
                if (String.IsNullOrEmpty(style)) return "";
                foreach (string declaration in style.Split(';'))
                {
                    int colon = declaration.IndexOf(':');
                    if (colon > 0 && name.Equals(declaration.Substring(0, colon).Trim(), StringComparison.OrdinalIgnoreCase))
                        return declaration.Substring(colon + 1).Trim();
                }
                return "";
            }

            static double VmlExtent(string value)
            {
                // Standalone VML shapes use CSS lengths. Unitless coordinates use pixels outside a group.
                string unit = value.Length >= 2 ? value.Substring(value.Length - 2).ToLowerInvariant() : "";
                double factor;
                switch (unit)
                {
                    case "in": factor = 914400; break;
                    case "cm": factor = 360000; break;
                    case "mm": factor = 36000; break;
                    case "pc": factor = 152400; break;
                    case "pt": factor = 12700; break;
                    case "px": factor = 9525; break;
                    default: factor = 9525; unit = ""; break;
                }
                double number;
                if (!Double.TryParse(value.Substring(0, value.Length - unit.Length), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out number)) return 0;
                return number * factor;
            }

            static bool HasScan(XmlElement paragraph, XmlNamespaceManager ns, PageSize size)
            {
                double minWidth = size.Width * 635.0 * 0.7, minHeight = size.Height * 635.0 * 0.7;
                foreach (XmlElement extent in paragraph.SelectNodes(".//w:drawing/*[wp:extent and .//pic:pic]/wp:extent", ns))
                {
                    long cx, cy;
                    if (Int64.TryParse(extent.GetAttribute("cx"), out cx) && Int64.TryParse(extent.GetAttribute("cy"), out cy)
                        && cx >= minWidth && cy >= minHeight) return true;
                }
                foreach (XmlElement shape in paragraph.SelectNodes(".//w:pict/v:shape[v:imagedata]", ns))
                {
                    string style = shape.GetAttribute("style");
                    if (VmlExtent(StyleValue(style, "width")) >= minWidth && VmlExtent(StyleValue(style, "height")) >= minHeight) return true;
                }
                return false;
            }

            internal void MarkScannedPages(XmlDocument doc, List<XmlElement> sections, PageSize[] sizes)
            {
                // Word can paint opaque body images over a foreground header drawing. Anchor another instance
                // of the same PNG in the scan's paragraph, where it shares the image's drawing layer.
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("w", w);
                ns.AddNamespace("wp", wp);
                ns.AddNamespace("pic", pic);
                ns.AddNamespace("v", Vml);
                int index = 0;
                var imageIds = new Dictionary<string, string>();
                foreach (XmlElement paragraph in doc.DocumentElement.SelectNodes("w:body//w:p[not(ancestor::w:txbxContent or ancestor::w:del)]", ns))
                {
                    token.ThrowIfCancellationRequested();
                    var size = sizes[index];
                    if (HasScan(paragraph, ns, size))
                    {
                        string imageId;
                        if (!imageIds.TryGetValue(size.Key, out imageId))
                        {
                            imageId = Link(main, Image(size), "image");
                            imageIds.Add(size.Key, imageId);
                        }
                        paragraph.AppendChild(Drawing(doc, size, imageId));
                    }
                    var props = paragraph["pPr", w];
                    if (index < sizes.Length - 1 && props != null && props["sectPr", w] == sections[index]) index++;
                }
            }

            string NewDrawingId()
            {
                while (!usedIds.Add(nextId)) nextId++;
                return (nextId++).ToString(CultureInfo.InvariantCulture);
            }

            bool Enabled(XmlElement setting)
            {
                if (setting == null) return false;
                string val = setting.GetAttribute("val", w);
                return val != "0" && val != "false" && val != "off";
            }

            PackagePart InternalPart(PackagePart from, PackageRelationship rel, string kind)
            {
                if (rel.RelationshipType != r + "/" + kind || rel.TargetMode != TargetMode.Internal) throw InvalidWord();
                var target = PackUriHelper.ResolvePartUri(from.Uri, rel.TargetUri);
                if (!original.PartExists(target)) throw InvalidWord();
                return original.GetPart(target);
            }

            internal void MarkSection(XmlElement section, PageSize size)
            {
                var refs = new Dictionary<string, XmlElement>();
                foreach (XmlElement element in section.ChildNodes.OfType<XmlElement>())
                {
                    if (element.LocalName != "headerReference" || element.NamespaceURI != w) continue;
                    string kind = element.GetAttribute("type", w);
                    string id = element.GetAttribute("id", r);
                    if (refs.ContainsKey(kind) || !originalMain.RelationshipExists(id)) throw InvalidWord();
                    refs.Add(kind, element);
                    inherited[kind] = id;
                }
                foreach (string kind in new[] { "default", "first", "even" })
                {
                    token.ThrowIfCancellationRequested();
                    XmlElement reference;
                    refs.TryGetValue(kind, out reference);
                    string source;
                    inherited.TryGetValue(kind, out source);
                    if ((kind == "first" && !Enabled(section["titlePg", w])) || (kind == "even" && !evenHeaders))
                    {
                        if (reference != null) previousOutput[kind] = source;
                        continue;
                    }
                    string output = MarkHeader(kind, source, size);
                    string previous;
                    if (reference == null && (!previousOutput.TryGetValue(kind, out previous) || output != previous))
                    {
                        reference = section.OwnerDocument.CreateElement("w", "headerReference", w);
                        reference.SetAttribute("type", w, kind);
                        section.PrependChild(reference);
                    }
                    if (reference != null) reference.SetAttribute("id", r, output);
                    previousOutput[kind] = output;
                }
            }

            string MarkHeader(string kind, string sourceId, PageSize size)
            {
                PackagePart source = sourceId == null ? null : InternalPart(originalMain, originalMain.GetRelationship(sourceId), "header");
                string key = (source == null ? kind : source.Uri.ToString()) + "|" + size.Key;
                string id;
                if (variants.TryGetValue(key, out id)) return id;
                XmlDocument xml;
                if (source == null)
                {
                    xml = new XmlDocument();
                    xml.AppendChild(xml.CreateElement("w", "hdr", w));
                }
                else
                {
                    if (source.ContentType != HeaderType) throw InvalidWord();
                    xml = ReadXml(source);
                    if (xml.DocumentElement.LocalName != "hdr" || xml.DocumentElement.NamespaceURI != w) throw InvalidWord();
                }
                PackagePart header;
                if (source != null && usedHeaders.Add(source.Uri))
                {
                    header = copy.GetPart(source.Uri);
                    id = sourceId;
                }
                else
                {
                    var uri = UniqueSibling(source == null ? main.Uri : source.Uri, "header", ".xml");
                    header = copy.CreatePart(uri, HeaderType, CompressionOption.Normal);
                    id = Link(main, header, "header");
                    if (source != null)
                    {
                        // The clone is a sibling, so local image, chart and other targets remain valid.
                        foreach (var rel in source.GetRelationships())
                            header.CreateRelationship(rel.TargetUri, rel.TargetMode, rel.RelationshipType, rel.Id);
                        foreach (XmlElement prop in xml.GetElementsByTagName("docPr", wp)) prop.SetAttribute("id", NewDrawingId());
                    }
                }
                string imageId = Link(header, Image(size), "image");
                var paragraph = xml.DocumentElement["p", w];
                if (paragraph == null)
                {
                    paragraph = xml.CreateElement("w", "p", w);
                    paragraph.InnerXml = "<w:pPr xmlns:w='" + w + "'><w:spacing w:before='0' w:after='0' w:line='1' w:lineRule='exact'/></w:pPr>";
                    xml.DocumentElement.AppendChild(paragraph);
                }
                paragraph.AppendChild(Drawing(xml, size, imageId));
                SaveXml(header, xml);
                variants.Add(key, id);
                return id;
            }

            static Uri UniqueSibling(Uri part, string name, string extension)
            {
                return PackUriHelper.ResolvePartUri(part, new Uri(name + "-" + Guid.NewGuid().ToString("N") + extension, UriKind.Relative));
            }

            string Link(PackagePart from, PackagePart to, string kind)
            {
                return from.CreateRelationship(PackUriHelper.GetRelativeUri(from.Uri, to.Uri), TargetMode.Internal, r + "/" + kind).Id;
            }

            PackagePart Image(PageSize size)
            {
                PackagePart existing;
                if (images.TryGetValue(size.Key, out existing)) return existing;
                double scale = Math.Min(1.0 / 7.5, 3000.0 / Math.Max(size.Width, size.Height));
                var bitmap = WatermarkArt.Render(Math.Max(1, (int)Math.Round(size.Width * scale)),
                    Math.Max(1, (int)Math.Round(size.Height * scale)), options);
                token.ThrowIfCancellationRequested();
                var part = copy.CreatePart(UniqueSibling(main.Uri, "media/filigrane", ".png"), "image/png", CompressionOption.NotCompressed);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = part.GetStream(FileMode.Create, FileAccess.Write)) encoder.Save(stream);
                images.Add(size.Key, part);
                return part;
            }

            XmlElement Drawing(XmlDocument xml, PageSize size, string imageId)
            {
                string cx = (size.Width * 635L).ToString(CultureInfo.InvariantCulture);
                string cy = (size.Height * 635L).ToString(CultureInfo.InvariantCulture);
                var run = xml.CreateElement("w", "r", w);
                // Only fixed namespace URIs and validated numbers enter this markup. Text and relationship IDs are assigned via the DOM.
                run.InnerXml = "<w:drawing xmlns:w='" + w + "' xmlns:wp='" + wp + "' xmlns:a='" + a + "' xmlns:pic='" + pic + "'>"
                    + "<wp:anchor distT='0' distB='0' distL='0' distR='0' simplePos='0' relativeHeight='" + topLayer.ToString(CultureInfo.InvariantCulture)
                    + "' behindDoc='0' locked='0' layoutInCell='0' allowOverlap='1'>"
                    + "<wp:simplePos x='0' y='0'/><wp:positionH relativeFrom='page'><wp:posOffset>0</wp:posOffset></wp:positionH>"
                    + "<wp:positionV relativeFrom='page'><wp:posOffset>0</wp:posOffset></wp:positionV>"
                    + "<wp:extent cx='" + cx + "' cy='" + cy + "'/><wp:wrapNone/><wp:docPr id='" + NewDrawingId() + "' name='Filigrane'/>"
                    + "<wp:cNvGraphicFramePr/><a:graphic><a:graphicData uri='" + pic + "'><pic:pic><pic:nvPicPr>"
                    + "<pic:cNvPr id='0' name='Filigrane'/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip/>"
                    + "<a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='" + cx + "' cy='" + cy + "'/>"
                    + "</a:xfrm><a:prstGeom prst='rect'><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:anchor></w:drawing>";
                ((XmlElement)run.GetElementsByTagName("blip", a)[0]).SetAttribute("embed", r, imageId);
                return run;
            }
        }
    }
}
