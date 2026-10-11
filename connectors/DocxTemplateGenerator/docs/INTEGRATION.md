# Power Automate and Power Apps integration

> **Verification status.** The flow and app recipes below follow documented Power Platform behaviour and the connector's actual request/response contract, which is covered by the local test suite. They have **not yet been executed in a tenant** for this release. Expressions marked **(verify)** depend on designer behaviour that must be confirmed during the tenant smoke test in [DEPLOYMENT.md](DEPLOYMENT.md#tenant-verification-checklist). Record the confirmed expressions there.

## How the data moves

```text
SharePoint "Get file content" (template .docx)
        │  file content (binary; JSON form {"$content-type", "$content": "<base64>"})
        ▼
DOCX Template Generator ── GenerateDocument(templateBase64, dataJson, fileName, strictMode)
        │  {"fileName", "mimeType", "fileBase64": "<base64 .docx>", "warnings", "statistics"}
        ▼
SharePoint "Create file"  ── File Content = base64ToBinary(fileBase64)
```

The connector does not call SharePoint or any other service. Retrieving the template and saving the result are separate actions in your flow, and they run under your connections.

## Power Automate cloud flow

### 1. Get the template

Add **SharePoint → Get file content** (or **Get file content using path**) for the `.docx` template. Its output, **File content**, is binary. In code view it looks like `{"$content-type": "application/vnd…", "$content": "UEsDB…"}`.

### 2. Build the data

Use a **Compose** action (here named `Compose_data`) with a JSON object:

```json
{
  "CompanyName": "@{triggerBody()?['company']}",
  "IsApproved": true,
  "Products": @{body('Select_products')}
}
```

**Select** is the simplest way to shape an array of SharePoint list items into row objects:

| Key | Value |
| --- | --- |
| `Name` | `item()?['Title']` |
| `DIN` | `item()?['DIN']` |
| `PriceFormatted` | `formatNumber(item()?['Price'], 'C2', 'en-CA')` |
| `IsCovered` | `item()?['Covered']` |

Preformat dates and money in the flow with `formatDateTime()` and `formatNumber()`. The connector prints values exactly as received.

### 3. Call Generate document

Add **DOCX Template Generator → Generate a Word document from a template**:

| Field | Value |
| --- | --- |
| Template file content | **File content** from step 1 (dynamic content) **(verify)** |
| Data (JSON text) | `string(outputs('Compose_data'))` |
| Output file name | `concat('Summary-', triggerBody()?['company'], '.docx')` |
| Strict mode | `Yes` (default) |

Template file content is declared as `string`/`format: byte`, so the designer should convert the binary **File content** to base64 when you map it. If your tenant passes something else, use one of these alternatives; the connector accepts all of them:

- `body('Get_file_content')?['$content']`, which is the base64 text itself **(verify)**,
- the whole file-content object, which is unwrapped automatically,
- a `data:…;base64,` URL.

**Data (JSON text)** must be text. `string(...)` serializes the Compose object; passing the object itself also works when the designer allows it.

### 4. Save the result

Add **SharePoint → Create file**:

| Field | Value |
| --- | --- |
| Folder path | your library folder |
| File name | `body('Generate_a_Word_document_from_a_template')?['fileName']` |
| File content | `base64ToBinary(body('Generate_a_Word_document_from_a_template')?['fileBase64'])` |

Replace `Generate_a_Word_document_from_a_template` with your action's internal name (spaces become underscores; check it in the action's **Peek code**). Do not pass `fileBase64` without `base64ToBinary()`, and do not apply `base64()` to it: either would save the base64 characters instead of a Word file.

The same File content expression works for **OneDrive for Business → Create file**, **Office 365 Outlook → Send an email (V2)** attachments (Content bytes), and Teams or Dataverse file columns.

### 5. Handle errors

A failed generation returns HTTP 4xx/5xx and fails the action, so the flow stops unless you handle it:

- Add a parallel branch or a **Scope** with **Configure run after → has failed**.
- Read `body('Generate_a_Word_document_from_a_template')?['error']?['code']` and `?['message']`; `?['field']` and `?['location']` point into the template.
- Log `error.correlationId` when you report a `RENDER_FAILED`.

The connector returns **413/422** for deterministic problems, so Power Automate's default retry policy does not retry them. It does retry **500**. If you prefer no retries at all, set the action's retry policy to **None**.

### 6. Optional: validate before publishing a template

A small admin flow can:

1. call **List the tags in a Word template** and show `fields` and `sampleDataJson`, then
2. call **Validate a Word template** with sample data and fail when `valid` is `false`.

## Power Apps (canvas)

Custom connectors are premium in Power Apps; see [DEPLOYMENT.md](DEPLOYMENT.md#licensing-and-dlp). Add the connector under **Data → Add data → DOCX Template Generator**.

### Recommended: let a flow do the file handling

Canvas apps have no direct way to read a SharePoint document library file as base64. The robust pattern is:

1. Power Apps calls a flow with a Power Apps (V2) trigger, passing the data as text: `JSON(record)`.
2. The flow runs steps 1–4 above and returns the new file's link.

```powerfx
Set(
    varResult,
    GenerateSummaryFlow.Run(
        JSON({
            CompanyName: txtCompany.Text,
            IsApproved: chkApproved.Value,
            Products: ForAll(colProducts, { Name: Name, DIN: DIN, PriceFormatted: Text(Price, "$#,##0.00", "en-CA"), IsCovered: Covered })
        }, JSONFormat.Compact)
    )
);
Launch(varResult.fileurl)
```

### Direct call from the app (verify)

When the app already holds the template as base64, for example from an attachment control or a Dataverse file column, it can call the connector directly:

```powerfx
Set(
    varDocx,
    DOCXTemplateGenerator.GenerateDocument({
        templateBase64: varTemplateBase64,
        dataJson: JSON({ CompanyName: txtCompany.Text, IsApproved: chkApproved.Value, Products: colProducts }, JSONFormat.Compact),
        fileName: "Summary.docx",
        strictMode: true
    })
);
// varDocx.fileBase64 is the generated file.
```

- `templateBase64` also accepts a `data:…;base64,` URL. `JSON(media, JSONFormat.IncludeBinaryData)` returns such a URL wrapped in double quotes. Strip them with `Mid(j, 2, Len(j) - 2)` before passing it **(verify)**.
- The connector's name in Power Fx is the display name with spaces removed. Check IntelliSense after adding it.
- Use `IfError(...)` or `Notify(FirstError.Message)` to show errors. The connector's JSON error body includes `code`, `message` and `location`.
- To save `fileBase64`, pass it to a flow that does `base64ToBinary()` → **Create file**. Browser download of `data:` URLs from canvas apps is restricted on some platforms **(verify)**.

Requests from Power Apps carry the same size limits. Keep templates and data small for interactive use: large data is better generated in a flow.

## Base64 conventions recap

| Where | Format |
| --- | --- |
| Input `templateBase64` | Standard base64 (whitespace ignored), `data:` URL, or `{"$content": …}` object |
| Output `fileBase64` | Standard base64 of the full `.docx` (about 4/3 of the file size) |
| To binary in a flow | `base64ToBinary(<fileBase64>)` |
| MIME type | `application/vnd.openxmlformats-officedocument.wordprocessingml.document` (returned as `mimeType`) |
