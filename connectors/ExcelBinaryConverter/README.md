# Excel Binary Converter

This Power Automate custom connector converts legacy Excel `.xls` (OLE Compound File + BIFF8) and `.xlsb` (ZIP/OPC + BIFF12) files into standard `.xlsx` workbooks. The parser and writer run entirely inside the connector's C# custom-code sandbox: the script does not call an API, launch Office, or use an external conversion service.

The generated workbook is intentionally value-only. It keeps worksheet order and names, hidden/very-hidden worksheet state, populated cell addresses, explicit blank cells, and the following cached cell data types:

- text, including BIFF shared strings and BIFF8 `CONTINUE` records;
- numbers, including RK and MULRK encodings;
- Boolean values;
- Excel error values;
- cached formula results (number, text, Boolean, error, or blank);
- date/time serials, their date/time classification, and the workbook's 1900/1904 date system.

Formatting, formulas themselves, macros, charts, drawings, merged cells, comments, names, links, and other workbook features are not copied. Password-protected or encrypted files are rejected. BIFF versions older than BIFF8 and non-worksheet sheet types are also rejected rather than silently producing misleading output.

## Operation

### ConvertToXlsx

Send a JSON body with:

| Field | Required | Description |
| --- | --- | --- |
| `fileContent` | Yes | Base64 content of an `.xls` or `.xlsb` file. A Power Platform binary object containing `$content` is accepted too. |
| `fileName` | No | Original name used to derive the returned `.xlsx` name. |

The successful response contains `fileName`, `contentType`, base64 `fileContent`, detected `sourceFormat`, `sheetCount`, `cellCount`, and `elapsedMilliseconds`. Pass `fileContent` directly to the content input of a **Create file** action and use the returned `fileName` as its name.

## Guardrails

The script keeps a 10-second margin inside the Power Platform 120-second custom-code limit and accepts at most:

- 25 MB decoded input;
- 75 MB generated `.xlsx` output;
- 500,000 cells;
- 1,024 worksheets;
- 128 MB cumulatively expanded from an `.xlsb` ZIP package.

These caps are deliberate because Power Automate custom code runs in a memory- and time-constrained process. The connector returns a structured error instead of partially converting a workbook when a limit or unsupported feature is encountered.

## Files

- `apiDefinition.swagger.json` — OpenAPI 2.0 operation and schemas.
- `apiProperties.json` — custom connector metadata and script operation registration.
- `script.csx` — standalone BIFF8/BIFF12 readers and minimal SpreadsheetML writer.
- `tests/` — request/response fixtures used by the repository connector test harness.

## Attribution

The record-level parsing approach is adapted from the Apache-2.0-licensed [powerquery-driverless](https://github.com/SStavarache-Personal/powerquery-driverless) `Xls.Workbook.pq` and `Xlsb.Workbook.pq` readers. This connector is an independent C# implementation designed around the Power Platform custom-code namespace, file-size, and runtime constraints.
