using System.IO.Compression;
using System.Xml.Linq;
using ConnectorTestKit;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Newtonsoft.Json.Linq;

namespace ConnectorTestKit.Tests;

public sealed class HtmlSlideConverterTests
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static async Task<ConnectorInvocationResult> Invoke(string html, bool strict = true, string operation = "ConvertHtmlToPptx")
    {
        string root = ConnectorRepository.FindRepositoryRoot();
        ConnectorDefinition connector = ConnectorRepository.LoadConnector(root, "HtmlSlideConverter");
        return await new ConnectorInvoker().InvokeAsync(connector, new ConnectorInvocationRequest
        {
            OperationId = operation,
            RequestBody = new JObject { ["html"] = html, ["strict"] = strict, ["fileName"] = "Synthetic" }.ToString()
        });
    }

    private static string Slide(string content, string style = "") =>
        "<section class='slide' style='width:1280px;height:720px;" + style + "'>" + content + "</section>";
    private const string Box = "left:40px;top:40px;width:600px;height:100px";

    private static JObject Success(ConnectorInvocationResult result)
    {
        Assert.True(result.StatusCode == 200, result.BodyText);
        Assert.Empty(result.OutboundRequests);
        return JObject.Parse(result.BodyText);
    }

    private static byte[] Bytes(JObject body) => Convert.FromBase64String((string)body["fileContent"]!);
    private static XDocument Xml(ZipArchive zip, string name)
    {
        using Stream stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    private static void ValidatePackage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using PresentationDocument document = PresentationDocument.Open(stream, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(e => e.Part?.Uri + " " + e.Path?.XPath + " " + e.Description)));
    }

    [Fact]
    public async Task Generates_valid_ordered_deck_with_native_text_and_table()
    {
        string html = "<!doctype html><html><head><style>.title {color:#123456;font-size:32px} td {padding:4px;border:1px solid #222222}</style></head><body>" +
            Slide("<div class='title' id='heading' style='" + Box + "'>First <b>bold</b> &amp; café</div>" +
                  "<table id='merged' style='left:40px;top:170px;width:900px;height:200px;font-size:18px'>" +
                  "<colgroup><col style='width:40%'><col style='width:30%'><col style='width:30%'></colgroup>" +
                  "<thead><tr><th colspan='2'>Header</th><th>Other</th></tr></thead>" +
                  "<tbody><tr><td rowspan='2'>Both rows</td><td>A</td><td>B</td></tr><tr><td>C</td><td>D</td></tr></tbody></table>") +
            Slide("<p style='" + Box + "'>Second slide</p>", "background-color:#eeeeee") + "</body></html>";
        JObject body = Success(await Invoke(html));
        Assert.Equal(2, (int)body["slideCount"]!);
        Assert.Equal("Synthetic.pptx", (string)body["fileName"]!);
        byte[] bytes = Bytes(body);
        ValidatePackage(bytes);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        XDocument first = Xml(zip, "ppt/slides/slide1.xml");
        Assert.Contains("First", string.Join("", first.Descendants(A + "t").Select(t => t.Value)));
        Assert.Equal("Second slide", Xml(zip, "ppt/slides/slide2.xml").Descendants(A + "t").Single().Value);
        Assert.Single(first.Descendants(A + "tbl"));
        Assert.Equal("http://schemas.openxmlformats.org/drawingml/2006/table", (string)first.Descendants(A + "graphicData").Single().Attribute("uri")!);
        Assert.Contains(first.Descendants(A + "tc"), c => (string?)c.Attribute("gridSpan") == "2");
        Assert.Contains(first.Descendants(A + "tc"), c => (string?)c.Attribute("rowSpan") == "2");
        Assert.Contains(first.Descendants(A + "tc"), c => (string?)c.Attribute("vMerge") == "1");
        Assert.Equal(3, first.Descendants(A + "gridCol").Count());
        Assert.Contains(first.Descendants(A + "rPr"), r => (string?)r.Attribute("b") == "1");
        Assert.Contains(first.Descendants(A + "srgbClr"), c => (string?)c.Attribute("val") == "123456");
    }

    [Fact]
    public async Task Supports_region_style_table_with_22_grid_columns_and_variable_company_rows()
    {
        string Header() => "<tr style='height:40px;background:#0070c0;color:#ffffff'><th colspan='2'>Tier</th>" +
            string.Concat(Enumerable.Range(1, 10).Select(i => "<th colspan='2'>Region " + i + "</th>")) + "</tr>";
        string Company(string name) => "<tr><td>" + name + "</td><td>Approved</td>" +
            string.Concat(Enumerable.Range(1, 10).Select(i => "<td>Listed</td><td>$0.25</td>")) + "</tr>";
        string Summary(string name) => "<tr><td colspan='2'>" + name + "</td>" +
            string.Concat(Enumerable.Range(1, 10).Select(i => "<td colspan='2'>$0.25</td>")) + "</tr>";
        string table = "<table style='left:20px;top:200px;width:1240px;height:360px;font-size:14px;border:1px solid #000000'>" +
            Header() + Company("Reference") + string.Concat(Enumerable.Range(1, 3).Select(i => Company("Company " + i))) +
            Summary("Selling price") + Summary("Listing price") + Summary("Private") + Summary("Public") + "</table>";
        byte[] bytes = Bytes(Success(await Invoke(Slide(table))));
        ValidatePackage(bytes);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        XDocument xml = Xml(zip, "ppt/slides/slide1.xml");
        Assert.Equal(22, xml.Descendants(A + "gridCol").Count());
        Assert.Equal(9, xml.Descendants(A + "tr").Count());
        Assert.Equal(198, xml.Descendants(A + "tc").Count());
    }

    [Fact]
    public async Task Svg_is_embedded_as_vector_with_independent_picture_relationships()
    {
        string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 300 200' style='left:100px;top:200px;width:300px;height:200px'>" +
            "<rect x='20' y='30' width='60' height='140' fill='#0070c0'/></svg>";
        JObject body = Success(await Invoke(Slide(svg) + Slide(svg)));
        Assert.Equal("SVG_FALLBACK_MISSING", (string)body["warnings"]![0]!["code"]!);
        byte[] bytes = Bytes(body); ValidatePackage(bytes);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Single(zip.Entries, e => e.FullName.EndsWith(".svg"));
        XNamespace svgNs = "http://schemas.microsoft.com/office/drawing/2016/SVG/main";
        Assert.Single(Xml(zip, "ppt/slides/slide1.xml").Descendants(svgNs + "svgBlip"));
        Assert.Single(Xml(zip, "ppt/slides/slide2.xml").Descendants(P + "pic"));
        Assert.NotNull(zip.GetEntry("ppt/slides/_rels/slide2.xml.rels"));
    }

    [Fact]
    public async Task Accepts_embedded_png_and_preserves_alt_text()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4//8/AwAI/AL+XwRZAAAAAElFTkSuQmCC";
        byte[] bytes = Bytes(Success(await Invoke(Slide("<img alt='Example image' src='data:image/png;base64," + png + "' style='" + Box + "'>"))));
        ValidatePackage(bytes);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal("Example image", (string)Xml(zip, "ppt/slides/slide1.xml").Descendants(P + "pic").Single().Descendants(P + "cNvPr").Single().Attribute("descr")!);
    }

    [Theory]
    [InlineData("<p style='left:1250px;top:40px;width:100px;height:100px'>Text</p>", "OUT_OF_BOUNDS")]
    [InlineData("<p>Text</p>", "MISSING_GEOMETRY")]
    [InlineData("<div style='left:0;top:0;width:100px;height:100px;display:flex'>Text</div>", "UNSUPPORTED_LAYOUT")]
    [InlineData("<script>alert(1)</script>", "ACTIVE_CONTENT")]
    [InlineData("<img src='https://example.com/image.png' style='left:0;top:0;width:100px;height:100px'>", "EXTERNAL_ASSET")]
    [InlineData("<p style='left:0;top:0;width:10%;height:100px'>Text</p>", "INVALID_LENGTH")]
    [InlineData("<p style='left:0;top:0;width:100px;height:100px;transform:rotate(10deg)'>Text</p>", "UNSUPPORTED_CSS")]
    [InlineData("<table style='left:0;top:0;width:400px;height:200px'><tr><td>A</td><td>B</td></tr><tr><td>C</td></tr></table>", "INVALID_TABLE")]
    [InlineData("<svg style='left:0;top:0;width:100px;height:100px'><image href='https://example.com/a.png'/></svg>", "EXTERNAL_ASSET")]
    [InlineData("<p style='left:0;top:0;width:100px;height:100px;border-left:1px solid red'>Text</p>", "UNSUPPORTED_STYLE_CONTEXT")]
    [InlineData("<table style='left:0;top:0;width:400px;height:200px'><tr><td style='width:200px'>A</td></tr></table>", "UNSUPPORTED_STYLE_CONTEXT")]
    public async Task Rejects_invalid_or_unsupported_content_without_network(string content, string error)
    {
        ConnectorInvocationResult result = await Invoke(Slide(content));
        Assert.Equal(422, result.StatusCode);
        Assert.Equal(error, (string)JObject.Parse(result.BodyText)["error"]!["code"]!);
        Assert.Empty(result.OutboundRequests);
    }

    [Fact]
    public async Task Validation_returns_inventory_without_file_and_decodes_operation_id()
    {
        string operation = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("ValidateHtmlSlides"));
        JObject body = Success(await Invoke(Slide("<p style='" + Box + "'>Test</p>"), operation: operation));
        Assert.True((bool)body["valid"]!);
        Assert.Null(body["fileContent"]);
        Assert.Equal(1, (int)body["slides"]![0]!["textCount"]!);
    }

    [Fact]
    public async Task Permissive_mode_reports_ignored_properties_but_keeps_supported_content()
    {
        JObject body = Success(await Invoke(Slide("<p style='" + Box + ";box-shadow:0 0 4px black'>Test</p>"), strict: false));
        Assert.Equal("UNSUPPORTED_CSS", (string)body["warnings"]![0]!["code"]!);
        ValidatePackage(Bytes(body));
    }

    [Fact]
    public async Task Nested_positioned_container_offsets_are_applied()
    {
        JObject body = Success(await Invoke(Slide("<div style='left:100px;top:100px;width:700px;height:300px;background:#eeeeee'>" +
            "<p style='left:40px;top:40px;width:200px;height:100px'>Nested</p></div>")));
        using var zip = new ZipArchive(new MemoryStream(Bytes(body)));
        XElement text = Xml(zip, "ppt/slides/slide1.xml").Descendants(P + "sp").Single(s => s.Descendants(A + "t").Any());
        XElement offset = text.Descendants(A + "off").Single();
        Assert.Equal("1333500", (string)offset.Attribute("x")!);
        Assert.Equal("1333500", (string)offset.Attribute("y")!);
    }

    [Fact]
    public async Task Rejects_unclosed_html_and_mismatched_slide_sizes()
    {
        Assert.Equal(422, (await Invoke("<section class='slide'><p>unclosed")).StatusCode);
        ConnectorInvocationResult result = await Invoke(Slide("") + "<section class='slide' style='width:960px;height:540px'></section>");
        Assert.Equal("SLIDE_SIZE_MISMATCH", (string)JObject.Parse(result.BodyText)["error"]!["code"]!);
    }

    [Fact]
    public async Task Simultaneous_horizontal_vertical_merge_and_group_fill_are_preserved()
    {
        string content = "<table style='left:40px;top:100px;width:900px;height:300px;background-color:#0000ff'>" +
            "<tbody style='background:#ff0000'><tr><td rowspan='2' colspan='2'>Merged block</td><td>A</td></tr>" +
            "<tr><td>B</td></tr><tr><td>C</td><td>D</td><td>E</td></tr></tbody></table>";
        byte[] bytes = Bytes(Success(await Invoke(Slide(content)))); ValidatePackage(bytes);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var xml = Xml(zip, "ppt/slides/slide1.xml");
        Assert.Contains(xml.Descendants(A + "tc"), c => (string?)c.Attribute("rowSpan") == "2" && (string?)c.Attribute("gridSpan") == "2");
        Assert.All(xml.Descendants(A + "tcPr"), p => Assert.Equal("FF0000", (string)p.Element(A + "solidFill")!.Element(A + "srgbClr")!.Attribute("val")!));
    }

    [Fact]
    public async Task Svg_local_references_and_supplied_raster_preview_are_supported()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQABpfZFQAAAAABJRU5ErkJggg==";
        string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100' fill='none' data-fallback-src='data:image/png;base64," + png + "' style='left:20px;top:20px;width:100px;height:100px'>" +
            "<defs><linearGradient id='g'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/></linearGradient></defs>" +
            "<rect width='100' height='100' fill='url(\"#g\")'/></svg>";
        JObject body = Success(await Invoke(Slide(svg)));
        Assert.Empty((JArray)body["warnings"]!);
        ValidatePackage(Bytes(body));
    }

    [Fact]
    public async Task Paragraphs_line_breaks_and_preformatted_whitespace_survive_conversion()
    {
        string html = Slide("<div style='" + Box + "'><p>A <b>bold</b></p><p>B<br>C</p></div>" +
            "<p style='left:40px;top:200px;width:600px;height:100px;white-space:pre-wrap'> X  Y\nZ</p>");
        byte[] bytes = Bytes(Success(await Invoke(html))); ValidatePackage(bytes);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        XDocument xml = Xml(zip, "ppt/slides/slide1.xml");
        Assert.Contains(xml.Descendants(A + "t"), t => t.Value == " X  Y");
        Assert.Equal(2, xml.Descendants(A + "br").Count());
        Assert.Equal(3, xml.Descendants(A + "p").Count());
    }

    [Fact]
    public async Task Hundreds_of_independent_slides_are_bounded()
    {
        ConnectorInvocationResult result = await Invoke(string.Concat(Enumerable.Repeat(Slide(""), 101)));
        Assert.Equal(413, result.StatusCode);
        Assert.Equal("LIMIT_EXCEEDED", (string)JObject.Parse(result.BodyText)["error"]!["code"]!);
    }

    [Fact]
    public async Task Stylesheet_specificity_inline_override_and_point_units_are_applied()
    {
        string html = "<style>p{color:red;font-size:24pt}.test{color:blue}#target{color:green}</style>" +
            Slide("<p id='target' class='test' style='" + Box + ";color:#123456'>Hello</p>");
        byte[] bytes = Bytes(Success(await Invoke(html))); ValidatePackage(bytes);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var props = Xml(zip, "ppt/slides/slide1.xml").Descendants(A + "rPr").Single();
        Assert.Equal("2400", (string)props.Attribute("sz")!);
        Assert.Equal("123456", (string)props.Element(A + "solidFill")!.Element(A + "srgbClr")!.Attribute("val")!);
    }
}
