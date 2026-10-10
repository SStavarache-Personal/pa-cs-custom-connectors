# HTML Slide Converter

Generate a single `.pptx` from completed HTML containing explicitly positioned slide sections. Text becomes editable text boxes, tables become native editable tables, and charts supplied as PNG/JPEG/SVG become embedded graphics. All data and display strings are supplied in the HTML; there are no dataset mappings or PowerPoint templates to upload.

The converter runs entirely in the Power Automate C# custom-code sandbox. It does not call an API, fetch external assets, start a browser, launch Office, or depend on a runtime NuGet package. This is a compiler for the fixed-layout profile below, not a general HTML/CSS browser renderer.

## Actions

| Operation | Purpose |
| --- | --- |
| `ConvertHtmlToPptx` | Validate completed HTML and return a base64 `.pptx`, slide inventory and diagnostics. |
| `ValidateHtmlSlides` | Run the same HTML, layout, table and asset checks; return inventory/diagnostics without packaging a file. |

Both actions accept:

```json
{
  "html": "<section class='slide'><p style='left:40px;top:40px;width:800px;height:80px;font-size:32px'>Completed report</p></section>",
  "fileName": "Report.pptx",
  "strict": true
}
```

Only `html` is required. `fileName` defaults to `Presentation.pptx`; the extension is appended when absent. `strict` defaults to `true`.

Example success response (file content abbreviated):

```json
{
  "fileName": "Report.pptx",
  "contentType": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
  "fileContent": "UEsDB...",
  "slideCount": 1,
  "slideWidthPx": 1280,
  "slideHeightPx": 720,
  "objectCount": 1,
  "cellCount": 0,
  "warnings": [],
  "slides": [{"index": 1, "name": "Slide 1", "textCount": 1, "tableCount": 0, "imageCount": 0}],
  "elapsedMilliseconds": 20
}
```

`ValidateHtmlSlides` returns the same inventory fields plus `valid: true`, but omits `fileName`, `contentType` and `fileContent`. A failed validation returns a non-2xx structured error, not a partially generated deck.

In a flow, pass the returned `fileName` to **Create file → File Name**. For **File Content**, explicitly decode the returned base64 string:

```text
base64ToBinary(body('Convert_HTML_slides_to_a_presentation')?['fileContent'])
```

Use your actual action name in that expression. Avoid applying `base64()` again or saving the base64 characters as the file.

## Slide layout profile

- One `<section class="slide">` creates one slide; source order determines deck order.
- Concatenate multiple sections in one HTML string. Optional `<!doctype html>`, `html`, `head`, and `body` wrappers are supported.
- Slide sections default to `width:1280px;height:720px` (13⅓ × 7½ inches at 96 DPI). Explicit dimensions may range from 96 to 5,376 pixels. Every slide must have the same dimensions.
- Each positioned element requires `left`, `top`, `width`, and `height`. Elements must fit within the slide canvas; automatic pagination and resizing are not performed.
- Lengths support unitless pixels, `px`, `pt`, `in`, `cm`, and `mm`. Percentages are limited to table column widths and `line-height`.
- Nested positioned `div` containers add their own `left`/`top` offsets to children. Coordinates are relative to the enclosing container's outer top-left; container padding does not offset child boxes. A container background is a separate native shape. Source order is drawing order.
- Non-void elements must have explicit closing tags. Common void tags such as `img`, `br`, and `col` do not require closing tags. Quoted/unquoted attributes, comments, and HTML entities are accepted. Use properly closed markup rather than relying on browser error recovery.
- `id` or `data-name` gives an object its PowerPoint shape name; section `data-name`/`id` gives the slide its name.

### Text

Positioned `div`, `p`, `span`, and `h1`–`h6` elements become text boxes. Inline `span`, `b`/`strong`, `i`/`em`, `u`, and `br` produce editable rich text. Nested `p`, `div`, and heading elements create paragraphs. Headings do not receive implicit browser font sizes/margins; supply the required font size.

Use explicitly sized boxes and installed fonts. Text remains editable; PowerPoint performs final glyph layout and wrapping, which can differ from a browser. No browser font measurement, text-overflow detection, automatic font shrinking, font embedding, or `@font-face` loading is performed. Input authors must size boxes to fit their content.

### Tables

`table`, `thead`, `tbody`, `tfoot`, `tr`, `th`, `td`, `colgroup`, and `col` are supported. Tables retain editability and may contain horizontally/vertically merged cells using positive `colspan`/`rowspan`. The grid must be complete and rectangular after accounting for spans. Spans cannot overlap or extend beyond the table.

The HTML defines the whole populated table, so adding/removing company rows is simply adding/removing `tr` elements before calling the connector. Fixed headers, variable body rows and merged summary rows can coexist in one table. The converter does not need row prototypes or a dataset schema.

- Without `colgroup`, grid columns share the explicit table width equally.
- With `colgroup`, supply exactly one `col` per grid column. Explicit pixel or percentage widths must sum to the table width/100%. `col span` is not supported.
- Explicit row heights consume part of the table's height; unspecified rows share the remainder equally. When all row heights are explicit, they must sum to the table height.
- Cell width/height cannot override the grid. Column widths belong on `col`, and row heights belong on `tr`.
- Remove hidden rows/cells/groups from the supplied HTML and update its spans; `display:none` is not supported inside a table grid.
- Table/row fills, borders and padding supply cell defaults. Explicit cell styles override those defaults. `th` defaults to bold. CSS border conflict resolution is not a browser algorithm; shared edges should use consistent border declarations.

Example:

```html
<section class="slide" style="width:1280px;height:720px">
  <table style="left:40px;top:160px;width:1200px;height:300px;
                font-family:Arial;font-size:18px;border:1px solid #000000;
                padding:6px;vertical-align:middle">
    <colgroup>
      <col style="width:20%">
      <col style="width:20%">
      <col style="width:30%">
      <col style="width:30%">
    </colgroup>
    <thead>
      <tr style="height:60px;background:#0070c0;color:#ffffff">
        <th colspan="2">Company</th>
        <th>Region A</th>
        <th>Region B</th>
      </tr>
    </thead>
    <tbody>
      <tr><td colspan="2">Example Company</td><td>Listed</td><td>Pending</td></tr>
      <tr><td colspan="2">Another Company</td><td>Listed</td><td>Listed</td></tr>
      <tr><td colspan="2"><b>Summary</b></td><td>$0.25</td><td>$0.30</td></tr>
    </tbody>
  </table>
</section>
```

### Images and chart graphics

- Use `img src="data:image/png;base64,..."` or `data:image/jpeg;base64,...` with explicit geometry.
- Static SVG can be supplied inline, through `img src="data:image/svg+xml;base64,..."`, or as a UTF-8 URL-encoded SVG data URL. Accepted URL-encoded prefixes are `data:image/svg+xml,`, `data:image/svg+xml;utf8,`, and `data:image/svg+xml;charset=utf-8,` (case insensitive). SVG is embedded as a vector graphic using the Office SVG extension.
- URL-encoded payloads are percent-decoded once as UTF-8. Literal `+` stays `+`; malformed percent escapes and invalid UTF-8 are rejected. Base64 remains required for PNG/JPEG.
- Supply a `viewBox` and use self-contained SVG presentation attributes/inline styling. SVG must be well-formed XML. Local `#fragment` references are accepted; external references, scripts, animations, SVG stylesheet blocks, and `foreignObject` are rejected.
- For inline SVG, root `style` defines slide geometry using the same supported CSS profile. SVG children keep their own presentation attributes. Include the SVG namespace where possible.
- A chart graphic has no editable Excel dataset or native chart series. It can be moved/resized as a picture. Tables and text remain native editable objects.
- Images stretch into their declared box. Preserve the source aspect ratio in the supplied width/height.
- Optional `data-fallback-src="data:image/png;base64,..."` on SVG/`img` supplies a raster preview for older/non-SVG Office clients. Without it, the converter emits `SVG_FALLBACK_MISSING` and uses a transparent raster fallback. A compatible Office client renders the embedded SVG; older clients may show an empty picture. The connector does not rasterize SVG.
- No external image/font requests are made. Embedded data URLs make the HTML self-contained.

In Power Apps, construct the SVG source with Power Fx `EncodeUrl` applied to the SVG markup only:

```powerfx
With(
    { svg: "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><rect width='100' height='100' fill='#0070c0'/></svg>" },
    "<section class='slide'><img style='left:40px;top:40px;width:300px;height:300px' src='data:image/svg+xml;utf8," &
    EncodeUrl(svg) &
    "'></section>"
)
```

Pass the resulting completed HTML string in the connector's `html` input. Encode the SVG payload once; leave the data URL prefix and surrounding HTML unencoded. The same SVG validation and raster fallback rules apply to every SVG input format. See Microsoft's [Power Fx EncodeUrl reference](https://learn.microsoft.com/en-us/power-platform/power-fx/reference/function-encode-decode).

## CSS support

Inline `style` and `style` blocks are supported. Stylesheets accept tag, `.class`, `#id`, `tag.class`, and `tag#id` selectors, including comma-separated lists. Specificity and source order select declarations; inline styles win. Font/text properties inherit. Descendant selectors, pseudo-selectors, `!important`, media rules and external stylesheets are not supported.

| CSS | Supported values |
| --- | --- |
| `position` | `absolute`, `relative` (both use explicit geometry) |
| `left`, `top`, `width`, `height` | Explicit lengths |
| `background`, `background-color`, `color` | `#RGB`, `#RRGGBB`, `rgb(r,g,b)`, documented named colours; transparent fills |
| `font-family` | First named font in the supplied list |
| `font-size` | Explicit lengths corresponding to 1–400 points |
| `font-weight` | `normal`, `bold`, `400`, `700` |
| `font-style` | `normal`, `italic` |
| `text-decoration` | `none`, `underline` |
| `text-align` | `left`, `center`, `right`, `justify` |
| `vertical-align` | `top`, `middle`, `bottom` |
| `white-space` | `normal`, `pre-wrap` |
| `line-height` | Positive length, percentage, or multiplier |
| `padding` and four side variants | Nonnegative explicit lengths; shorthand accepts 1–4 values |
| `border` and four side variants on cells | `<width> solid\|dashed\|dotted <colour>`, `none`, `0` |
| `border-collapse`, `table-layout` | `collapse`, `fixed` respectively |
| `display` | `block`, `none` |

Named colours: black, white, red, green, blue, gray/grey, yellow, navy, silver. Text colour cannot be transparent. Shapes support a uniform `border`; per-side borders are for table cells.

Formatting is checked in context: inline rich text cannot define its own box/background/padding, image boxes cannot define cell/shape borders or padding, and table row/column geometry must use the grid controls above. Position a containing shape behind an image when a background or border is needed. PNG/JPEG signatures are checked without fully decoding raster image data; supply valid image files.

Flexbox/Grid, transforms, gradients, shadows, rounded corners, automatic sizing, percentage geometry, nested tables, HTML lists, hyperlinks, scripts/canvas charts and browser-default margins are outside this profile. Use positioned text/shape boxes and supplied chart graphics instead.

## Validation and limits

Strict mode rejects unsupported properties, attributes and elements. `strict:false` ignores those unsupported declarations/elements with diagnostics. Invalid lengths, incomplete geometry, table spans, external assets and active content always fail. Warnings include a code, message, element/tag ID and one-based slide index (zero for document-level issues).

Example failure:

```json
{"error":{"code":"MISSING_GEOMETRY","message":"Positioned elements need left, top, width and height. Missing left","slideIndex":1,"location":"p#title","warnings":[]}}
```

| Limit | Value |
| --- | --- |
| HTML | 5 MiB UTF-8 |
| Slides | 100 |
| Positioned objects | 10,000 total |
| Table grid | 64 columns, 256 rows per table; 20,000 grid cells total |
| Embedded media | 20 MiB after decoding, deduplicated by SHA-256 |
| Generated `.pptx` | 50 MiB |
| HTML tokens / nesting | 30,000 / 64 levels |
| SVG elements / nesting | 20,000 / 64 levels per SVG |
| CSS selectors / diagnostics | 500 / 500 |
| Processing budget | 110 seconds, leaving margin below the platform's 120-second limit |

These are application safety caps, not a promise that every Power Automate request/response path accepts their maximum sizes. Base64 adds approximately one-third to binary payload size. Use smaller batches when your environment imposes tighter limits.

## Files, testing and deployment

- `apiDefinition.swagger.json`, `apiProperties.json`, `script.csx`, and this README are the deployable connector.
- `examples/completed-slides.html` is a synthetic two-slide example with text, merged tables and an SVG chart. No user-supplied template/content is included.
- `tests/*.json` are offline scenarios for the shared repository harness.
- `testing/tests/ConnectorTestKit.Tests/HtmlSlideConverterTests.cs` checks native editability, merged grids, source order, embedded assets, error paths and generated package validity using the Open XML SDK **only in tests**.
- `testing/HtmlSlideConverterCompile/` compiles the actual script against .NET Standard 2.0. No SDK dependency is deployed into the connector.

Run:

```powershell
dotnet build testing/HtmlSlideConverterCompile/HtmlSlideConverterCompile.csproj
dotnet test testing/tests/ConnectorTestKit.Tests/ConnectorTestKit.Tests.csproj
```

For deployment, use the repository deployment workflow or import the Swagger, enable custom code and select both operations. Choose **No authentication**. The placeholder host is never contacted by these scripted operations. Local tests do not replace a smoke test in the target Power Automate environment and an inspection in the Office versions used by recipients.
