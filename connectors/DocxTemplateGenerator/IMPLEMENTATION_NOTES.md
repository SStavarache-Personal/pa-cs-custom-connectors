# Implementation notes

The implementation log for V1 of the DOCX Template Generator: which conventions were followed, which architecture decisions were made, what was verified and what was not.

## Repository conventions followed

- **Connector layout:** `connectors/DocxTemplateGenerator/` holds `apiDefinition.swagger.json` (OpenAPI 2.0 JSON, title ≤ 30 characters), `apiProperties.json` (with `scriptOperations`), `script.csx` and `README.md`, as described in `AGENTS.md` and the authoring skill.
- **Closest existing pattern:** `HtmlSlideConverter`, which is also backendless and builds an OOXML package in memory. This connector reuses its shapes:
  - the placeholder host `api.example.com` with no authentication,
  - a structured `{"error": {...}}` contract with 4xx/5xx statuses,
  - a strict/non-strict switch and a validate action,
  - xUnit tests that check outputs with the Open XML SDK **as a test-only dependency**,
  - a `testing/*Compile` project that compiles the unchanged script against `netstandard2.0` / C# 7.3, with a CI build step.
- **Harness:** scenarios live in `tests/*.json` (run by `ConnectorHarnessTests` and shown in the local workbench). Repository validation (namespaces, size, operation alignment) passes unchanged.
- **Naming:** PascalCase operation IDs and definitions, `_camelCase` private fields, `Async` suffix on async methods.
- **Single script file, no bundler:** every connector in the repository maintains `script.csx` directly, and the harness compiles that file. A multi-file source tree plus a bundling step would add a generated artifact that could drift from its sources. Instead the script is organized into seven numbered logical modules (entry, errors/limits, package reader/writer, tokenizer, renderer, checkbox renderer, inventory), mainly as nested classes.

## Architecture decisions

| Decision | Rationale |
| --- | --- |
| **Base64-safe operation routing**: raw operation IDs are matched first, then the value is base64-decoded | All three operation IDs are 16 characters of valid base64. The repository's usual "always try to decode first" snippet would turn `GenerateDocument` into garbage bytes. Covered by tests. |
| `Context.CorrelationId` read defensively; `CorrelationId` added to the test kit's `ConnectorRuntimeContext` | It is part of Microsoft's documented `IScriptContext` but was missing from the local runtime emulation. The change is additive. |
| One engine for discovery and rendering | `ListPlaceholders` and `ValidateTemplate` run the same walker as `GenerateDocument` in a discovery mode, as the spec requires. With data, `ValidateTemplate` does a full in-memory render plus the output checks, but returns no file. |
| Plan-then-mutate on live XML | Each container is analyzed (tags, marker pairing) before it is changed. Cloned rows are analyzed again rather than mapped back to the prototype, which keeps rendering of every copy deterministic. |
| Visible-text model with barriers | A paragraph is flattened into its `w:t` text. Tabs, breaks, fields, drawings and the boundaries of hyperlinks, content controls and revisions become a non-XML barrier character. A tag may cross run boundaries (the common Word fragmentation) but never a barrier, so a rewrite can never straddle containers. |
| Replacement formatting: the tag's first character | The run holding `{` receives the value; the tag's other runs lose only the tag characters. Text and run boundaries outside the tag are untouched. |
| Escapes `{{` / `}}`, with `{{FieldName}}` rejected | Gives a way to print literal braces, while handlebars-style tags fail loudly instead of rendering literally. |
| Strict scoping: no fallback from row items to top-level data | Required by the spec; it removes ambiguity. `Root.` reaches top-level data. |
| Row sections need different cells, except in one-cell rows | Follows the spec's convention. One-cell rows are allowed so single-column tables can be repeated; this is unambiguous because inline conditions are unsupported. |
| Block conditions remove their marker paragraphs | Predictable spacing: authors control gaps with real content paragraphs. |
| Clone safety | The first copy keeps all IDs. Later copies get new deterministic `w:sdt/w:id` values (allocated from 1,000,000,000, skipping IDs used by any story, footnote or endnote part), have `w14:paraId`/`w14:textId` removed, and lose bookmarks and range permissions. Rows containing drawings, hyperlinks, notes, comments, nested tables, revisions, vertical merges or data-bound controls are rejected for arrays (allowed as Boolean conditions). |
| Orphan cleanup only for ranges the engine broke | Bookmark, permission and comment-range markers that lost their partner because of a removal are deleted; pre-existing orphans are left as they were. |
| Cells, headers, footers and the body always end with a paragraph | Word requires this. A fix-up paragraph is added only when a removal leaves a table last or nothing at all. |
| Checkbox update: state + glyph + (if needed) font only | Only Word's single-run `w:t` representation is accepted, including private-use-area glyphs for symbol fonts such as Wingdings. Anything else raises `UNSUPPORTED_CHECKBOX`, so visual state and `w14:checked` can never disagree. |
| Tags in excluded locations are errors | Text boxes, field results, footnotes/endnotes, placeholder text and data-bound controls are not rewritten. A tag found there fails the request, so tags are never left silently. |
| Package hardening | Central-directory parse to detect encrypted entries; reject unsafe or duplicate (case-insensitive) entry names, Deflate64/other methods, ZIP64 inconsistencies and a > 200:1 ratio on parts over 1 MiB; check actual versus declared sizes during bounded decompression. DTDs are prohibited, there is no `XmlResolver`, and external relationships are never resolved. `.dotx` input is converted to a `.docx` content type; `.docm`/`.dotm` and Strict Open XML are rejected. |
| Untouched entries copied byte-for-byte | Entry order, names, timestamps and compression method (stored vs deflated) are kept. Only modified XML parts are re-serialized (UTF-8, declaration kept, whitespace preserved). |
| Output integrity check | The generated ZIP is reopened to check: same entries in the same order, every changed part parses, no table without rows, every cell ends with a paragraph, each engine-allocated `sdt` ID appears exactly once, and (strict mode) every bound checkbox's glyph matches `w14:checked`. A failure is `RENDER_FAILED`. |
| HTTP statuses | 400 request/data shape, 413 limits (including the 90-second budget; 408 would be auto-retried by Power Automate), 422 template/data semantics, 500 internal. |
| `ListPlaceholders` row-type hint | Without data a row section is ambiguous. Flag-like names (`ShowX`, `IsX`, `HasX`, …) are inventoried as Boolean rows; others as arrays when they use item fields. This affects only the inventory, never rendering. |

## Test assets

- `examples/ProductSummaryTemplate.docx` reproduces Word's XML conventions:
  - rsids, `w:proofErr` and a `_GoBack` bookmark splitting tags,
  - `w14:paraId`, Word's checkbox markup, a custom table style with first/last-row formatting,
  - a bullet list, an inline PNG, a PAGE field,
  - two sections with first, default, even and section-specific headers and footers, one of them a landscape section.

  It is **generated** by `fixtures/build_fixtures.py`, not saved by Word.
- `fixtures/independent-python-docx.docx` (python-docx) and `fixtures/independent-libreoffice.docx` (the same file re-saved by LibreOffice) are independently produced cross-checks.
- Edge cases are built in memory by `DocxTestPackage` in the test project. These include unsafe ZIPs, DTDs, encrypted flags, bombs and malformed XML, which are deliberately not committed as binary files.
- Every successful generation in the tests is checked with `OpenXmlValidator(FileFormatVersions.Office2019)`, and must introduce no validation error that the template did not already have.

## Verification log

| Date | Environment | Check | Result |
| --- | --- | --- | --- |
| 2026-10-11 | Linux container, .NET SDK 10.0.401 | `dotnet test testing/ConnectorTesting.sln` | 210 passed (81 pre-existing + 129 new, including 5 harness scenarios) |
| 2026-10-11 | same | `dotnet build testing/DocxTemplateGeneratorCompile` (netstandard2.0, C# 7.3, official interface shim) | Build succeeded, no errors |
| 2026-10-11 | same | Script size | 127 KB (127,429 bytes) of the 1 MB limit |
| 2026-10-11 | same | Performance: example 44 ms; 1,000 rows 515 ms; 200 × 32,000-char values 642 ms | Recorded by `Performance_common_large_and_long_text_documents` |
| 2026-10-11 | LibreOffice 24.2 headless → PDF → PNG | Visual check of generated example, 25-row table and python-docx sample | Layout, table styling, repeated-row shading, checkboxes, lists, image, headers and footers render as intended |
| — | Power Platform tenant | Import, Test tab, cloud flow, Power Apps | **Not performed: no tenant access.** See `docs/DEPLOYMENT.md` checklist |
| — | Desktop Microsoft Word | No repair prompt; checkbox toggle, save and reopen | **Not performed: Word not available.** Generate review samples with `DOCX_REVIEW_OUTPUT` |

## Known gaps and unverified assumptions

- **Tenant behaviour is unverified:**
  - acceptance of the placeholder host,
  - designer mapping of **File content** to a `format: byte` parameter,
  - effective timeout (2 minutes vs the older 5-second statement),
  - request/response payload ceilings,
  - Power Apps invocation.
- **Desktop Word acceptance is unverified.** Open XML SDK schema validation passes, but Word's repair heuristics can be stricter. In particular the following must still be opened in Word:
  - the duplicated-row output with stripped `w14:paraId`,
  - regenerated `w:sdt` IDs,
  - a cell or header given a fix-up empty paragraph.
- Generated fixtures imitate Word output; an actual Word-saved template should be added to `fixtures/` during tenant verification.
- Table styles with last-row formatting apply to the row that ends up last; this was verified only in LibreOffice rendering.
- Pagination and statistics in `docProps/app.xml` are not updated (Word recalculates them on open).
- Field results such as TOC page numbers are not refreshed.
- Tags inside the middle paragraphs of a multi-paragraph field (for example TOC entry text) are processed, because fields are tracked per paragraph.

## Next steps outside V1

- Paragraph-block loops (repeat paragraphs per item) and nested row loops through nested tables.
- Optional fallback scoping (item → parent → root) as an opt-in mode.
- Image insertion from base64 data, hyperlink values, and rich text runs.
- Updating `docProps` and asking Word to refresh fields on open (`w:updateFields`), opt-in.
- Comments, footnotes, endnotes and text-box story support.
- A solution-packaged deployment artifact if the repository adopts solution packaging.
