using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ConnectorTestKit;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using static ConnectorTestKit.Tests.DocxTestPackage;

namespace ConnectorTestKit.Tests;

public sealed class DocxTemplateGeneratorTests
{
    private const string Connector = "DocxTemplateGenerator";
    private readonly ITestOutputHelper _output;

    public DocxTemplateGeneratorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ------------------------------------------------------------------
    // Harness helpers
    // ------------------------------------------------------------------

    private static string ConnectorDirectory => Path.Combine(ConnectorRepository.FindRepositoryRoot(), "connectors", Connector);

    private static byte[] Fixture(params string[] relative) => File.ReadAllBytes(Path.Combine(new[] { ConnectorDirectory }.Concat(relative).ToArray()));

    private static async Task<ConnectorInvocationResult> InvokeRaw(string operation, string? body)
    {
        ConnectorDefinition connector = ConnectorRepository.LoadConnector(ConnectorRepository.FindRepositoryRoot(), Connector);
        ConnectorInvocationResult result = await new ConnectorInvoker().InvokeAsync(connector, new ConnectorInvocationRequest
        {
            OperationId = operation,
            RequestBody = body
        });
        Assert.Empty(result.OutboundRequests);
        return result;
    }

    private static Task<ConnectorInvocationResult> Invoke(string operation, JObject body) => InvokeRaw(operation, body.ToString());

    private static JObject Request(byte[] template, object? data = null, bool? strict = null, string? fileName = null)
    {
        var body = new JObject { ["templateBase64"] = Convert.ToBase64String(template) };
        if (data != null) body["dataJson"] = data is string s ? s : JObject.FromObject(data).ToString();
        if (strict.HasValue) body["strictMode"] = strict.Value;
        if (fileName != null) body["fileName"] = fileName;
        return body;
    }

    private static async Task<(JObject Body, byte[] Docx)> Generate(byte[] template, object data, bool strict = true)
    {
        ConnectorInvocationResult result = await Invoke("GenerateDocument", Request(template, data, strict));
        Assert.True(result.StatusCode == 200, result.BodyText);
        JObject body = JObject.Parse(result.BodyText);
        byte[] docx = Convert.FromBase64String((string)body["fileBase64"]!);
        IReadOnlyList<string> errors = NewValidationErrors(template, docx);
        Assert.True(errors.Count == 0, "Open XML validation errors:\n" + string.Join("\n", errors));
        return (body, docx);
    }

    private static async Task<JObject> Fail(byte[] template, object? data, int status, string code, bool strict = true, string operation = "GenerateDocument")
    {
        ConnectorInvocationResult result = await Invoke(operation, Request(template, data, strict));
        Assert.True(result.StatusCode == status, $"Expected {status} {code} but got {result.StatusCode}: {result.BodyText}");
        JObject error = (JObject)JObject.Parse(result.BodyText)["error"]!;
        Assert.Equal(code, (string)error["code"]!);
        Assert.False(string.IsNullOrWhiteSpace((string?)error["correlationId"]));
        Assert.Null(JObject.Parse(result.BodyText)["fileBase64"]);
        return error;
    }

    private static XDocument Body(byte[] docx) => Part(docx, "word/document.xml");

    private static byte[] Doc(params string[] blocks) => Build(string.Concat(blocks));

    // ------------------------------------------------------------------
    // Plain text
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replaces_single_multiple_and_repeated_tags()
    {
        byte[] template = Doc(Text("Hello {Name}!"), Text("{Name} and {Other} and {Name}"), Text("No tags here."));
        var (body, docx) = await Generate(template, new { Name = "Ada", Other = "Grace" });
        Assert.Equal(new[] { "Hello Ada!", "Ada and Grace and Ada", "No tags here." }, Paragraphs(Body(docx)));
        Assert.Equal(4, (int)body["statistics"]!["textFieldsReplaced"]!);
        Assert.Empty((JArray)body["warnings"]!);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", (string)body["mimeType"]!);
        Assert.Equal("Document.docx", (string)body["fileName"]!);
    }

    [Theory]
    [InlineData("<b> & \"quoted\" 'apos' > done")]
    [InlineData("Café crème, l'été à Montréal — Œuvre «française»")]
    [InlineData("東京 🚀 ✓ Ωmega")]
    [InlineData("  leading and trailing  ")]
    public async Task Renders_special_characters_safely(string value)
    {
        var (_, docx) = await Generate(Doc(Text("[{Value}]")), new { Value = value });
        Assert.Equal("[" + value + "]", Paragraphs(Body(docx)).Single());
        Assert.All(Body(docx).Descendants(Wn + "t").Where(t => t.Value.Contains(value.Trim())),
            t => Assert.Equal("preserve", (string?)t.Attribute(XNamespace.Xml + "space")));
    }

    [Fact]
    public async Task Line_breaks_and_tabs_become_word_elements_in_the_same_run()
    {
        var (_, docx) = await Generate(Doc(P(R("Notes: {Notes}.", "<w:b/>"))), new { Notes = "one\r\ntwo\nthree\tfour" });
        XElement paragraph = Body(docx).Descendants(Wn + "p").Single();
        Assert.Equal("Notes: one\ntwo\nthree\tfour.", ParagraphText(paragraph));
        XElement run = paragraph.Elements(Wn + "r").Single();
        Assert.NotNull(run.Element(Wn + "rPr")!.Element(Wn + "b"));
        Assert.Equal(2, run.Elements(Wn + "br").Count());
        Assert.Single(run.Elements(Wn + "tab"));
        Assert.DoesNotContain(Body(docx).Descendants(Wn + "t"), t => t.Value.Contains('\n'));
    }

    [Fact]
    public async Task Long_text_and_number_and_boolean_values_use_deterministic_plain_text()
    {
        string longText = new string('x', 30000);
        var (_, docx) = await Generate(Doc(Text("{Long}"), Text("{Int}|{Dec}|{Neg}|{Flag}|{Zero}")),
            "{\"Long\":\"" + longText + "\",\"Int\":42,\"Dec\":12.50,\"Neg\":-0.5,\"Flag\":true,\"Zero\":0}");
        List<string> paragraphs = Paragraphs(Body(docx));
        Assert.Equal(longText, paragraphs[0]);
        Assert.Equal("42|12.50|-0.5|true|0", paragraphs[1]);
    }

    [Fact]
    public async Task Text_in_static_tables_is_replaced_without_changing_the_table()
    {
        byte[] template = Doc(Table(2, Row(Cell(Text("Company")), Cell(Text("{CompanyName}")))));
        var (_, docx) = await Generate(template, new { CompanyName = "Example Pharma Inc." });
        Assert.Equal(new[] { new[] { "Company", "Example Pharma Inc." }.ToList() }, TableText(Body(docx).Descendants(Wn + "tbl").Single()));
    }

    [Fact]
    public async Task Empty_string_is_legal_and_dates_stay_as_supplied_strings()
    {
        var (_, docx) = await Generate(Doc(Text("A{Empty}B {Date}")), JObject.Parse("{\"Empty\":\"\",\"Date\":\"2026-10-11T08:00:00Z\"}"));
        Assert.Equal("AB 2026-10-11T08:00:00Z", Paragraphs(Body(docx)).Single());
    }

    [Fact]
    public async Task Escaped_braces_render_as_literal_braces()
    {
        var (_, docx) = await Generate(Doc(Text("JSON {{\"a\": 1}} and a lone {{ brace — {Name}")), new { Name = "ok" });
        Assert.Equal("JSON {\"a\": 1} and a lone { brace — ok", Paragraphs(Body(docx)).Single());
    }

    // ------------------------------------------------------------------
    // Fragmented runs
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("{Company|Name}")]
    [InlineData("{|CompanyName|}")]
    [InlineData("{Com|pany|Na|me}")]
    public async Task Tags_split_across_runs_are_replaced(string split)
    {
        string[] parts = split.Split('|');
        string runs = R("Prefix ", "<w:i/>") + string.Join("<w:proofErr w:type=\"spellStart\"/>", parts.Select(p => R(p, "<w:b/>"))) +
            "<w:bookmarkStart w:id=\"7\" w:name=\"_GoBack\"/><w:bookmarkEnd w:id=\"7\"/>" + R(" suffix", "<w:u w:val=\"single\"/>");
        var (_, docx) = await Generate(Doc(P(runs)), new { CompanyName = "Exemple Pharma" });
        XElement paragraph = Body(docx).Descendants(Wn + "p").Single();
        Assert.Equal("Prefix Exemple Pharma suffix", ParagraphText(paragraph));
        List<XElement> runsOut = paragraph.Elements(Wn + "r").ToList();
        Assert.Equal(3, runsOut.Count);
        Assert.NotNull(runsOut[0].Element(Wn + "rPr")!.Element(Wn + "i"));
        Assert.Equal("Exemple Pharma", runsOut[1].Element(Wn + "t")!.Value);
        Assert.NotNull(runsOut[1].Element(Wn + "rPr")!.Element(Wn + "b"));
        Assert.NotNull(runsOut[2].Element(Wn + "rPr")!.Element(Wn + "u"));
        Assert.Single(paragraph.Elements(Wn + "bookmarkStart"));
    }

    [Fact]
    public async Task Replacement_inherits_formatting_of_the_first_tag_character()
    {
        string runs = R("Dear {", "<w:color w:val=\"FF0000\"/>") + R("Customer.Name}", "<w:b/>") + R(", welcome.", "<w:i/>");
        var (_, docx) = await Generate(Doc(P(runs)), new { Customer = new { Name = "Zoë" } });
        List<XElement> runsOut = Body(docx).Descendants(Wn + "r").ToList();
        Assert.Equal("Dear Zoë", runsOut[0].Element(Wn + "t")!.Value);
        Assert.NotNull(runsOut[0].Descendants(Wn + "color").SingleOrDefault());
        Assert.Equal(", welcome.", runsOut[1].Element(Wn + "t")!.Value);
        Assert.NotNull(runsOut[1].Descendants(Wn + "i").SingleOrDefault());
        Assert.Equal(2, runsOut.Count);
    }

    [Fact]
    public async Task Multiple_fragmented_tags_in_one_paragraph_keep_surrounding_text()
    {
        string runs = R("A{Fir") + R("st}B{") + R("Second") + R("}C");
        var (_, docx) = await Generate(Doc(P(runs)), new { First = "1", Second = "2" });
        Assert.Equal("A1B2C", Paragraphs(Body(docx)).Single());
    }

    [Theory]
    [InlineData("<w:r><w:t>{Na</w:t></w:r><w:r><w:tab/><w:t>me}</w:t></w:r>")]
    [InlineData("<w:r><w:t>{Na</w:t><w:br/><w:t>me}</w:t></w:r>")]
    [InlineData("<w:hyperlink w:anchor=\"x\"><w:r><w:t>{Na</w:t></w:r></w:hyperlink><w:r><w:t>me}</w:t></w:r>")]
    public async Task Tags_interrupted_by_breaks_or_container_boundaries_are_rejected(string runs)
    {
        JObject error = await Fail(Doc(P(runs)), new { Name = "x" }, 422, "INVALID_PLACEHOLDER");
        Assert.Equal("word/document.xml", (string)error["part"]!);
        Assert.Equal("paragraph 1", (string)error["location"]!);
    }

    [Fact]
    public async Task Tags_split_across_paragraphs_or_cells_are_rejected()
    {
        await Fail(Doc(Text("{Na"), Text("me}")), new { Name = "x" }, 422, "INVALID_PLACEHOLDER");
        await Fail(Doc(Table(2, Row(Cell(Text("{Na")), Cell(Text("me}"))))), new { Name = "x" }, 422, "INVALID_PLACEHOLDER");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{Price: 5}")]
    [InlineData("{a..b}")]
    [InlineData("{Root}")]
    [InlineData("stray } brace")]
    [InlineData("{{Name}}")]
    public async Task Malformed_or_ambiguous_tags_are_rejected(string text)
    {
        await Fail(Doc(Text(text)), new { Name = "x" }, 422, "INVALID_PLACEHOLDER");
    }

    [Fact]
    public async Task Hyperlink_text_and_inline_content_controls_are_rewritten_inside_their_boundaries()
    {
        string runs = R("See ") + "<w:hyperlink w:anchor=\"top\"><w:r><w:t>{LinkText}</w:t></w:r></w:hyperlink>" +
            "<w:sdt><w:sdtPr><w:id w:val=\"55\"/></w:sdtPr><w:sdtContent><w:r><w:t>[{Inside}]</w:t></w:r></w:sdtContent></w:sdt>";
        var (_, docx) = await Generate(Doc(P(runs)), new { LinkText = "the summary", Inside = "ok" });
        XElement paragraph = Body(docx).Descendants(Wn + "p").Single();
        Assert.Equal("See the summary[ok]", ParagraphText(paragraph));
        Assert.Equal("the summary", paragraph.Element(Wn + "hyperlink")!.Descendants(Wn + "t").Single().Value);
    }

    [Fact]
    public async Task Field_results_are_never_rewritten_and_tags_inside_them_are_rejected()
    {
        string page = "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";
        var (_, docx) = await Generate(Doc(P(R("Page ") + page + R(" of {Doc}"))), new { Doc = "X" });
        Assert.Equal("Page 1 of X", Paragraphs(Body(docx)).Single());
        Assert.Contains(Body(docx).Descendants(Wn + "instrText"), i => i.Value.Contains("PAGE"));
        string tagged = page.Replace("<w:t>1</w:t>", "<w:t>{Doc}</w:t>");
        await Fail(Doc(P(tagged)), new { Doc = "X" }, 422, "UNSUPPORTED_TEMPLATE_STRUCTURE");
    }

    // ------------------------------------------------------------------
    // Tables
    // ------------------------------------------------------------------

    private static byte[] ProductTable(string extraRows = "", string loopTrPr = "<w:cantSplit/>") => Doc(
        Table(3,
            RowWithProps("<w:tblHeader/>", Cell(Text("Product")), Cell(Text("DIN")), Cell(Text("Price"))),
            RowWithProps(loopTrPr,
                Cell(P(R("{#Products}") + R("{Name}", "<w:b/>")), "<w:tcW w:w=\"2000\" w:type=\"dxa\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"DDEEFF\"/>"),
                Cell(Text("{DIN}")),
                Cell(P(R("{PriceFormatted}{/Products}"), "<w:jc w:val=\"right\"/>"))),
            extraRows,
            Row(Cell(Text("Total")), Cell(Text("")), Cell(Text("{Total}"))))
    );

    private static object Products(int count) => new
    {
        Total = "$" + count,
        Products = Enumerable.Range(1, count).Select(i => new { Name = "Product " + i, DIN = i.ToString("D8"), PriceFormatted = "$" + i + ".00" }).ToArray()
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(150)]
    public async Task Repeats_the_template_row_for_each_item(int count)
    {
        var (body, docx) = await Generate(ProductTable(), Products(count));
        XElement table = Body(docx).Descendants(Wn + "tbl").Single();
        List<List<string>> rows = TableText(table);
        Assert.Equal(count + 2, rows.Count);
        Assert.Equal(new[] { "Product", "DIN", "Price" }, rows[0]);
        Assert.Equal(new[] { "Total", "", "$" + count }, rows[^1]);
        for (int i = 1; i <= count; i++)
            Assert.Equal(new[] { "Product " + i, i.ToString("D8"), "$" + i + ".00" }, rows[i]);
        Assert.Equal(count, (int)body["statistics"]!["tableRowsCreated"]!);

        List<XElement> dataRows = table.Elements(Wn + "tr").Skip(1).Take(count).ToList();
        Assert.All(dataRows, row =>
        {
            Assert.NotNull(row.Element(Wn + "trPr")!.Element(Wn + "cantSplit"));
            Assert.Equal("DDEEFF", (string)row.Descendants(Wn + "shd").Single().Attribute(Wn + "fill")!);
            Assert.Equal("right", (string)row.Elements(Wn + "tc").Last().Descendants(Wn + "jc").Single().Attribute(Wn + "val")!);
            Assert.NotNull(row.Elements(Wn + "tc").First().Descendants(Wn + "b").SingleOrDefault());
        });
        Assert.DoesNotContain("{", AllText(Body(docx)));
    }

    [Fact]
    public async Task Separate_tables_with_identical_field_names_bind_to_their_own_items()
    {
        string LoopTable(string name) => Table(2, Row(Cell(Text("{#" + name + "}{Name}")), Cell(Text("{Value}{/" + name + "}"))));
        byte[] template = Doc(LoopTable("First"), Text("between"), LoopTable("Second"));
        var data = new
        {
            First = new[] { new { Name = "a", Value = "1" }, new { Name = "b", Value = "2" } },
            Second = new[] { new { Name = "c", Value = "3" } }
        };
        var (_, docx) = await Generate(template, data);
        List<XElement> tables = Body(docx).Descendants(Wn + "tbl").ToList();
        Assert.Equal(new[] { new List<string> { "a", "1" }, new List<string> { "b", "2" } }, TableText(tables[0]));
        Assert.Equal(new[] { new List<string> { "c", "3" } }, TableText(tables[1]));
    }

    [Fact]
    public async Task Row_items_resolve_locally_and_Root_reaches_top_level_data()
    {
        byte[] template = Doc(Table(2, Row(Cell(Text("{#Items}{Name}")), Cell(Text("{Root.Currency} {Price}{/Items}")))));
        var (_, docx) = await Generate(template, new { Currency = "CAD", Items = new[] { new { Name = "A", Price = "1" } } });
        Assert.Equal(new List<string> { "A", "CAD 1" }, TableText(Body(docx).Descendants(Wn + "tbl").Single())[0]);

        JObject error = await Fail(Doc(Table(2, Row(Cell(Text("{#Items}{Name}")), Cell(Text("{Currency}{/Items}"))))),
            new { Currency = "CAD", Items = new[] { new { Name = "A" } } }, 422, "MISSING_FIELD");
        Assert.Contains("Root.Currency", (string)error["message"]!);
        Assert.Contains("(item 1)", (string)error["location"]!);
    }

    [Fact]
    public async Task Marker_only_paragraphs_in_loop_cells_do_not_leave_blank_lines()
    {
        byte[] template = Doc(Table(2, Row(Cell(Text("{#Items}") + Text("{Name}")), Cell(Text("{Value}") + Text("{/Items}")))));
        var (_, docx) = await Generate(template, new { Items = new[] { new { Name = "A", Value = "1" } } });
        XElement row = Body(docx).Descendants(Wn + "tr").Single();
        Assert.All(row.Elements(Wn + "tc"), cell => Assert.Single(cell.Elements(Wn + "p")));
    }

    [Fact]
    public async Task Single_column_tables_may_hold_both_markers_in_one_cell()
    {
        var (_, docx) = await Generate(Doc(Table(1, Row(Cell(Text("Items"))), Row(Cell(Text("{#Items}{Name}{/Items}"))))),
            new { Items = new[] { new { Name = "A" }, new { Name = "B" } } });
        Assert.Equal(new[] { "Items", "A", "B" }, TableText(Body(docx).Descendants(Wn + "tbl").Single()).Select(r => r[0]));
    }

    [Fact]
    public async Task Repeated_rows_with_checkboxes_get_unique_ids_and_their_own_state()
    {
        byte[] template = Doc(Table(2, Row(Cell(Text("{#Items}{Name}")), Cell(P(Checkbox("Done", 777) + R("{/Items}"))))));
        var data = new { Items = new[] { new { Name = "A", Done = true }, new { Name = "B", Done = false }, new { Name = "C", Done = true } } };
        var (body, docx) = await Generate(template, data);
        List<XElement> sdts = Body(docx).Descendants(Wn + "sdt").ToList();
        Assert.Equal(3, sdts.Count);
        Assert.Equal(3, sdts.Select(s => (string)s.Descendants(Wn + "id").Single().Attribute(Wn + "val")!).Distinct().Count());
        Assert.Equal("777", (string)sdts[0].Descendants(Wn + "id").Single().Attribute(Wn + "val")!);
        Assert.Equal(new[] { "1", "0", "1" }, sdts.Select(s => (string)s.Descendants(W14 + "checked").Single().Attribute(W14 + "val")!));
        Assert.Equal(new[] { "☒", "☐", "☒" }, sdts.Select(s => s.Descendants(Wn + "t").Single().Value));
        Assert.All(sdts, s => Assert.Equal("Done", (string)s.Descendants(Wn + "tag").Single().Attribute(Wn + "val")!));
        Assert.Equal(3, (int)body["statistics"]!["checkboxesUpdated"]!);
    }

    [Fact]
    public async Task Clones_drop_duplicate_paragraph_ids_and_bookmarks_but_keep_the_first()
    {
        string cell = "<w:p w14:paraId=\"1A2B3C4D\" w14:textId=\"77777777\"><w:bookmarkStart w:id=\"3\" w:name=\"Row\"/><w:r><w:t>{#Items}{Name}</w:t></w:r><w:bookmarkEnd w:id=\"3\"/></w:p>";
        byte[] template = Doc(Table(2, Row(Cell(cell), Cell(Text("{/Items}")))));
        var (_, docx) = await Generate(template, new { Items = new[] { new { Name = "A" }, new { Name = "B" } } });
        XDocument xml = Body(docx);
        Assert.Single(xml.Descendants(Wn + "bookmarkStart"));
        Assert.Single(xml.Descendants(Wn + "p"), p => p.Attribute(W14 + "paraId") != null);
    }

    [Fact]
    public async Task A_loop_spanning_two_rows_is_rejected_with_location()
    {
        byte[] template = Doc(Table(2, Row(Cell(Text("Header")), Cell(Text("Header"))),
            Row(Cell(Text("{#Products}{Name}")), Cell(Text(""))), Row(Cell(Text("{DIN}")), Cell(Text("{/Products}")))));
        JObject error = await Fail(template, Products(1), 422, "UNSUPPORTED_TEMPLATE_STRUCTURE");
        Assert.Equal("Products", (string)error["field"]!);
        Assert.Equal("table 1, row 2", (string)error["location"]!);
        Assert.Contains("spans multiple table rows", (string)error["message"]!);
    }

    [Theory]
    [InlineData("{#Items}{Name}{/Items}", "same cell")]
    [InlineData("{#Items}{#Other}{Name}", "only one row section")]
    public async Task Ambiguous_row_markers_are_rejected(string firstCell, string fragment)
    {
        string last = fragment == "same cell" ? "x" : "{/Other}{/Items}";
        JObject error = await Fail(Doc(Table(2, Row(Cell(Text(firstCell)), Cell(Text(last))))),
            new { Items = new[] { new { Name = "a" } }, Other = true }, 422, "UNSUPPORTED_TEMPLATE_STRUCTURE");
        Assert.Contains(fragment, (string)error["message"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<w:vMerge w:val=\"restart\"/>", "vertically merged")]
    [InlineData("", "image")]
    [InlineData("", "tracked changes")]
    public async Task Rows_that_cannot_be_cloned_safely_are_rejected_for_arrays_but_allowed_as_conditions(string tcpr, string reason)
    {
        string content = reason == "image" ? P(R("{#Items}") + Image()) : reason == "tracked changes"
            ? P(R("{#Items}") + "<w:ins w:id=\"1\" w:author=\"a\" w:date=\"2024-01-01T00:00:00Z\"><w:r><w:t>new</w:t></w:r></w:ins>") : Text("{#Items}");
        byte[] template = Doc(Table(2, Row(Cell(content, "<w:tcW w:w=\"2000\" w:type=\"dxa\"/>" + tcpr), Cell(Text("{/Items}")))));
        JObject error = await Fail(template, new { Items = new[] { new { } } }, 422, "UNSUPPORTED_TEMPLATE_STRUCTURE");
        Assert.Contains(reason, (string)error["message"]!);
        ConnectorInvocationResult ok = await Invoke("GenerateDocument", Request(template, new { Items = true }));
        Assert.Equal(200, ok.StatusCode);
    }

    [Fact]
    public async Task Removing_every_row_removes_the_table_with_a_warning()
    {
        var (body, docx) = await Generate(Doc(Text("Before"), Table(1, Row(Cell(Text("{#Items}{Name}{/Items}")))), Text("After")), new { Items = Array.Empty<object>() });
        Assert.Empty(Body(docx).Descendants(Wn + "tbl"));
        Assert.Equal("EMPTY_TABLE_REMOVED", (string)body["warnings"]![0]!["code"]!);
        Assert.Equal(new[] { "Before", "After" }, Paragraphs(Body(docx)));
    }

    [Theory]
    [InlineData("\"text\"", "TYPE_MISMATCH")]
    [InlineData("[1, 2]", "TYPE_MISMATCH")]
    [InlineData("{\"a\":1}", "TYPE_MISMATCH")]
    public async Task Row_sections_need_arrays_of_objects_or_booleans(string value, string code)
    {
        await Fail(ProductTable(), "{\"Total\":\"1\",\"Products\":" + value + "}", 422, code);
    }

    [Fact]
    public async Task Too_many_loop_items_hit_the_resource_limit()
    {
        await Fail(ProductTable(), Products(1001), 413, "RESOURCE_LIMIT_EXCEEDED");
    }

    // ------------------------------------------------------------------
    // Conditionals
    // ------------------------------------------------------------------

    private static byte[] ConditionalDoc() => Doc(
        Text("Intro"),
        Text("{#ShowA}"),
        P(R("A content {Name}"), "<w:pStyle w:val=\"Disclaimer\"/><w:spacing w:before=\"240\" w:after=\"240\"/>"),
        Table(1, Row(Cell(Text("A table")))),
        Text("{/ShowA}"),
        Text("{#ShowB}"),
        Text("B content"),
        Text("{#ShowNested}"),
        Text("Nested content"),
        Text("{/ShowNested}"),
        Text("{/ShowB}"),
        Text("Outro"));

    [Theory]
    [InlineData(true, true, true, new[] { "Intro", "A content Zed", "A table", "B content", "Nested content", "Outro" })]
    [InlineData(false, true, false, new[] { "Intro", "B content", "Outro" })]
    [InlineData(true, false, true, new[] { "Intro", "A content Zed", "A table", "Outro" })]
    [InlineData(false, false, false, new[] { "Intro", "Outro" })]
    public async Task Paragraph_blocks_keep_or_remove_content_and_always_remove_markers(bool a, bool b, bool nested, string[] expected)
    {
        var (body, docx) = await Generate(ConditionalDoc(), new { ShowA = a, ShowB = b, ShowNested = nested, Name = "Zed" });
        Assert.Equal(expected, Paragraphs(Body(docx)));
        if (a)
        {
            XElement styled = Body(docx).Descendants(Wn + "p").First(p => ParagraphText(p).StartsWith("A content"));
            Assert.Equal("240", (string)styled.Descendants(Wn + "spacing").Single().Attribute(Wn + "before")!);
            Assert.Equal("Disclaimer", (string)styled.Descendants(Wn + "pStyle").Single().Attribute(Wn + "val")!);
        }
        Assert.True((int)body["statistics"]!["conditionalBlocksEvaluated"]! >= 2);
    }

    [Fact]
    public async Task False_blocks_do_not_require_their_inner_values()
    {
        var (_, docx) = await Generate(ConditionalDoc(), new { ShowA = false, ShowB = false });
        Assert.Equal(new[] { "Intro", "Outro" }, Paragraphs(Body(docx)));
    }

    [Fact]
    public async Task Boolean_table_rows_are_kept_or_removed()
    {
        byte[] template = Doc(Table(2, Row(Cell(Text("Always")), Cell(Text("1"))),
            Row(Cell(Text("{#ShowDiscount}Discount")), Cell(Text("{Discount}{/ShowDiscount}"))),
            Row(Cell(Text("Total")), Cell(Text("9")))));
        var (_, kept) = await Generate(template, new { ShowDiscount = true, Discount = "-1" });
        Assert.Equal(new[] { "Always", "Discount", "Total" }, TableText(Body(kept).Descendants(Wn + "tbl").Single()).Select(r => r[0]));
        var (_, removed) = await Generate(template, new { ShowDiscount = false });
        Assert.Equal(new[] { "Always", "Total" }, TableText(Body(removed).Descendants(Wn + "tbl").Single()).Select(r => r[0]));
    }

    [Fact]
    public async Task Conditions_inside_repeated_rows_use_each_item()
    {
        string first = Text("{#Items}{Name}") + Text("{#HasNote}") + Text("Note: {Note}") + Text("{/HasNote}");
        byte[] template = Doc(Table(2, Row(Cell(first), Cell(Text("{/Items}")))));
        var (_, docx) = await Generate(template, new { Items = new object[] { new { Name = "A", HasNote = true, Note = "n1" }, new { Name = "B", HasNote = false } } });
        Assert.Equal(new[] { "A\nNote: n1", "B" }, TableText(Body(docx).Descendants(Wn + "tbl").Single()).Select(r => r[0]));
    }

    [Fact]
    public async Task Missing_conditions_fail_in_strict_mode_and_are_removed_with_warnings_otherwise()
    {
        JObject error = await Fail(ConditionalDoc(), new { ShowB = false, Name = "x" }, 422, "MISSING_FIELD");
        Assert.Equal("ShowA", (string)error["field"]!);
        var (body, docx) = await Generate(ConditionalDoc(), new { ShowB = false, Name = "x" }, strict: false);
        Assert.Equal(new[] { "Intro", "Outro" }, Paragraphs(Body(docx)));
        Assert.Contains((JArray)body["warnings"]!, w => (string)w["code"]! == "MISSING_FIELD" && (string)w["field"]! == "ShowA");
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("\"yes\"")]
    public async Task Only_json_booleans_are_conditions(string value)
    {
        JObject error = await Fail(ConditionalDoc(), "{\"ShowA\":" + value + ",\"ShowB\":false,\"Name\":\"x\"}", 422, "TYPE_MISMATCH");
        Assert.Equal("ShowA", (string)error["field"]!);
    }

    [Fact]
    public async Task Arrays_on_paragraph_blocks_explain_the_row_alternative()
    {
        JObject error = await Fail(ConditionalDoc(), new { ShowA = new[] { 1 }, ShowB = false }, 422, "TYPE_MISMATCH");
        Assert.Contains("table row", (string)error["message"]!);
    }

    [Theory]
    [InlineData(new[] { "{#A}", "x" }, "UNBALANCED_MARKER")]
    [InlineData(new[] { "x", "{/A}" }, "UNBALANCED_MARKER")]
    [InlineData(new[] { "{#A}", "{#B}", "{/A}", "{/B}" }, "UNBALANCED_MARKER")]
    [InlineData(new[] { "Inline {#A}text{/A} here" }, "UNSUPPORTED_TEMPLATE_STRUCTURE")]
    public async Task Malformed_blocks_are_rejected(string[] paragraphs, string code)
    {
        await Fail(Doc(paragraphs.Select(Text).ToArray()), new { A = true, B = true }, 422, code);
    }

    [Fact]
    public async Task Blocks_cannot_remove_section_breaks()
    {
        string sectionBreak = "<w:p><w:pPr><w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/></w:sectPr></w:pPr></w:p>";
        await Fail(Doc(Text("{#A}"), sectionBreak, Text("{/A}")), new { A = false }, 422, "UNSUPPORTED_TEMPLATE_STRUCTURE");
    }

    [Fact]
    public async Task Adjacent_blocks_and_removed_bookmarks_leave_no_orphans()
    {
        string bookmarked = "<w:p><w:bookmarkStart w:id=\"9\" w:name=\"Spot\"/><w:r><w:t>{#A}</w:t></w:r></w:p>";
        byte[] template = Doc(bookmarked, Text("a"), Text("{/A}"), Text("{#B}"), "<w:p><w:r><w:t>b</w:t></w:r><w:bookmarkEnd w:id=\"9\"/></w:p>", Text("{/B}"));
        var (_, docx) = await Generate(template, new { A = true, B = false });
        Assert.Equal(new[] { "a" }, Paragraphs(Body(docx)));
        Assert.Empty(Body(docx).Descendants(Wn + "bookmarkStart"));
        Assert.Empty(Body(docx).Descendants(Wn + "bookmarkEnd"));
    }

    [Fact]
    public async Task Removing_the_last_paragraph_of_a_cell_keeps_the_cell_valid()
    {
        byte[] template = Doc(Table(1, Row(Cell(Text("{#A}") + Text("only") + Text("{/A}")))));
        var (_, docx) = await Generate(template, new { A = false });
        XElement cell = Body(docx).Descendants(Wn + "tc").Single();
        Assert.Equal(Wn + "p", cell.Elements().Last().Name);
    }

    // ------------------------------------------------------------------
    // Checkboxes
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(false, true, "1", "☒")]
    [InlineData(true, false, "0", "☐")]
    [InlineData(true, true, "1", "☒")]
    public async Task Checkboxes_follow_boolean_values_and_stay_content_controls(bool initial, bool value, string state, string glyph)
    {
        byte[] template = Doc(P(R("Approved ") + Checkbox("IsApproved", 42, initial)));
        var (body, docx) = await Generate(template, new { IsApproved = value });
        XElement sdt = Body(docx).Descendants(Wn + "sdt").Single();
        Assert.Equal(state, (string)sdt.Descendants(W14 + "checked").Single().Attribute(W14 + "val")!);
        Assert.Equal(glyph, sdt.Descendants(Wn + "t").Single().Value);
        Assert.Equal("42", (string)sdt.Descendants(Wn + "id").Single().Attribute(Wn + "val")!);
        Assert.Equal("2612", (string)sdt.Descendants(W14 + "checkedState").Single().Attribute(W14 + "val")!);
        Assert.Equal("MS Gothic", (string)sdt.Descendants(Wn + "rFonts").Single().Attribute(Wn + "ascii")!);
        Assert.Equal(1, (int)body["statistics"]!["checkboxesUpdated"]!);
    }

    [Fact]
    public async Task Checkboxes_in_headers_and_footers_are_updated()
    {
        var options = new Options();
        options.Headers["header1.xml"] = P(R("Header ") + Checkbox("InHeader", 1));
        options.Footers["footer1.xml"] = P(R("Footer ") + Checkbox("InFooter", 2, true));
        byte[] template = Build(Text("Body"), options);
        var (_, docx) = await Generate(template, new { InHeader = true, InFooter = false });
        Assert.Equal("☒", Part(docx, "word/header1.xml").Descendants(Wn + "t").Last().Value);
        Assert.Equal("☐", Part(docx, "word/footer1.xml").Descendants(Wn + "t").Last().Value);
    }

    [Fact]
    public async Task Custom_symbol_font_checkboxes_switch_glyph_and_font()
    {
        // Wingdings 0xFE (checked box) / Segoe UI Symbol 0x2610, as configured in Word's Change... dialog.
        string sdt = "<w:sdt><w:sdtPr><w:tag w:val=\"Custom\"/><w:id w:val=\"5\"/><w14:checkbox><w14:checked w14:val=\"0\"/>" +
            "<w14:checkedState w14:val=\"00FE\" w14:font=\"Wingdings\"/><w14:uncheckedState w14:val=\"00A8\" w14:font=\"Wingdings\"/></w14:checkbox></w:sdtPr>" +
            "<w:sdtContent><w:r><w:rPr><w:rFonts w:ascii=\"Wingdings\" w:hAnsi=\"Wingdings\"/></w:rPr><w:t>\uF0A8</w:t></w:r></w:sdtContent></w:sdt>";
        var (_, docx) = await Generate(Doc(P(sdt)), new { Custom = true });
        Assert.Equal("\uF0FE", Body(docx).Descendants(Wn + "t").Single().Value);

        string mixedFonts = Checkbox("Mixed", 6, false, "2612", "2610", "MS Gothic").Replace("w14:uncheckedState w14:val=\"2610\" w14:font=\"MS Gothic\"", "w14:uncheckedState w14:val=\"2610\" w14:font=\"Segoe UI Symbol\"");
        var (_, mixed) = await Generate(Doc(P(mixedFonts)), new { Mixed = true });
        XElement fonts = Body(mixed).Descendants(Wn + "rFonts").Single();
        Assert.Equal("MS Gothic", (string)fonts.Attribute(Wn + "ascii")!);
        var (_, unchecked_) = await Generate(Doc(P(mixedFonts.Replace("☐", "☐"))), new { Mixed = false });
        Assert.Equal("Segoe UI Symbol", (string)Body(unchecked_).Descendants(Wn + "rFonts").Single().Attribute(Wn + "ascii")!);
    }

    [Fact]
    public async Task Unbound_checkboxes_are_left_untouched()
    {
        byte[] template = Doc(P(Checkbox("", 9, true)));
        var (body, docx) = await Generate(template, new { });
        Assert.Equal("☒", Body(docx).Descendants(Wn + "t").Single().Value);
        Assert.Equal(0, (int)body["statistics"]!["checkboxesUpdated"]!);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task Checkbox_values_must_be_booleans(string value)
    {
        string code = value == "null" ? "MISSING_FIELD" : "TYPE_MISMATCH";
        JObject error = await Fail(Doc(P(Checkbox("IsApproved", 1))), "{\"IsApproved\":" + value + "}", 422, code);
        Assert.Equal("IsApproved", (string)error["field"]!);
    }

    [Fact]
    public async Task Unsupported_checkbox_shapes_fail_instead_of_desynchronizing()
    {
        string twoRuns = Checkbox("A", 1).Replace("</w:r></w:sdtContent>", "</w:r><w:r><w:t>x</w:t></w:r></w:sdtContent>");
        await Fail(Doc(P(twoRuns)), new { A = true }, 422, "UNSUPPORTED_CHECKBOX");
        string wrongGlyph = Checkbox("A", 1, glyph: "X");
        await Fail(Doc(P(wrongGlyph)), new { A = true }, 422, "UNSUPPORTED_CHECKBOX");
        string bound = Checkbox("A", 1).Replace("<w14:checkbox>", "<w:dataBinding w:xpath=\"/root/a\" w:storeItemID=\"{00000000-0000-0000-0000-000000000000}\"/><w14:checkbox>");
        await Fail(Doc(P(bound)), new { A = true }, 422, "UNSUPPORTED_CHECKBOX");
        await Fail(Doc(P(Checkbox("Has#Hash", 1))), new { A = true }, 422, "UNSUPPORTED_CHECKBOX");
    }

    [Fact]
    public async Task Duplicate_checkbox_tags_in_one_scope_are_reported()
    {
        byte[] template = Doc(P(Checkbox("IsApproved", 1) + Checkbox("IsApproved", 2)));
        await Fail(template, new { IsApproved = true }, 422, "DUPLICATE_CHECKBOX_TAG");
        var (body, _) = await Generate(template, new { IsApproved = true }, strict: false);
        Assert.Contains((JArray)body["warnings"]!, w => (string)w["code"]! == "DUPLICATE_CHECKBOX_TAG");
    }

    // ------------------------------------------------------------------
    // Headers, footers and the end-to-end example template
    // ------------------------------------------------------------------

    private static JObject ExampleData() => JObject.Parse(File.ReadAllText(Path.Combine(ConnectorDirectory, "examples", "ProductSummary.data.json")));

    [Fact]
    public async Task Example_template_populates_body_headers_footers_and_preserves_everything_else()
    {
        byte[] template = Fixture("examples", "ProductSummaryTemplate.docx");
        var (body, docx) = await Generate(template, ExampleData());
        Assert.Empty((JArray)body["warnings"]!);

        XDocument document = Body(docx);
        List<string> paragraphs = Paragraphs(document);
        Assert.Equal("Product Summary — Exemple Pharma Inc.", paragraphs[0]);
        Assert.Equal("Prepared for Hôpital Sainte-Thérèse (Montréal) on 2026-10-11.", paragraphs[1]);
        Assert.Contains("This summary was prepared for Exemple Pharma Inc.; prices are indicative and exclude taxes.", paragraphs);
        Assert.DoesNotContain("Internal: margin review pending.", paragraphs);
        Assert.Contains("Notes: First line of the notes.\nSecond line — Unicode ✓ 東京 🚀", paragraphs);
        Assert.DoesNotContain(paragraphs, p => p.Contains('{') || p.Contains('}'));

        XElement table = document.Descendants(Wn + "tbl").First();
        List<List<string>> rows = TableText(table);
        Assert.Equal(5, rows.Count); // header, three products, total (discount row removed)
        Assert.Equal(new[] { "Café & Crème <Test> \"quoted\" l'été", "00000001", "12,50 $", "☒" }, rows[3]);
        Assert.Equal(new[] { "Total", "", "$33.25", "" }, rows[4]);

        Assert.Equal("CONFIDENTIAL — Exemple Pharma Inc.", AllText(Part(docx, "word/header1.xml")));
        Assert.Equal("Exemple Pharma Inc. | Product Summary | Approved ☒", AllText(Part(docx, "word/header2.xml")));
        Assert.Equal("Even page — Hôpital Sainte-Thérèse", AllText(Part(docx, "word/header3.xml")));
        Assert.Equal("Appendix — 2026-10-11", AllText(Part(docx, "word/header4.xml")));
        XDocument footer = Part(docx, "word/footer1.xml");
        Assert.Equal(new[] { new List<string> { "Contact", "Email" }, new List<string> { "A. Tremblay", "a.tremblay@example.com" } },
            TableText(footer.Descendants(Wn + "tbl").Single()));
        Assert.Contains(footer.Descendants(Wn + "instrText"), i => i.Value.Contains("PAGE"));
        Assert.Equal("Generated by Power Automate", AllText(Part(docx, "word/footer2.xml")));

        JObject stats = (JObject)body["statistics"]!;
        Assert.Equal(3 + 1, (int)stats["tableRowsCreated"]!);
        Assert.Equal(7, (int)stats["storyPartsProcessed"]!);
        Assert.Equal(2 + 1 + 3, (int)stats["checkboxesUpdated"]!);

        // Untouched parts are byte-for-byte identical and the entry list is unchanged.
        Assert.Equal(EntryNames(template), EntryNames(docx));
        foreach (string part in new[] { "word/styles.xml", "word/numbering.xml", "word/settings.xml", "word/media/image1.png", "word/_rels/document.xml.rels", "[Content_Types].xml", "docProps/core.xml" })
            Assert.Equal(RawPart(template, part), RawPart(docx, part));

        // Sections, header/footer references, the drawing and list numbering survive.
        XDocument original = Body(template);
        Assert.Equal(original.Descendants(Wn + "sectPr").Select(s => s.ToString()), document.Descendants(Wn + "sectPr").Select(s => s.ToString()));
        Assert.Equal(original.Descendants(Wn + "drawing").Single().ToString(), document.Descendants(Wn + "drawing").Single().ToString());
        Assert.Equal(2, document.Descendants(Wn + "numPr").Count());
    }

    [Fact]
    public async Task Example_template_only_reuses_existing_formatting_properties()
    {
        byte[] template = Fixture("examples", "ProductSummaryTemplate.docx");
        var (_, docx) = await Generate(template, ExampleData());
        foreach (string part in new[] { "word/document.xml", "word/header1.xml", "word/header2.xml", "word/footer1.xml" })
        {
            HashSet<string> before = FormattingSignatures(Part(template, part));
            HashSet<string> after = FormattingSignatures(Part(docx, part));
            Assert.Empty(after.Except(before));
        }
    }

    private static HashSet<string> FormattingSignatures(XDocument xml) =>
        xml.Descendants().Where(e => e.Name.Namespace == Wn && new[] { "pPr", "rPr", "tblPr", "trPr", "tcPr", "sectPr", "tblGrid" }.Contains(e.Name.LocalName))
            .Select(e => e.ToString(SaveOptions.DisableFormatting)).ToHashSet();

    [Fact]
    public async Task First_even_and_section_specific_headers_are_processed_once_each()
    {
        var options = new Options();
        options.Headers["header-first.xml"] = Text("First {A}");
        options.Headers["header.xml"] = Text("Default {A}");
        options.Headers["header-even.xml"] = Text("Even {A}");
        options.Footers["footer-first.xml"] = Text("First footer {A}");
        options.Footers["footer.xml"] = Table(1, Row(Cell(Text("{#Rows}{V}{/Rows}"))));
        // A second section reuses the default header (linked) through the same relationship.
        string secondSection = "<w:p><w:pPr><w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdH0\"/><w:pgSz w:w=\"12240\" w:h=\"15840\"/></w:sectPr></w:pPr><w:r><w:t>Section one {A}</w:t></w:r></w:p>";
        byte[] template = Build(secondSection + Text("Section two"), options);
        ConnectorInvocationResult list = await Invoke("ListPlaceholders", Request(template));
        JArray parts = (JArray)JObject.Parse(list.BodyText)["storyParts"]!;
        Assert.Equal(6, parts.Count);
        var (body, docx) = await Generate(template, new { A = "x", Rows = new[] { new { V = "1" }, new { V = "2" } } });
        Assert.Equal("First x", AllText(Part(docx, "word/header-first.xml")));
        Assert.Equal("Default x", AllText(Part(docx, "word/header.xml")));
        Assert.Equal("Even x", AllText(Part(docx, "word/header-even.xml")));
        Assert.Equal("First footer x", AllText(Part(docx, "word/footer-first.xml")));
        Assert.Equal(new[] { "1", "2" }, TableText(Part(docx, "word/footer.xml").Descendants(Wn + "tbl").Single()).Select(r => r[0]));
        Assert.Equal(6, (int)body["statistics"]!["storyPartsProcessed"]!);
    }

    [Fact]
    public async Task Orphan_header_entries_are_not_processed()
    {
        var options = new Options();
        options.ExtraEntries.Add(("word/header9.xml", Encoding.UTF8.GetBytes("<w:hdr xmlns:w=\"" + W + "\"><w:p><w:r><w:t>{Missing}</w:t></w:r></w:p></w:hdr>")));
        byte[] template = Build(Text("Body"), options);
        var (_, docx) = await Generate(template, new { });
        Assert.Equal(RawPart(template, "word/header9.xml"), RawPart(docx, "word/header9.xml"));
    }

    [Fact]
    public async Task Text_boxes_and_footnotes_with_tags_are_reported_not_silently_left()
    {
        string textBox = "<w:r><w:pict><v:shape xmlns:v=\"urn:schemas-microsoft-com:vml\"><v:textbox><w:txbxContent><w:p><w:r><w:t>{Name}</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r>";
        await Fail(Doc(P(textBox)), new { Name = "x" }, 422, "UNSUPPORTED_TEMPLATE_STRUCTURE");
    }

    [Fact]
    public async Task Tags_in_content_control_placeholder_text_are_rejected()
    {
        string control = "<w:sdt><w:sdtPr><w:id w:val=\"3\"/><w:showingPlcHdr/></w:sdtPr><w:sdtContent><w:r><w:rPr><w:rStyle w:val=\"PlaceholderText\"/></w:rPr><w:t>{Name}</w:t></w:r></w:sdtContent></w:sdt>";
        await Fail(Doc(P(control)), new { Name = "x" }, 422, "UNSUPPORTED_TEMPLATE_STRUCTURE");
    }

    [Fact]
    public async Task Tracked_changes_produce_a_warning_and_are_kept()
    {
        string paragraph = P(R("Hello ") + "<w:ins w:id=\"1\" w:author=\"a\" w:date=\"2024-01-01T00:00:00Z\"><w:r><w:t>{Name}</w:t></w:r></w:ins>");
        var (body, docx) = await Generate(Doc(paragraph), new { Name = "Ada" });
        Assert.Contains((JArray)body["warnings"]!, w => (string)w["code"]! == "TRACKED_CHANGES");
        Assert.Equal("Ada", Body(docx).Descendants(Wn + "ins").Single().Value);
    }

    // ------------------------------------------------------------------
    // Independent generators (python-docx and LibreOffice)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("independent-python-docx.docx")]
    [InlineData("independent-libreoffice.docx")]
    public async Task Independently_generated_templates_render(string fixture)
    {
        byte[] template = Fixture("fixtures", fixture);
        JObject data = JObject.Parse(File.ReadAllText(Path.Combine(ConnectorDirectory, "fixtures", "independent.data.json")));
        var (_, docx) = await Generate(template, data);
        List<string> paragraphs = Paragraphs(Body(docx));
        Assert.Contains("Order SO-1001", paragraphs);
        Assert.Contains("Customer: Société Générale Ltée", paragraphs);
        Assert.Contains("Terms apply to Société Générale Ltée.", paragraphs);
        Assert.DoesNotContain(paragraphs, p => p.Contains('{'));
        List<List<string>> rows = TableText(Body(docx).Descendants(Wn + "tbl").Single());
        Assert.Equal(new[] { "Widget", "2", "$4.00" }, rows[1]);
        Assert.Equal(new[] { "Gadget", "1", "$9.99" }, rows[2]);
        string header = string.Concat(EntryNames(docx).Where(n => n.StartsWith("word/header")).Select(n => AllText(Part(docx, n))));
        Assert.Contains("Header for SO-1001", header);
    }

    // ------------------------------------------------------------------
    // Package safety
    // ------------------------------------------------------------------

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, data) in entries)
            {
                using Stream s = zip.CreateEntry(name).Open();
                s.Write(data, 0, data.Length);
            }
        }
        return stream.ToArray();
    }

    [Fact]
    public async Task Rejects_bad_base64_and_non_docx_inputs()
    {
        ConnectorInvocationResult badBase64 = await InvokeRaw("GenerateDocument", "{\"templateBase64\":\"not base64!!\",\"dataJson\":\"{}\"}");
        Assert.Equal(400, badBase64.StatusCode);
        Assert.Equal("INVALID_BASE64", (string)JObject.Parse(badBase64.BodyText)["error"]!["code"]!);
        await Fail(Encoding.UTF8.GetBytes("plain text, not a zip"), new { }, 422, "INVALID_DOCX");
        await Fail(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0 }, new { }, 422, "INVALID_DOCX");
        await Fail(Zip(("hello.txt", Encoding.UTF8.GetBytes("hi"))), new { }, 422, "INVALID_DOCX");
        await Fail(Build("", new Options { OmitDocument = true }), new { }, 422, "INVALID_DOCX");
        byte[] truncated = Doc(Text("x"));
        await Fail(truncated.Take(truncated.Length / 2).ToArray(), new { }, 422, "INVALID_DOCX");
    }

    [Fact]
    public async Task Rejects_malformed_xml_and_dtds()
    {
        await Fail(Build("", new Options { RawDocumentXml = "<w:document xmlns:w=\"" + W + "\"><w:body><w:p>" }), new { }, 422, "INVALID_DOCX");
        string dtd = "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY e \"boom\">]><w:document xmlns:w=\"" + W + "\"><w:body><w:p><w:r><w:t>&e;</w:t></w:r></w:p></w:body></w:document>";
        await Fail(Build("", new Options { RawDocumentXml = dtd }), new { }, 422, "INVALID_DOCX");
    }

    [Theory]
    [InlineData("../evil.xml")]
    [InlineData("/absolute.xml")]
    [InlineData("word/../../evil.xml")]
    [InlineData("C:/evil.xml")]
    [InlineData("word\\evil.xml")]
    public async Task Rejects_unsafe_entry_paths(string name)
    {
        var options = new Options();
        options.ExtraEntries.Add((name, new byte[] { 1 }));
        await Fail(Build(Text("x"), options), new { }, 422, "UNSAFE_PACKAGE");
    }

    [Fact]
    public async Task Rejects_duplicate_entries_zip_bombs_and_encrypted_entries()
    {
        var duplicate = new Options();
        duplicate.ExtraEntries.Add(("word/document.xml", Encoding.UTF8.GetBytes("<x/>")));
        await Fail(Build(Text("x"), duplicate), new { }, 422, "UNSAFE_PACKAGE");

        var bomb = new Options();
        bomb.ExtraEntries.Add(("word/media/bomb.bin", new byte[4 * 1024 * 1024]));
        await Fail(Build(Text("x"), bomb), new { }, 422, "UNSAFE_PACKAGE");

        byte[] encrypted = Doc(Text("x"));
        // Set general-purpose bit 0 (encrypted) on the first central directory header.
        int central = FindSignature(encrypted, 0x02014b50);
        encrypted[central + 8] |= 1;
        await Fail(encrypted, new { }, 422, "UNSAFE_PACKAGE");
    }

    private static int FindSignature(byte[] data, uint signature)
    {
        for (int i = 0; i + 4 <= data.Length; i++)
            if (BitConverter.ToUInt32(data, i) == signature) return i;
        throw new InvalidOperationException();
    }

    [Fact]
    public async Task Rejects_oversized_templates_and_macro_documents()
    {
        var big = new Options();
        var random = new Random(1);
        byte[] noise = new byte[5 * 1024 * 1024 + 10];
        random.NextBytes(noise);
        big.ExtraEntries.Add(("word/media/noise.bin", noise));
        await Fail(Build(Text("x"), big), new { }, 413, "RESOURCE_LIMIT_EXCEEDED");

        await Fail(Build(Text("x"), new Options { MainContentType = "application/vnd.ms-word.document.macroEnabled.main+xml" }), new { }, 422, "INVALID_DOCX");
        var vba = new Options();
        vba.ExtraEntries.Add(("word/vbaProject.bin", new byte[] { 1, 2, 3 }));
        await Fail(Build(Text("x"), vba), new { }, 422, "INVALID_DOCX");
    }

    [Fact]
    public async Task Dotx_templates_produce_documents()
    {
        byte[] template = Build(Text("{A}"), new Options { MainContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml" });
        ConnectorInvocationResult result = await Invoke("GenerateDocument", Request(template, new { A = "ok" }));
        Assert.Equal(200, result.StatusCode);
        byte[] docx = Convert.FromBase64String((string)JObject.Parse(result.BodyText)["fileBase64"]!);
        Assert.Contains("wordprocessingml.document.main+xml", Encoding.UTF8.GetString(RawPart(docx, "[Content_Types].xml")));
        Assert.Empty(ValidationErrors(docx));
    }

    [Fact]
    public async Task Errors_never_echo_document_or_data_content()
    {
        const string secret = "SECRET-CUSTOMER-VALUE";
        ConnectorInvocationResult result = await Invoke("GenerateDocument", Request(Doc(Text("{" + "Name}"), Text(secret + " {Missing}")), new { Name = secret }));
        Assert.Equal(422, result.StatusCode);
        Assert.DoesNotContain(secret, result.BodyText);
    }

    // ------------------------------------------------------------------
    // API contract
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("GenerateDocument")]
    [InlineData("ValidateTemplate")]
    [InlineData("ListPlaceholders")]
    public async Task Base64_encoded_operation_ids_are_routed(string operation)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(operation));
        ConnectorInvocationResult result = await Invoke(encoded, Request(Doc(Text("{A}")), new { A = "x" }));
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Unknown_operations_and_bad_requests_are_explicit()
    {
        ConnectorInvocationResult unknown = await Invoke("DeleteEverything", new JObject());
        Assert.Equal(400, unknown.StatusCode);
        Assert.Equal("UNKNOWN_OPERATION", (string)JObject.Parse(unknown.BodyText)["error"]!["code"]!);

        Assert.Equal("INVALID_REQUEST", (string)JObject.Parse((await InvokeRaw("GenerateDocument", "[1]")).BodyText)["error"]!["code"]!);
        Assert.Equal("INVALID_REQUEST", (string)JObject.Parse((await InvokeRaw("GenerateDocument", "{\"dataJson\":\"{}\"}")).BodyText)["error"]!["code"]!);
        Assert.Equal("INVALID_REQUEST", (string)JObject.Parse((await Invoke("GenerateDocument", Request(Doc(Text("x"))))).BodyText)["error"]!["code"]!);
        Assert.Equal("INVALID_REQUEST", (string)JObject.Parse((await Invoke("GenerateDocument", new JObject { ["templateBase64"] = "AA==", ["dataJson"] = "{}", ["strictMode"] = "yes" })).BodyText)["error"]!["code"]!);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("")]
    [InlineData("{\"a\":1} trailing")]
    public async Task Invalid_data_json_is_rejected(string data)
    {
        ConnectorInvocationResult result = await Invoke("GenerateDocument", new JObject { ["templateBase64"] = Convert.ToBase64String(Doc(Text("x"))), ["dataJson"] = data });
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("INVALID_DATA_JSON", (string)JObject.Parse(result.BodyText)["error"]!["code"]!);
    }

    [Fact]
    public async Task Accepts_file_content_objects_data_urls_and_object_data()
    {
        byte[] template = Doc(Text("{A}"));
        string base64 = Convert.ToBase64String(template);
        var fileContent = new JObject { ["templateBase64"] = new JObject { ["$content-type"] = "application/octet-stream", ["$content"] = base64 }, ["dataJson"] = "{\"A\":\"1\"}" };
        Assert.Equal(200, (await Invoke("GenerateDocument", fileContent)).StatusCode);
        var dataUrl = new JObject { ["templateBase64"] = "data:application/vnd.openxmlformats-officedocument.wordprocessingml.document;base64," + base64, ["dataJson"] = new JObject { ["A"] = "1" } };
        Assert.Equal(200, (await Invoke("GenerateDocument", dataUrl)).StatusCode);
    }

    [Theory]
    [InlineData("Report", "Report.docx")]
    [InlineData("Report.DOCX", "Report.docx")]
    [InlineData("../../etc/passwd", "passwd.docx")]
    [InlineData("a:b*c?.docx", "a_b_c_.docx")]
    [InlineData("Summary.doc", "Summary.docx")]
    [InlineData("CON", "_CON.docx")]
    [InlineData("  ", "Document.docx")]
    public async Task File_names_are_sanitized(string input, string expected)
    {
        ConnectorInvocationResult result = await Invoke("GenerateDocument", Request(Doc(Text("x")), new { }, fileName: input));
        JObject body = JObject.Parse(result.BodyText);
        Assert.Equal(expected, (string)body["fileName"]!);
    }

    [Fact]
    public async Task Strict_mode_rejects_missing_values_and_permissive_mode_warns()
    {
        byte[] template = Doc(Text("A{Missing}B"), Text("{Present}"));
        JObject error = await Fail(template, new { Present = "p" }, 422, "MISSING_FIELD");
        Assert.Equal("Missing", (string)error["field"]!);
        Assert.Equal("paragraph 1", (string)error["location"]!);
        await Fail(template, JObject.Parse("{\"Missing\":null,\"Present\":\"p\"}"), 422, "MISSING_FIELD");

        var (body, docx) = await Generate(template, new { Present = "p" }, strict: false);
        Assert.Equal(new[] { "AB", "p" }, Paragraphs(Body(docx)));
        Assert.Equal("MISSING_FIELD", (string)body["warnings"]![0]!["code"]!);
    }

    [Fact]
    public async Task Structural_errors_stay_fatal_in_permissive_mode()
    {
        await Fail(Doc(Text("{#A}")), new { A = true }, 422, "UNBALANCED_MARKER", strict: false);
        await Fail(Doc(Text("{Obj}")), new { Obj = new { X = 1 } }, 422, "TYPE_MISMATCH", strict: false);
    }

    [Fact]
    public async Task Multiple_errors_are_reported_together()
    {
        JObject error = await Fail(Doc(Text("{A}"), Text("{B}"), Text("{C}")), new { }, 422, "MISSING_FIELD");
        Assert.Equal(2, ((JArray)error["relatedErrors"]!).Count);
    }

    [Fact]
    public async Task Validate_template_reports_structure_and_data_problems_without_a_file()
    {
        byte[] template = Fixture("examples", "ProductSummaryTemplate.docx");
        JObject structural = JObject.Parse((await Invoke("ValidateTemplate", Request(template))).BodyText);
        Assert.True((bool)structural["valid"]!);
        Assert.False((bool)structural["dataValidated"]!);
        Assert.Null(structural["fileBase64"]);
        Assert.Contains((JArray)structural["fields"]!, f => (string)f["dataPath"]! == "Products[].Name");

        JObject data = ExampleData();
        JObject withData = JObject.Parse((await Invoke("ValidateTemplate", Request(template, data))).BodyText);
        Assert.True((bool)withData["valid"]!, withData.ToString());
        Assert.True((bool)withData["dataValidated"]!);
        Assert.Equal(4, (int)withData["statistics"]!["tableRowsCreated"]!);

        data.Remove("CompanyName");
        data["Products"]![0]!["IsCovered"] = "yes";
        JObject invalid = JObject.Parse((await Invoke("ValidateTemplate", Request(template, data))).BodyText);
        Assert.False((bool)invalid["valid"]!);
        JArray errors = (JArray)invalid["errors"]!;
        Assert.Contains(errors, e => (string)e["code"]! == "MISSING_FIELD" && (string)e["field"]! == "CompanyName" && (string)e["part"]! == "word/header1.xml");
        Assert.Contains(errors, e => (string)e["code"]! == "TYPE_MISMATCH" && (string)e["field"]! == "IsCovered");

        JObject broken = JObject.Parse((await Invoke("ValidateTemplate", new JObject { ["templateBase64"] = "%%%" })).BodyText);
        Assert.False((bool)broken["valid"]!);
        Assert.Equal("INVALID_BASE64", (string)broken["errors"]![0]!["code"]!);

        JObject unbalanced = JObject.Parse((await Invoke("ValidateTemplate", Request(Doc(Text("{#A}"), Text("{Name}"))))).BodyText);
        Assert.False((bool)unbalanced["valid"]!);
        Assert.Equal("UNBALANCED_MARKER", (string)unbalanced["errors"]![0]!["code"]!);
    }

    [Fact]
    public async Task List_placeholders_inventories_fields_with_types_scopes_and_sample_data()
    {
        byte[] template = Fixture("examples", "ProductSummaryTemplate.docx");
        ConnectorInvocationResult result = await Invoke("ListPlaceholders", Request(template));
        Assert.Equal(200, result.StatusCode);
        JObject body = JObject.Parse(result.BodyText);
        JArray fields = (JArray)body["fields"]!;
        JToken Field(string type, string path) => fields.Single(f => (string)f["type"]! == type && (string)f["dataPath"]! == path);

        Assert.Equal(5, (int)Field("text", "CompanyName")["occurrences"]!);
        Assert.Equal("array", (string)Field("tableRow", "Products")["likelyType"]!);
        Assert.Equal("boolean", (string)Field("tableRow", "ShowDiscountRow")["likelyType"]!);
        Assert.Equal("Products[]", (string)Field("text", "Products[].Name")["scope"]!);
        Assert.Equal("Products[]", (string)Field("checkbox", "Products[].IsCovered")["scope"]!);
        Assert.Equal(new JArray("boolean"), Field("condition", "ShowDisclaimer")["acceptedValues"]);
        Assert.Contains(((JArray)Field("text", "CompanyName")["locations"]!), l => (string)l["part"]! == "word/header1.xml");
        Assert.Equal("table 1, row 2, cell 1, paragraph 1", (string)Field("text", "Products[].Name")["locations"]![0]!["location"]!);
        Assert.Equal(7, ((JArray)body["storyParts"]!).Count);

        JObject sample = JObject.Parse((string)body["sampleDataJson"]!);
        Assert.Equal(JTokenType.Array, sample["Products"]!.Type);
        Assert.Equal(JTokenType.Boolean, sample["Products"]![0]!["IsCovered"]!.Type);
        Assert.Equal(JTokenType.Boolean, sample["ShowDiscountRow"]!.Type);
        Assert.Equal("", (string)sample["Customer"]!["Name"]!);
        Assert.Equal("", (string)sample["DiscountFormatted"]!);
        Assert.Contains(fields, f => (string)f["dataPath"]! == "DiscountFormatted" && (string)f["scope"]! == "");

        // The sample payload is itself a valid (if empty) data set for the template.
        ConnectorInvocationResult filled = await Invoke("GenerateDocument", Request(template, (string)body["sampleDataJson"]!));
        Assert.Equal(200, filled.StatusCode);
    }

    [Fact]
    public async Task List_placeholders_reports_package_errors_with_http_status()
    {
        ConnectorInvocationResult result = await Invoke("ListPlaceholders", new JObject { ["templateBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("nope")) });
        Assert.Equal(422, result.StatusCode);
    }

    [Fact]
    public void Script_is_backendless_and_within_platform_limits()
    {
        string script = File.ReadAllText(Path.Combine(ConnectorDirectory, "script.csx"));
        Assert.True(Encoding.UTF8.GetByteCount(script) < 1_000_000);
        Assert.DoesNotContain("SendAsync", script);
        Assert.DoesNotContain("HttpClient", script);
        Assert.DoesNotContain("File.", script);
        Assert.DoesNotContain("System.Reflection", script);
        Assert.Contains("public class Script : ScriptBase", script);
        JObject swagger = JObject.Parse(File.ReadAllText(Path.Combine(ConnectorDirectory, "apiDefinition.swagger.json")));
        string[] operations = swagger["paths"]!.Children<JProperty>().Select(p => (string)p.Value["post"]!["operationId"]!).ToArray();
        Assert.Equal(new[] { "GenerateDocument", "ValidateTemplate", "ListPlaceholders" }, operations);
        foreach (JProperty definition in ((JObject)swagger["definitions"]!).Properties())
            Assert.NotNull(definition.Value["type"]);
    }

    /// <summary>
    /// Set DOCX_REVIEW_OUTPUT to a directory to write generated samples for manual
    /// review in desktop Word (repair prompts, checkbox toggling, visual layout).
    /// </summary>
    [Fact]
    public async Task Writes_review_samples_when_requested()
    {
        string? directory = Environment.GetEnvironmentVariable("DOCX_REVIEW_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var (_, example) = await Generate(Fixture("examples", "ProductSummaryTemplate.docx"), ExampleData());
        File.WriteAllBytes(Path.Combine(directory, "ProductSummary.generated.docx"), example);
        JObject independent = JObject.Parse(File.ReadAllText(Path.Combine(ConnectorDirectory, "fixtures", "independent.data.json")));
        var (_, docx) = await Generate(Fixture("fixtures", "independent-python-docx.docx"), independent);
        File.WriteAllBytes(Path.Combine(directory, "Independent.generated.docx"), docx);
        var (_, rows) = await Generate(ProductTable(), Products(25));
        File.WriteAllBytes(Path.Combine(directory, "Rows25.generated.docx"), rows);
    }

    // ------------------------------------------------------------------
    // Performance (local measurements; tenant timings must be re-measured)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Performance_common_large_and_long_text_documents()
    {
        byte[] example = Fixture("examples", "ProductSummaryTemplate.docx");
        await Generate(example, ExampleData()); // warm-up compiles the script
        var timer = Stopwatch.StartNew();
        await Generate(example, ExampleData());
        long common = timer.ElapsedMilliseconds;

        timer.Restart();
        var (_, large) = await Generate(ProductTable(), Products(1000));
        long thousandRows = timer.ElapsedMilliseconds;

        timer.Restart();
        await Generate(Doc(Enumerable.Range(0, 200).Select(i => Text("Paragraph " + i + ": {Long}")).ToArray()), new { Long = new string('a', 32000) });
        long longText = timer.ElapsedMilliseconds;

        _output.WriteLine($"common example: {common} ms; 1,000 rows: {thousandRows} ms ({large.Length:N0} bytes); 200 x 32,000-char values: {longText} ms");
        Assert.True(common < 5000, "Common document exceeded 5 seconds: " + common);
        Assert.True(thousandRows < 30000, "1,000-row document exceeded 30 seconds: " + thousandRows);
    }
}
