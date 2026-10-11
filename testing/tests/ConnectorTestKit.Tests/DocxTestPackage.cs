using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace ConnectorTestKit.Tests;

/// <summary>
/// Builds small WordprocessingML packages for edge-case tests and inspects
/// generated output. Test-only: the Open XML SDK is never part of the connector.
/// </summary>
internal static class DocxTestPackage
{
    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string W14Uri = "http://schemas.microsoft.com/office/word/2010/wordml";
    public static readonly XNamespace Wn = W;
    public static readonly XNamespace W14 = W14Uri;

    private const string Namespaces =
        "xmlns:w=\"" + W + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:w14=\"" + W14Uri + "\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" mc:Ignorable=\"w14\"";

    public sealed class Options
    {
        public Dictionary<string, string> Headers { get; } = new();
        public Dictionary<string, string> Footers { get; } = new();
        public List<(string Name, byte[] Data)> ExtraEntries { get; } = new();
        public string MainContentType { get; set; } = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
        public string? SectionReferences { get; set; }
        public bool OmitDocument { get; set; }
        public string? RawDocumentXml { get; set; }
    }

    public static byte[] Build(string bodyXml, Options? options = null)
    {
        options ??= new Options();
        var overrides = new StringBuilder();
        overrides.Append($"<Override PartName=\"/word/document.xml\" ContentType=\"{options.MainContentType}\"/>");
        var rels = new StringBuilder();
        var references = new StringBuilder();
        foreach (var header in options.Headers)
        {
            overrides.Append($"<Override PartName=\"/word/{header.Key}\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/>");
            string id = "rIdH" + rels.Length;
            rels.Append($"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"{header.Key}\"/>");
            references.Append($"<w:headerReference w:type=\"{VariantOf(header.Key)}\" r:id=\"{id}\"/>");
        }
        foreach (var footer in options.Footers)
        {
            overrides.Append($"<Override PartName=\"/word/{footer.Key}\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>");
            string id = "rIdF" + rels.Length;
            rels.Append($"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"{footer.Key}\"/>");
            references.Append($"<w:footerReference w:type=\"{VariantOf(footer.Key)}\" r:id=\"{id}\"/>");
        }
        string sectPr = "<w:sectPr>" + (options.SectionReferences ?? references.ToString()) +
            "<w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>";

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/>" + overrides + "</Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            if (!options.OmitDocument)
            {
                Add(zip, "word/document.xml", options.RawDocumentXml ??
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document " + Namespaces + "><w:body>" + bodyXml + sectPr + "</w:body></w:document>");
            }
            Add(zip, "word/_rels/document.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + rels + "</Relationships>");
            foreach (var header in options.Headers)
                Add(zip, "word/" + header.Key, "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:hdr " + Namespaces + ">" + header.Value + "</w:hdr>");
            foreach (var footer in options.Footers)
                Add(zip, "word/" + footer.Key, "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:ftr " + Namespaces + ">" + footer.Value + "</w:ftr>");
            foreach (var (name, data) in options.ExtraEntries)
            {
                using Stream entry = zip.CreateEntry(name).Open();
                entry.Write(data, 0, data.Length);
            }
        }
        return stream.ToArray();
    }

    // Header/footer variant comes from the file name: header-first.xml, footer-even.xml, else default.
    private static string VariantOf(string name) => name.Contains("-first") ? "first" : name.Contains("-even") ? "even" : "default";

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // ---------- WordprocessingML snippets ----------

    public static string P(string runs, string ppr = "") => "<w:p>" + (ppr.Length > 0 ? "<w:pPr>" + ppr + "</w:pPr>" : "") + runs + "</w:p>";

    public static string R(string text, string rpr = "") =>
        "<w:r>" + (rpr.Length > 0 ? "<w:rPr>" + rpr + "</w:rPr>" : "") + "<w:t xml:space=\"preserve\">" + System.Security.SecurityElement.Escape(text) + "</w:t></w:r>";

    public static string Text(string text) => P(R(text));

    public static string Cell(string content, string tcpr = "<w:tcW w:w=\"2000\" w:type=\"dxa\"/>") => "<w:tc><w:tcPr>" + tcpr + "</w:tcPr>" + content + "</w:tc>";

    public static string Row(params string[] cells) => "<w:tr>" + string.Concat(cells) + "</w:tr>";

    public static string RowWithProps(string trpr, params string[] cells) => "<w:tr><w:trPr>" + trpr + "</w:trPr>" + string.Concat(cells) + "</w:tr>";

    public static string Table(int columns, params string[] rows) =>
        "<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr><w:tblGrid>" +
        string.Concat(Enumerable.Repeat("<w:gridCol w:w=\"2000\"/>", columns)) + "</w:tblGrid>" + string.Concat(rows) + "</w:tbl>";

    public static string Checkbox(string tag, int id, bool isChecked = false, string checkedVal = "2612", string uncheckedVal = "2610", string font = "MS Gothic", string? glyph = null)
    {
        glyph ??= char.ConvertFromUtf32(Convert.ToInt32(isChecked ? checkedVal : uncheckedVal, 16));
        return "<w:sdt><w:sdtPr>" + (tag.Length > 0 ? "<w:tag w:val=\"" + tag + "\"/>" : "") + "<w:id w:val=\"" + id + "\"/>" +
            "<w14:checkbox><w14:checked w14:val=\"" + (isChecked ? 1 : 0) + "\"/>" +
            "<w14:checkedState w14:val=\"" + checkedVal + "\" w14:font=\"" + font + "\"/>" +
            "<w14:uncheckedState w14:val=\"" + uncheckedVal + "\" w14:font=\"" + font + "\"/></w14:checkbox></w:sdtPr>" +
            "<w:sdtContent><w:r><w:rPr><w:rFonts w:ascii=\"" + font + "\" w:eastAsia=\"" + font + "\" w:hAnsi=\"" + font + "\" w:hint=\"eastAsia\"/></w:rPr><w:t>" + glyph + "</w:t></w:r></w:sdtContent></w:sdt>";
    }

    public static string Image(int docPrId = 1) =>
        "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"914400\" cy=\"457200\"/><wp:docPr id=\"" + docPrId + "\" name=\"Picture\"/>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"0\" name=\"x.png\"/><pic:cNvPicPr/></pic:nvPicPr>" +
        "<pic:blipFill><a:blip r:embed=\"rIdMissing\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"457200\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    // ---------- inspection ----------

    public static XDocument Part(byte[] docx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(docx));
        ZipArchiveEntry entry = zip.GetEntry(name) ?? throw new InvalidOperationException("Missing part " + name);
        using Stream stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    public static byte[] RawPart(byte[] docx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(docx));
        using Stream stream = zip.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static List<string> EntryNames(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx));
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    /// <summary>Visible text per paragraph: w:t content, w:br as \n, w:tab as \t.</summary>
    public static List<string> Paragraphs(XContainer root) =>
        root.Descendants(Wn + "p").Select(ParagraphText).ToList();

    public static string ParagraphText(XElement paragraph)
    {
        var text = new StringBuilder();
        foreach (XElement element in paragraph.Descendants())
        {
            if (element.Name == Wn + "t") text.Append(element.Value);
            else if (element.Name == Wn + "br") text.Append('\n');
            else if (element.Name == Wn + "tab" && element.Parent?.Name == Wn + "r") text.Append('\t');
        }
        return text.ToString();
    }

    public static string AllText(XContainer root) => string.Join("\n", Paragraphs(root));

    public static List<List<string>> TableText(XElement table) =>
        table.Elements(Wn + "tr").Select(row => row.Elements(Wn + "tc").Select(cell => string.Join("\n", Paragraphs(cell))).ToList()).ToList();

    /// <summary>Validation errors introduced by generation (errors already present in the template are ignored).</summary>
    public static IReadOnlyList<string> NewValidationErrors(byte[] template, byte[] output)
    {
        HashSet<string> before = ValidationErrors(template).ToHashSet();
        return ValidationErrors(output).Where(e => !before.Contains(e)).ToList();
    }

    public static IReadOnlyList<string> ValidationErrors(byte[] docx)
    {
        using var stream = new MemoryStream(docx);
        using WordprocessingDocument document = WordprocessingDocument.Open(stream, false);
        return new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document)
            .Select(e => e.Part?.Uri + " " + e.Description)
            .ToList();
    }
}
