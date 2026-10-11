# DOCX Template Generator

Fill a Word (`.docx`) template with JSON data from Power Automate or Power Apps. You supply the template file and a JSON object; you get back an editable `.docx` that keeps the template's layout, styles, tables, images, headers, footers and section settings.

Supported in V1:

- **Text tags** such as `{CompanyName}` and `{Customer.Name}`, including tags that Word has split across several runs.
- **Repeated table rows**: `{#Products}` … `{/Products}` across one formatted row. Arrays of any length work, including empty arrays.
- **Boolean conditions** that keep or remove whole paragraph blocks or table rows.
- **Checkbox content controls** (Developer → Check Box Content Control) set from Boolean values. They stay interactive and also work inside repeated rows.
- All of the above in the **body, headers and footers**, including first-page, even-page and section-specific variants.

Everything runs in the connector's C# custom code (`script.csx`). The package is rebuilt in memory with `System.IO.Compression` and `System.Xml.Linq`. The script calls no backend or document library, never writes to the file system, and makes no outbound request (it never calls `Context.SendAsync`).

| Guide | Contents |
| --- | --- |
| [Template authoring guide](docs/TEMPLATE_AUTHORING.md) | How to write tags, repeated rows, conditions and checkboxes in Word, plus the V1 limits |
| [Power Automate and Power Apps integration](docs/INTEGRATION.md) | SharePoint template retrieval, base64 handling, calling the actions, saving the result |
| [Deployment and operations](docs/DEPLOYMENT.md) | Importing, enabling code, licensing/DLP, rollout, rollback, troubleshooting |
| [Implementation notes](IMPLEMENTATION_NOTES.md) | Architecture decisions, verification status, known gaps |
| [Changelog](CHANGELOG.md) | Release history |

## Actions

| Operation ID | Summary | Success response |
| --- | --- | --- |
| `GenerateDocument` | Generate a Word document from a template | `fileName`, `mimeType`, `fileBase64`, `warnings`, `statistics` |
| `ValidateTemplate` | Validate a template, optionally against sample data | `valid`, `dataValidated`, `errors`, `warnings`, `fields`, `storyParts` |
| `ListPlaceholders` | List the tags in a template | `fields`, `storyParts`, `sampleDataJson`, `errors`, `warnings` |

All three actions read the template the same way. Validation and listing reuse exactly the discovery path that generation uses.

### GenerateDocument

```json
{
  "templateBase64": "<base64 .docx>",
  "dataJson": "{\"CompanyName\":\"Example Pharma Inc.\",\"IsApproved\":true,\"Products\":[{\"Name\":\"Atorvastatin\",\"DIN\":\"02456789\",\"PriceFormatted\":\"$12.50\"}]}",
  "fileName": "ProductSummary.docx",
  "strictMode": true
}
```

| Property | Required | Notes |
| --- | --- | --- |
| `templateBase64` | Yes | The `.docx` file content. Accepts a base64 string, a `data:…;base64,` URL, or a Power Automate file-content object (`{"$content-type": …, "$content": …}`). `.dotx` templates are accepted and produce a `.docx`. |
| `dataJson` | Yes | A JSON **object** serialized as text. A JSON object (not text) is also accepted from direct HTTP callers. |
| `fileName` | No | Unsafe characters (`<>:"/\|?*`, control characters, path parts, reserved Windows names) are replaced, and `.docx` is ensured. Defaults to `Document.docx`. A `FILE_NAME_SANITIZED` warning reports any change. |
| `strictMode` | No | Defaults to `true`; see [missing values](#missing-values-and-strict-mode). |

Success (HTTP 200):

```json
{
  "fileName": "ProductSummary.docx",
  "mimeType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  "fileBase64": "UEsDBBQAAAAIA…",
  "warnings": [],
  "statistics": {
    "textFieldsReplaced": 4,
    "tableRowsCreated": 1,
    "conditionalBlocksEvaluated": 0,
    "checkboxesUpdated": 1,
    "storyPartsProcessed": 3,
    "elapsedMilliseconds": 21
  }
}
```

To save the result, pass `base64ToBinary(body('Generate_document')?['fileBase64'])` (using your action's name) to SharePoint **Create file → File Content**. See [the integration guide](docs/INTEGRATION.md).

### ValidateTemplate

The request takes `templateBase64`, optional `dataJson` and optional `strictMode`.

- Without data, it checks the package and template structure.
- With data, it also renders the template in memory and reports every data problem (missing fields, type mismatches, limits) without returning a file.
- Template, package and data problems come back as HTTP 200 with `valid: false`. Only malformed requests (for example a missing `templateBase64`) return HTTP 400.

```json
{
  "valid": false,
  "dataValidated": true,
  "errors": [
    { "code": "MISSING_FIELD", "message": "No value was supplied for {CompanyName}.", "part": "word/header1.xml", "field": "CompanyName", "location": "paragraph 1" }
  ],
  "warnings": [],
  "fields": [ … same shape as ListPlaceholders … ],
  "storyParts": [ { "part": "word/header1.xml", "type": "header", "references": ["section 1 first header"] } ],
  "statistics": { … },
  "elapsedMilliseconds": 35
}
```

### ListPlaceholders

The request takes only `templateBase64`. The response lists each tag with its type, data path, scope and up to ten locations. It also returns `sampleDataJson`, a best-effort skeleton payload you can fill in.

```json
{
  "fields": [
    { "name": "CompanyName", "dataPath": "CompanyName", "type": "text", "acceptedValues": ["string","number","boolean"], "scope": "", "occurrences": 5,
      "locations": [ { "part": "word/document.xml", "location": "paragraph 1" }, { "part": "word/header1.xml", "location": "paragraph 1" } ] },
    { "name": "Products", "dataPath": "Products", "type": "tableRow", "acceptedValues": ["array","boolean"], "likelyType": "array", "scope": "", "occurrences": 1,
      "locations": [ { "part": "word/document.xml", "location": "table 1, row 2" } ] },
    { "name": "Name", "dataPath": "Products[].Name", "type": "text", "acceptedValues": ["string","number","boolean"], "scope": "Products[]", "occurrences": 1, "locations": [ … ] },
    { "name": "IsCovered", "dataPath": "Products[].IsCovered", "type": "checkbox", "acceptedValues": ["boolean"], "scope": "Products[]", "occurrences": 1, "locations": [ … ] }
  ],
  "storyParts": [ … ],
  "sampleDataJson": "{\"CompanyName\":\"\",\"Products\":[{\"Name\":\"\",\"IsCovered\":false}]}",
  "errors": [],
  "warnings": []
}
```

Without data, a table-row section could be an array or a Boolean. `likelyType` is a hint:

- Names such as `ShowX`, `IsX`, `HasX` or `IncludeX` are treated as Boolean rows, so their fields resolve in the enclosing scope.
- Any other row that uses item fields is treated as an array.

Use `ValidateTemplate` with real data for an exact answer. If the file cannot be read as a safe `.docx`, ListPlaceholders returns the HTTP error contract. Template-structure problems appear in `errors` alongside the inventory.

## Template syntax summary

| You write in Word | Data | Result |
| --- | --- | --- |
| `{CompanyName}`, `{Customer.Name}` | string, number or Boolean | Plain text; formatting comes from the tag's first character |
| `{#Products}` in one cell … `{/Products}` in another cell of the **same row** | array of objects | One copy of the row per item; zero items leave no data row |
| the same row markers | `true` / `false` | Keeps or removes the row |
| a paragraph containing only `{#ShowDisclaimer}` … a later paragraph containing only `{/ShowDisclaimer}` | `true` / `false` | Keeps or removes the paragraphs, tables and lists between them; the marker paragraphs are always removed |
| Check Box Content Control with **Tag** `IsApproved` | `true` / `false` | Updates checked state and symbol; remains interactive |
| `{Root.CompanyName}` inside a repeated row | any of the above | Reads top-level data instead of the current item |
| `{{` and `}}` | – | Literal `{` and `}` |

The exact grammar, scoping rules, number formatting and unsupported layouts are in the [template authoring guide](docs/TEMPLATE_AUTHORING.md). The complete example template is [`examples/ProductSummaryTemplate.docx`](examples/ProductSummaryTemplate.docx), with sample data in [`examples/ProductSummary.data.json`](examples/ProductSummary.data.json).

## Missing values and strict mode

| Situation | `strictMode: true` (default) | `strictMode: false` |
| --- | --- | --- |
| Text tag missing or `null` | `MISSING_FIELD` error | Empty text + `MISSING_FIELD` warning |
| Condition or row section missing or `null` | `MISSING_FIELD` error | Block/row removed + warning |
| Checkbox value missing or `null` | `MISSING_FIELD` error | Checkbox left unchanged + warning |
| Duplicate checkbox Tag in one scope | `DUPLICATE_CHECKBOX_TAG` error | Warning |
| Wrong type, malformed or unsupported template structure, unsafe package, limits | Error | Error |

Empty strings are always legal. Content inside a removed block or a false/empty row is never evaluated, so its fields may be absent.

## Errors

Errors use HTTP 400, 413, 422 or 500 and never include a partial file:

```json
{
  "error": {
    "code": "UNSUPPORTED_TEMPLATE_STRUCTURE",
    "message": "The Products section spans multiple table rows; V1 accepts a single template row. Put {#Products} and {/Products} in different cells of the same row.",
    "part": "word/document.xml",
    "field": "Products",
    "location": "table 2, row 3",
    "correlationId": "4f9c…",
    "relatedErrors": [ … further errors found in the same run … ]
  }
}
```

| Code | HTTP | Meaning |
| --- | --- | --- |
| `INVALID_REQUEST` | 400 | Body is not a JSON object, a required property is missing, or a property has the wrong type |
| `INVALID_BASE64` | 400 | `templateBase64` is not base64 |
| `INVALID_DATA_JSON` | 400 | `dataJson` is not a JSON object, or a value contains control characters Word cannot store |
| `UNKNOWN_OPERATION` | 400 | Operation ID not implemented by the script |
| `RESOURCE_LIMIT_EXCEEDED` | 413 | A size, item-count, output or processing-time limit was hit |
| `INVALID_DOCX` | 422 | Not a `.docx` (non-ZIP, encrypted/legacy `.doc`, `.docm`, Strict Open XML, missing or malformed parts) |
| `UNSAFE_PACKAGE` | 422 | Path traversal, duplicate entries, encrypted ZIP entries, or suspicious compression |
| `INVALID_PLACEHOLDER` | 422 | Malformed tag, unclosed or stray brace, tag interrupted by a tab, break or field, or ambiguous `{{Name}}` |
| `MISSING_FIELD` | 422 | Strict mode and a value is missing or `null` |
| `TYPE_MISMATCH` | 422 | For example a string for a condition, an object for a text tag, or an array for a paragraph block |
| `UNBALANCED_MARKER` | 422 | `{#X}` without `{/X}` (or the reverse), or crossed sections |
| `UNSUPPORTED_TEMPLATE_STRUCTURE` | 422 | Multi-row loops, markers in one cell, inline conditions, unclonable rows, tags in text boxes, fields or footnotes, section breaks inside conditions |
| `UNSUPPORTED_CHECKBOX` | 422 | The checkbox cannot be updated safely (unexpected content, mismatched symbol, data binding, invalid Tag) |
| `DUPLICATE_CHECKBOX_TAG` | 422 | Two checkboxes in the same scope and part share a Tag (often copy-pasted controls) |
| `RENDER_FAILED` | 500 | Internal fault or failed output integrity check; quote the correlation ID |

Diagnostics contain codes, field names, part names and locations such as `table 2, row 3, cell 1, paragraph 1`. They never contain document text or data values. Internal faults return only a generic message and the correlation ID; just the exception type name is logged.

Warnings include `MISSING_FIELD` (non-strict mode), `EMPTY_TABLE_REMOVED`, `TRACKED_CHANGES`, `FILE_NAME_SANITIZED`, `UNKNOWN_REQUEST_PROPERTY`, `DUPLICATE_CHECKBOX_TAG` (non-strict mode), `ROW_NOT_REPEATABLE` and `REMOVAL_MAY_FAIL` (the last two from validation).

## Limits

These are application safety caps in `script.csx` (`Limits` class), not documented Power Platform limits. Retune them after measuring in the target tenant.

| Limit | Value |
| --- | --- |
| Request body | 16 MiB of characters |
| Template (decoded) | 5 MiB |
| `dataJson` | 5 MiB of characters, nesting depth 64 |
| ZIP entries | 1,000 |
| Total uncompressed template | 25 MiB; 16 MiB per part |
| Compression ratio | > 200:1 for parts over 1 MiB is rejected as a possible ZIP bomb |
| Items per repeated row | 1,000 |
| Generated table rows per document | 3,000 |
| Generated XML elements from repeats | 2,000,000 |
| Text value length | 32,000 characters |
| Generated `.docx` | 20 MiB (about 27 MiB once base64-encoded) |
| Diagnostics returned | 100 |
| Processing budget | 90 seconds, below the 120-second custom-code limit |

Local measurements (`dotnet test`, Linux container, .NET 10):

| Case | Time |
| --- | --- |
| Example template (7 story parts, 3 products) | 44 ms |
| 1,000 repeated rows | 515 ms |
| 200 paragraphs × 32,000-character values | 642 ms |

Tenant timings will differ and must be measured; see [Deployment](docs/DEPLOYMENT.md#timeouts-and-performance).

## Files, build and tests

| Path | Purpose |
| --- | --- |
| `apiDefinition.swagger.json`, `apiProperties.json`, `script.csx` | The deployable connector (import-ready) |
| `examples/` | `ProductSummaryTemplate.docx` (example template), `ProductSummary.data.json` (sample data) and `ProductSummary.generated.docx` (engine output for review in desktop Word) |
| `fixtures/build_fixtures.py` | Regenerates the example template, the independent python-docx and LibreOffice fixtures, and `tests/*.json` |
| `fixtures/*.docx` | Cross-check templates produced by python-docx and re-saved by LibreOffice |
| `tests/*.json` | Seeded scenarios for the repository harness and the local Swagger workbench |
| `../../testing/tests/ConnectorTestKit.Tests/DocxTemplateGeneratorTests.cs` | Test matrix (text, fragmented runs, tables, headers/footers, conditions, checkboxes, formatting, safety, API, performance). Output is checked with the Open XML SDK validator, **test-only**. |
| `../../testing/DocxTemplateGeneratorCompile/` | Compiles the unchanged `script.csx` against .NET Standard 2.0 / C# 7.3 and Microsoft's documented `ScriptBase`/`IScriptContext` |

The script is maintained directly as a single file, following the repository's convention, so there is no bundling step. The logical modules are marked by numbered section comments.

```powershell
dotnet build testing/DocxTemplateGeneratorCompile/DocxTemplateGeneratorCompile.csproj
dotnet test testing/ConnectorTesting.sln
# Optional: write generated samples for manual review in desktop Word
$env:DOCX_REVIEW_OUTPUT = "C:\temp\docx-review"; dotnet test testing/ConnectorTesting.sln --filter Writes_review_samples
# Regenerate fixtures and scenarios (Python 3; python-docx and LibreOffice optional)
python3 connectors/DocxTemplateGenerator/fixtures/build_fixtures.py
```

## Deployment

```powershell
.\deploy-connector.ps1 -ConnectorName "DocxTemplateGenerator"
```

- Choose **No authentication**.
- Make sure **Code** is enabled for all three operations.
- The Swagger `host` is the reserved placeholder `api.example.com`, following this repository's convention for transformation connectors. Because custom code handles every operation and never forwards the request, nothing is ever sent to that host.

Licensing, DLP, rollout and the tenant checks still outstanding are in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).
