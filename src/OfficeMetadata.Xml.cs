using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml;

namespace AtelierPC
{
    internal static partial class OfficeMetadata
    {
        enum XmlPart { Manifest, Relationships, Document, Workbook, PivotCache }
        enum ExcelEdit { None, RemovePath, RemoveEditor, ReplaceEditor, RemoveCacheEditor }
        const string AnonymousEditor = "Utilisateur";

        static ExcelEdit WorkbookEdit(XmlReader reader, XmlPart part)
        {
            if (part == XmlPart.PivotCache && reader.Depth == 0 && reader.GetAttribute("refreshedBy") != null)
                return ExcelEdit.RemoveCacheEditor;
            if (part != XmlPart.Workbook) return ExcelEdit.None;
            if (reader.LocalName == "absPath" && reader.NamespaceURI == "http://schemas.microsoft.com/office/spreadsheetml/2010/11/ac")
                return ExcelEdit.RemovePath;
            if (reader.LocalName != "fileSharing" || !FamilyNamespace(reader.NamespaceURI, "spreadsheetml")) return ExcelEdit.None;
            string editor = reader.GetAttribute("userName");
            if (reader.GetAttribute("reservationPassword") != null)
                return editor == AnonymousEditor ? ExcelEdit.None : ExcelEdit.ReplaceEditor;
            return editor == null ? ExcelEdit.None : ExcelEdit.RemoveEditor;
        }

        static bool PrinterType(string type)
        {
            return type == OfficeType + "presentationml.printersettings" || type == OfficeType + "spreadsheetml.printersettings"
                || type == OfficeType + "wordprocessingml.printersettings";
        }

        static string PropertyLabel(string partNs, string fieldNs, string name)
        {
            if (partNs == PackageNs + "metadata/core-properties")
            {
                if (fieldNs == "http://purl.org/dc/elements/1.1/")
                    switch (name)
                    {
                        case "creator": return "Auteur";
                        case "title": return "Titre";
                        case "subject": return "Objet";
                        case "description": return "Description";
                        case "identifier": return "Identifiant du document";
                        case "language": return "Langue du document";
                    }
                if (fieldNs == "http://purl.org/dc/terms/")
                    switch (name)
                    {
                        case "created": return "Date de création";
                        case "modified": return "Date de modification";
                    }
                if (fieldNs == partNs)
                    switch (name)
                    {
                        case "lastModifiedBy": return "Dernier éditeur";
                        case "keywords": return "Mots-clés";
                        case "revision": return "Numéro de révision";
                        case "lastPrinted": return "Date de dernière impression";
                        case "category": return "Catégorie";
                        case "contentStatus": return "État du document";
                        case "version": return "Version du document";
                    }
            }
            if (fieldNs == partNs && (partNs == OfficeNs + "custom-properties" || partNs == StrictNs + "customProperties")
                && name == "property") return "Propriétés personnalisées";
            if (fieldNs == partNs && (partNs == OfficeNs + "extended-properties" || partNs == StrictNs + "extendedProperties"))
                switch (name)
                {
                    case "Company": return "Entreprise";
                    case "Manager": return "Responsable";
                    case "Application": return "Logiciel de création";
                    case "AppVersion": return "Version du logiciel";
                    case "Template": return "Modèle de document";
                    case "HyperlinkBase": return "Adresse de base des liens";
                    case "TotalTime": return "Durée d’édition";
                    case "Pages": case "Words": case "Characters": case "CharactersWithSpaces":
                    case "Lines": case "Paragraphs": case "Slides": case "Notes": case "HiddenSlides":
                        return "Statistiques du document";
                    case "TitlesOfParts": return "Titres des parties du document";
                }
            return "Autres propriétés du document";
        }

        // Les profils contiennent des données privées propres au pilote. Ils sont facultatifs.
        // Les réglages indépendants de l'imprimante, dont le papier et l'orientation, restent en place.
        static void FilterXml(ZipArchiveEntry entry, Stream output, XmlPart part, HashSet<string> printerIds, MetadataResult result, CancellationToken token)
        {
            using (var stream = entry.Open())
            using (var reader = Reader(stream))
            using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.None }))
            {
                int skipDepth = -1;
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (skipDepth >= 0)
                    {
                        if (reader.Depth == skipDepth && reader.NodeType == XmlNodeType.EndElement) skipDepth = -1;
                        continue;
                    }
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        switch (reader.NodeType)
                        {
                            case XmlNodeType.XmlDeclaration:
                                string standalone = reader.GetAttribute("standalone");
                                if (standalone == null) writer.WriteStartDocument(); else writer.WriteStartDocument(standalone == "yes");
                                break;
                            case XmlNodeType.EndElement: writer.WriteFullEndElement(); break;
                            case XmlNodeType.Text: writer.WriteString(reader.Value); break;
                            case XmlNodeType.Whitespace: case XmlNodeType.SignificantWhitespace: writer.WriteWhitespace(reader.Value); break;
                            case XmlNodeType.Comment: writer.WriteComment(reader.Value); break;
                            case XmlNodeType.CDATA: writer.WriteCData(reader.Value); break;
                            case XmlNodeType.ProcessingInstruction: writer.WriteProcessingInstruction(reader.Name, reader.Value); break;
                        }
                        continue;
                    }
                    string element = reader.LocalName, ns = reader.NamespaceURI;
                    ExcelEdit edit = WorkbookEdit(reader, part);
                    bool remove = edit == ExcelEdit.RemovePath || (part == XmlPart.Manifest && PrinterType((reader.GetAttribute("ContentType") ?? "").ToLowerInvariant()));
                    if (part == XmlPart.Relationships && printerIds.Contains(reader.GetAttribute("Id"))) remove = true;
                    bool removeId = false;
                    if (printerIds != null && part != XmlPart.Relationships)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            if (!IsRelationshipNamespace(reader.NamespaceURI) || !printerIds.Contains(reader.Value)) continue;
                            if (reader.LocalName != "id") throw InvalidPackage();
                            if (element == "printerSettings" && FamilyNamespace(ns, "wordprocessingml")) remove = true;
                            else if (element == "pageSetup" && FamilyNamespace(ns, "spreadsheetml")) removeId = true;
                            else throw InvalidPackage();
                        }
                        reader.MoveToElement();
                    }
                    if (remove)
                    {
                        if (edit == ExcelEdit.RemovePath && !String.IsNullOrWhiteSpace(reader.GetAttribute("url")))
                            result.Removed("Chemin d’origine");
                        if (!reader.IsEmptyElement) skipDepth = reader.Depth;
                        continue;
                    }
                    writer.WriteStartElement(reader.Prefix, element, ns);
                    while (reader.MoveToNextAttribute())
                    {
                        if (removeId && reader.LocalName == "id" && IsRelationshipNamespace(reader.NamespaceURI)) continue;
                        if (reader.NamespaceURI.Length == 0 && reader.LocalName == "userName" && (edit == ExcelEdit.ReplaceEditor || edit == ExcelEdit.RemoveEditor))
                        {
                            if (!String.IsNullOrWhiteSpace(reader.Value)) result.Removed("Utilisateur du partage");
                            continue;
                        }
                        if (reader.NamespaceURI.Length == 0 && reader.LocalName == "refreshedBy" && edit == ExcelEdit.RemoveCacheEditor)
                        {
                            if (!String.IsNullOrWhiteSpace(reader.Value)) result.Removed("Dernier utilisateur du tableau croisé dynamique");
                            continue;
                        }
                        writer.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI, reader.Value);
                    }
                    reader.MoveToElement();
                    if (edit == ExcelEdit.ReplaceEditor) writer.WriteAttributeString("userName", AnonymousEditor);
                    if (reader.IsEmptyElement) writer.WriteEndElement();
                }
            }
        }

        static bool IsRelationshipNamespace(string ns)
        {
            return ns == OfficeNs + "relationships" || ns == StrictNs + "relationships";
        }

        static bool FamilyNamespace(string ns, string family)
        {
            return ns == "http://schemas.openxmlformats.org/" + family + "/2006/main" || ns == "http://purl.oclc.org/ooxml/" + family + "/main";
        }
    }
}
