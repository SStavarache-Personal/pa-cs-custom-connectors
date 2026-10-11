# Word template authoring guide

This guide is for the people who maintain templates in Microsoft Word. You need no code: type tags into the document, format them like any other text, and save as **Word Document (.docx)**.

A complete working example is in [`../examples/ProductSummaryTemplate.docx`](../examples/ProductSummaryTemplate.docx), with sample data in [`../examples/ProductSummary.data.json`](../examples/ProductSummary.data.json).

## 1. Text tags

Type a field name in curly braces anywhere in normal body, header, footer or table-cell text:

```
Prepared for {Customer.Name} ({Customer.City}) on {ReportDate}.
```

with data

```json
{ "Customer": { "Name": "Hôpital Sainte-Thérèse", "City": "Montréal" }, "ReportDate": "2026-10-11" }
```

- **Formatting:** format the tag the way the value should look. The value takes the formatting of the tag's **first character** (the `{`), so a bold `{` gives a bold value. Text around the tag keeps its own formatting.
- **Split runs are fine.** Word often stores a tag in several pieces internally, for example after spell-check, autocorrect or partial formatting. The connector still finds the tag.
- **One paragraph only.** A tag cannot span two paragraphs or cells, or contain a tab, line break or field.
- **Dots** reach nested objects: `{Customer.Address.City}`. Array indexing such as `{Products[0].Name}` is not supported; use a repeated row instead.
- **Case-sensitive:** `{companyName}` and `{CompanyName}` are different fields.

### Exact tag grammar

```
tag      = "{" ws* [ "#" | "/" ] ws* path ws* "}"
path     = segment *( "." segment )
segment  = one or more of: letters (any language), digits, combining marks, "_", "-", inner spaces
```

- Spaces just inside the braces and around dots are ignored, so `{ Customer . Name }` is `{Customer.Name}`. Spaces inside a name are kept, so `{Company Name}` reads the JSON property `"Company Name"`.
- `#` and `/` are reserved for section markers. Any other punctuation inside braces (for example `{Price: 5}`) is an `INVALID_PLACEHOLDER` error rather than being silently ignored.
- A path starting with `Root.` always reads from the top-level data object (see [Scope](#5-scope-root-and-repeated-rows)). As a result, a top-level JSON property that is itself named `Root` can only be read as `{Root.Root}`.
- **Literal braces:** write `{{` for `{` and `}}` for `}`. A field name in double braces, such as `{{Name}}`, is rejected as ambiguous so that handlebars-style tags are never printed silently. To show literal text such as `{Name}` in the output, supply it through data, for example `{Example}` with `"Example": "{Name}"`. Values are never interpreted as tags.

### How values are written

| JSON value | Output text |
| --- | --- |
| `"text"` | The text as-is. `\n` (and `\r\n`, `\r`, vertical tab) become Word line breaks; `\t` becomes a tab. |
| `42`, `12.50`, `-0.5` | The JSON number as written: `42`, `12.50`, `-0.5`. Decimals keep their trailing zeros. |
| `true` / `false` | `true` / `false` |
| `null` or missing | See [missing values](#7-missing-values-and-strict-mode) |
| object or array | `TYPE_MISMATCH` error |

There is no locale formatting or formula evaluation. **Send currency, dates and localized numbers as preformatted strings**, for example `"PriceFormatted": "12,50 $"` or `"ReportDate": "11 octobre 2026"`. Values are inserted as plain text: HTML or Markdown is not interpreted, and XML special characters (`< > & " '`), accents and other Unicode are written safely.

## 2. Repeated table rows

1. Create the table in Word with its header row, column widths, borders, shading and alignment.
2. Format **one** row the way every data row should look. This is the template row.
3. In the template row, type `{#Products}` at the start of the **first cell** and `{/Products}` at the end of the **last cell**. Put the item's field tags in the cells.

| Product | DIN | Price |
| --- | --- | --- |
| `{#Products}{Name}` | `{DIN}` | `{PriceFormatted}{/Products}` |

```json
{ "Products": [
  { "Name": "Atorvastatin", "DIN": "02456789", "PriceFormatted": "$12.50" },
  { "Name": "Metformin",    "DIN": "02345678", "PriceFormatted": "$8.25" }
] }
```

Result: the header row is unchanged and followed by two data rows with the template row's formatting. The markers are removed.

- **Zero items** produce no data row; the header and other rows stay. If removing rows leaves a table empty, the table is removed and an `EMPTY_TABLE_REMOVED` warning is returned.
- Each item must be a JSON **object**; the tags in the row read that object's properties.
- Rows above and below the template row (headers, totals) are untouched and can contain ordinary tags, for example `{TotalFormatted}`.
- A marker can sit in its own paragraph within the cell. That paragraph is then removed, so no blank line is left.
- **Single-column tables:** when the row has exactly one cell, both markers may be in that cell, as in `{#Items}{Name}{/Items}`.
- Each repeated row may contain [checkboxes](#4-checkboxes) and [conditional paragraphs inside a cell](#3-conditions).
- Repeated rows work in headers and footers too.

**Table styles:** a Word table style with special first-row or last-row formatting applies it to whichever row ends up first or last *after* repetition. Check that the rendered borders and banding look right with 0, 1 and many items. If they don't, use direct cell formatting on the template row instead of style options.

### Not supported in repeated rows (V1)

Each of these is rejected with `UNSUPPORTED_TEMPLATE_STRUCTURE` and a location. No questionable file is produced.

- Loops spanning several rows, column loops, nested or overlapping row loops, or two row sections on one row.
- Markers in the same cell of a multi-cell row, or markers inside a sentence outside tables.
- Vertically merged cells (horizontal merges within the row are fine).
- Images, charts, shapes, text boxes or embedded objects in the template row.
- Hyperlinks, footnotes, endnotes or comments in the template row.
- Nested tables in the template row.
- Tracked changes in the template row, rows wrapped in a repeating-section content control, and data-bound content controls.

The same row used with `true`/`false` instead of an array may contain these elements; only cloning is restricted. `ValidateTemplate` warns with `ROW_NOT_REPEATABLE` when a row can only be used as a condition.

Bookmarks in the template row stay on the first copy only, so bookmark names remain unique.

## 3. Conditions

### Paragraph blocks

Put the start marker in a paragraph **by itself** and the end marker in a later paragraph **by itself**:

```
{#ShowDisclaimer}
This summary was prepared for {CompanyName}; prices are indicative.
• Coverage is subject to the plan rules.
{/ShowDisclaimer}
```

- `"ShowDisclaimer": true` keeps everything between the markers: paragraphs, lists, tables and images, with their formatting.
- `"ShowDisclaimer": false` removes everything between the markers.
- The two marker paragraphs are always deleted, so they leave **no blank line or spacing**. The spacing you see is that of the content paragraphs. Leave an empty paragraph inside or outside the block if you want an explicit gap.
- Only JSON `true` and `false` are conditions. `"true"`, `1` or a non-empty string give `TYPE_MISMATCH`, so there are no hidden truthiness rules.
- Both markers must be in the same container: the body, a header, a footer, one table cell, or one block content control.
- Blocks may be nested (`{#A}…{#B}…{/B}…{/A}`) and may follow one another. Crossed markers (`{#A}{#B}{/A}{/B}`) are rejected.
- A block cannot contain a section break. A block whose content includes footnotes, endnotes or comments can be kept but not removed.
- Inline conditions inside a sentence (`Hello {#VIP}valued {/VIP}customer`) are not supported.
- An **array** on a paragraph block is rejected. Repeat content with a table row instead; you can hide the table's borders if you only want repeated paragraphs.

### Table rows

The row markers from section 2 also work with a Boolean value:

| | |
| --- | --- |
| `{#ShowDiscountRow}Discount` | `{DiscountFormatted}{/ShowDiscountRow}` |

`true` keeps the row (without markers) and `false` removes it. Inside a Boolean row, tags read the enclosing data, not a list item.

### Conditions inside repeated rows

Inside a cell of a repeated row, a paragraph block is evaluated separately for each item:

```
{#Items}{Name}
{#HasNote}
Note: {Note}
{/HasNote}
```

## 4. Checkboxes

Use Word's modern checkbox content control. Legacy form-field checkboxes and typed symbols such as ☐ are not data-bound.

1. Show the Developer tab: **File → Options → Customize Ribbon → Developer**.
2. Place the cursor and choose **Developer → Controls → Check Box Content Control**.
3. With the checkbox selected, choose **Properties** and set **Tag** to the Boolean field name, for example `IsApproved`. The Title is optional and only shown to users.
4. Optional: change the checked/unchecked symbols in the same dialog.

With `"IsApproved": true` the checkbox is checked, and with `false` it is unchecked. The control stays a real, clickable checkbox: the connector updates its state and symbol (and the symbol font when the two states use different fonts) and keeps its Tag, Title, locking and formatting.

- The value must be JSON `true` or `false`; anything else is `TYPE_MISMATCH`.
- Checkboxes work in the body, headers, footers and repeated rows. In a repeated row the Tag names a property of the current item, and each copy gets its own state and a unique control ID.
- `Root.IsApproved` reads top-level data from inside a repeated row. Braces in the Tag (`{IsApproved}`) are tolerated.
- **Leave the Tag empty** for checkboxes the person filling the document should tick. Untagged checkboxes are never changed.
- Two checkboxes with the same Tag in the same scope of the same part raise `DUPLICATE_CHECKBOX_TAG`, because a copied checkbox keeps its Tag. Give each checkbox its own field, for example `IsApproved` and `IsRejected`. The same Tag in the body and in a header is allowed.
- Rejected with `UNSUPPORTED_CHECKBOX`:
  - controls whose content was edited so the symbol no longer matches their state,
  - controls bound to custom XML (XML mapping),
  - controls whose Tag is not a valid field name.

  Delete the control and insert a fresh one.

## 5. Scope, `Root.` and repeated rows

- At the top level, `{CompanyName}` reads `CompanyName` from the data object.
- Inside a repeated row, tags and checkbox Tags read the **current item only**. There is no automatic fallback to top-level data, so a name present both on the item and at the top level is never ambiguous. Use `{Root.CompanyName}` to read top-level data from inside a row.
- Boolean blocks and Boolean rows do not change scope.

## 6. Headers and footers

Tags, conditions, repeated rows and checkboxes work in every header and footer the document uses:

- default, first-page (**Different First Page**) and even-page (**Different Odd & Even Pages**) variants,
- headers and footers of every section.

A section whose header is **Link to Previous** simply reuses the earlier part, which is processed once. Header/footer files left in the package but not used by any section are ignored.

## 7. Missing values and strict mode

The flow or app chooses `strictMode`; it defaults to `true`.

- **Strict:** any missing or `null` value is an error that names the field and its location. No document is produced, so a tag can never silently survive.
- **Non-strict:** missing text becomes empty, missing conditions and rows are removed, missing checkboxes keep their template state, and each case is listed in `warnings`.

Malformed tags, unsupported layouts and type mismatches are errors in both modes. Fields inside removed blocks or empty/false rows are not needed.

## 8. What is not processed

These locations are never rewritten. If a tag is found there, generation fails with `UNSUPPORTED_TEMPLATE_STRUCTURE` so it cannot be left behind:

- text boxes and shapes,
- Word field results (TOC, PAGE, DOCPROPERTY…),
- equations,
- content-control placeholder text and data-bound controls,
- footnotes and endnotes.

Comments are not scanned or changed.

Also outside V1: inserting images, charts, HTML or rich text from data, column loops, dynamic table creation, PDF conversion, macros (`.docm` is rejected) and legacy form-field checkboxes.

The document's styles, numbering, images, charts, section and page settings, and any other content are copied unchanged. Adding rows or long text reflows pages, as it would in Word; that is expected.

## 9. Before you publish a template

1. Run **ListPlaceholders** to check that every field you expect is found, with the right type and scope. Use its `sampleDataJson` as a starting payload.
2. Run **ValidateTemplate** with realistic data, including an empty array and the longest values you expect.
3. Generate with 0, 1 and many rows, and with every condition both `true` and `false`. Open each result in desktop Word: there must be no repair prompt, checkboxes must toggle, and rows must look right.
4. If Track Changes was on while editing, **accept or reject all changes**. Tracked changes are kept in generated documents, and a `TRACKED_CHANGES` warning is returned.

## 10. Diagnosing errors

Every error has a `code`, a `message`, and usually `part`, `field` and `location`:

- `part` is the internal file: `word/document.xml` is the body; `word/header2.xml` or `word/footer1.xml` is a header or footer. `ValidateTemplate`'s `storyParts` says which section and variant each one is, for example `section 1 first header`.
- `location` counts from 1, as in `paragraph 7` (body paragraphs outside tables) or `table 2, row 3, cell 1, paragraph 1`. Errors in repeated rows add `(item n)`.

| Message starts with / code | Usual fix |
| --- | --- |
| `INVALID_PLACEHOLDER` "A '{' is not closed" | The closing brace is missing, or a tab, line break or field sits inside the tag. Retype the tag in one go. |
| `INVALID_PLACEHOLDER` "Double braces…" | Use `{Name}`, not `{{Name}}`. |
| `UNBALANCED_MARKER` | Add the missing `{/X}` or `{#X}` in the same container and check the spelling. |
| `UNSUPPORTED_TEMPLATE_STRUCTURE` "spans multiple table rows" | Put both markers in one row. |
| `UNSUPPORTED_TEMPLATE_STRUCTURE` "must be alone in their own paragraph" | Put each block marker on its own line (press Enter before and after it). |
| `MISSING_FIELD` "Inside a repeated row…" | Add the property to each item, or use `{Root.Name}`. |
| `TYPE_MISMATCH` | Send `true`/`false` for conditions and checkboxes, arrays of objects for repeated rows, and text or numbers for tags. |
| `DUPLICATE_CHECKBOX_TAG` | Open the checkbox **Properties** and give each checkbox its own Tag. |
