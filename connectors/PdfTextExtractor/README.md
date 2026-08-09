# PDF Text Extractor

`PdfTextExtractor` reads the existing digital or OCR text layer from a PDF entirely inside a Power Automate custom-code connector. It needs no API key, NuGet package, native binary, external service, or outbound HTTP call.

The implementation targets the hard case this repository did not previously cover: PDFs saved or exported from Microsoft Office. Word-generated product monographs use mixed simple and CID fonts, `ToUnicode` maps, individually positioned text runs, marked-content artifacts, forms, compressed streams, tables, bullets, and incremental cross-reference data. The extractor parses those structures and reconstructs plain text in spatial reading order.

## Operation

| Operation ID | Method and path | Result |
| --- | --- | --- |
| `ExtractPdfText` | `POST /pdf/extract` | Extracted text plus page, truncation, and warning metadata |
| `ExtractPdfPageChunks` | `POST /pdf/extract-page-chunks` | A JSON array containing Markdown-compatible text and PDF metadata per page |

Example request body:

```json
{
  "contentBytes": "<PDF file content from an earlier Power Automate action>",
  "startPage": 1,
  "endPage": 10,
  "includeArtifacts": false,
  "includePageBreaks": true
}
```

In a flow, bind **PDF Content** directly to the file-content output from SharePoint, OneDrive, Word Online, Excel Online, or another file action. Do not convert it with a Power Automate `base64()` expression; the connector's `format: byte` field handles the transport encoding.

The response contains:

- `text`: plain text with optional `--- Page N ---` separators.
- `hasTextLayer`: whether any selected page contained extractable text.
- `pageCount`, `pagesExtracted`, `startPage`, and `endPage`.
- `characterCount`, `truncated`, and `warnings`.

`includeArtifacts` defaults to `false`, which removes Office-generated headers, footers, and page numbers when the PDF tagged them as artifacts.

### Page-chunk response

`ExtractPdfPageChunks` accepts the same request fields. Set the optional `fileName` field when the flow knows the source name, because a PDF byte stream does not retain the SharePoint or OneDrive storage name. The operation returns a bare JSON array so **Apply to each** can consume it directly:

```json
[
  {
    "metadata": {
      "format": "PDF 1.7",
      "pdfVersion": "1.7",
      "fileName": "product-monograph.pdf",
      "fileSizeBytes": 656821,
      "title": "Product Monograph",
      "author": null,
      "subject": null,
      "keywords": null,
      "creator": "Microsoft Word for Microsoft 365",
      "producer": "Microsoft Word for Microsoft 365",
      "creationDate": "D:20250101120000-05'00'",
      "modificationDate": "D:20250101120000-05'00'",
      "trapped": null,
      "pageCount": 34,
      "pageNumber": 1
    },
    "page": {
      "width": 612.0,
      "height": 792.0,
      "rotation": 0
    },
    "contentType": "text/markdown",
    "text": "Page-one Markdown-compatible text...",
    "characterCount": 1132,
    "hasTextLayer": true,
    "truncated": false,
    "warnings": []
  }
]
```

The repeated `metadata` object is intentionally close to PyMuPDF4LLM's `page_chunks=True` model while using camel-case fields that are convenient in Power Automate. `title`, `author`, `subject`, `keywords`, `creator`, `producer`, `creationDate`, `modificationDate`, and `trapped` come from the PDF Info dictionary. `format`, `pdfVersion`, `fileSizeBytes`, `pageCount`, `pageNumber`, and page geometry are computed from the PDF itself. Text remains Markdown-compatible plain text; the connector does not invent headings or tables that are absent from the text layer.

## Supported PDF features

- PDF 1.x classic xref tables, xref streams, hybrid/incremental xrefs, object streams, and recovery scanning.
- Page-tree inheritance, rotated pages, nested Form XObjects, and inherited resources.
- Simple Type 1/TrueType fonts using WinAnsi, Standard Encoding, and `/Differences` glyph names.
- Type 0/CID fonts, variable-length `ToUnicode` CMaps, `bfchar`, and `bfrange` mappings.
- `Tj`, `TJ`, quote operators, text matrices, character/word spacing, horizontal scale, and rise.
- Flate, ASCIIHex, ASCII85, and RunLength stream filters, including TIFF/PNG predictors.
- Spatial line clustering, inferred spaces, tagged-artifact suppression, and bounded output.

The connector reads text that is already represented in the PDF. A searchable PDF produced by an OCR system is supported; an image-only scan returns `hasTextLayer: false` and an explanatory warning. Encrypted/password-protected PDFs return `ENCRYPTED_PDF_UNSUPPORTED`.

## Platform constraints addressed

- The script is one `script.csx`, is below the 1 MB custom-code limit, and uses only Microsoft's supported namespaces plus `Newtonsoft.Json`.
- It implements `public class Script : ScriptBase` and `ExecuteAsync`, and the Swagger operation ID, `scriptOperations`, and route all use `ExtractPdfText`.
- It is synchronous, cancellation-aware, has no network dependency, and is designed to stay within the two-minute custom-code timeout.
- The OpenAPI document is Swagger 2.0 and below 1 MB.
- Input is capped internally at 20 MiB by default (32 MiB absolute), output at 2 Mi characters by default (6 Mi absolute), decoded streams at 32 MiB each/96 MiB total, text fragments at 250,000 per page, and documents at 1,000 pages. Power Platform transport or gateway limits can be lower and apply before the script starts; base64 adds roughly one third to the request size.

Microsoft documents the custom-code contract, supported namespaces, .NET Standard 2.0 runtime, one-script rule, 1 MB script limit, and two-minute timeout in [Write code in a custom connector](https://learn.microsoft.com/en-us/connectors/custom-connectors/write-code). The connector/OpenAPI and flow limits are documented in the [custom connector FAQ](https://learn.microsoft.com/en-us/connectors/custom-connectors/faq), [OpenAPI import guidance](https://learn.microsoft.com/en-us/connectors/custom-connectors/define-openapi-definition), and [Power Automate limits](https://learn.microsoft.com/en-us/power-automate/limits-and-config).

## Accuracy benchmark

The reproducible benchmark compares normalized word tokens and token order against PyMuPDF4LLM 1.28.2 with OCR disabled, so both systems read the same embedded text rather than introducing an OCR-engine variable. It benchmarks both the original document response and the page-chunk response against `to_markdown(..., page_chunks=True)`, enables `includeArtifacts` to compare the full PDF text layer, downloads two pinned [Health Canada Drug Product Database product monographs](https://health-products.canada.ca/dpd-bdpp/index-eng.jsp), verifies their SHA-256 hashes, runs the exact connector script through the C# runtime shim, and fails unless every fixture reaches the committed accuracy thresholds.

- token multiset F1 >= 0.975;
- ordered-token `SequenceMatcher` ratio >= 0.94;
- exact page-chunk counts and PDF metadata, minimum page F1 >= 0.85, and mean page F1 >= 0.97;
- connector wall time < 120 seconds.

See [`benchmarks/README.md`](benchmarks/README.md) for the pinned fixtures and measured baseline.

Run from the repository root:

```bash
python -m venv .venv
. .venv/bin/activate
pip install -r connectors/PdfTextExtractor/benchmarks/requirements.txt
dotnet build testing/PdfTextExtractorRunner/PdfTextExtractorRunner.csproj -c Release
python connectors/PdfTextExtractor/benchmarks/benchmark.py \
  --runner-dll testing/PdfTextExtractorRunner/bin/Release/net8.0/PdfTextExtractorRunner.dll
python connectors/PdfTextExtractor/benchmarks/benchmark_page_chunks.py \
  --runner-dll testing/PdfTextExtractorRunner/bin/Release/net8.0/PdfTextExtractorRunner.dll
```

The downloaded PDFs and extracted text remain in `tmp/pdf-text-benchmark`; no copyrighted fixture content is committed.

## Implementation references

The parser is an original, dependency-free implementation based on Adobe's [PDF 1.4 reference](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/pdfreference1.4.pdf), particularly the file structure, filters, text operators, fonts, CMaps, and marked-content sections. [PdfPig](https://github.com/UglyToad/PdfPig) and [PDF.js](https://github.com/mozilla/pdf.js) were used as behavioral references for difficult font and content-stream cases; no source from either project is embedded. [PyMuPDF4LLM](https://github.com/pymupdf/RAG) is the independent benchmark oracle.

## Import

1. Create or open an unmanaged solution in Power Automate or Power Apps.
2. Create a custom connector from `apiDefinition.swagger.json`.
3. In the connector's **Code** tab, enable custom code and paste `script.csx`.
4. Select both `ExtractPdfText` and `ExtractPdfPageChunks` for custom code, then create/update the connector.
5. Test with an Office-exported searchable PDF and inspect `hasTextLayer`, `warnings`, and `truncated` before consuming `text`.

`api.example.com` is a syntactically valid placeholder host required by the connector definition. `ExtractPdfText` returns from custom code without calling it.
