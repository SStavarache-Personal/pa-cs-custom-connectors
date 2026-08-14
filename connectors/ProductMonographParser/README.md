# Product Monograph Parser

`ProductMonographParser` is a request-driven Power Automate custom connector for deterministic extraction of Health Canada product-monograph facts and clinical text blocks from ordered Markdown page chunks. It performs no outbound HTTP calls and does not use an AI service.

## Operation

### `ParseProductMonograph`

`POST /monographs/parse`

Accepts the page-array output of `PdfTextExtractor` operations `ExtractPdfPageChunks` and `ExtractPdfMarkdownPageChunks`, or the compatible `ProductMonographOcr` output. The operation returns HTTP 400 only when the request contract is malformed. A valid document that is incomplete or uncertain returns HTTP 200 with explicit field and document dispositions.

Representative request:

```json
{
  "sourceUrl": "https://example.sharepoint.com/monographs/sample.pdf",
  "pages": [
    {
      "metadata": {
        "fileName": "sample.pdf",
        "pageCount": 2,
        "pageNumber": 1
      },
      "contentType": "text/markdown",
      "text": "# PRODUCT MONOGRAPH\n\nControl No.: 123456\n\nDate of Initial Authorization: January 15, 2004",
      "truncated": false,
      "warnings": []
    },
    {
      "metadata": {
        "fileName": "sample.pdf",
        "pageCount": 2,
        "pageNumber": 2
      },
      "contentType": "text/markdown",
      "text": "# 1 INDICATIONS\n\nClinical text.\n\n# 2 CONTRAINDICATIONS\n\nContraindication text.",
      "truncated": false,
      "warnings": []
    }
  ]
}
```

The response includes:

- `documentStatus`: `parsed`, `partial`, `needsReview`, or `unsupported`.
- bilingual language evidence from title and section vocabulary.
- control number evidence, page, label, and confidence.
- modern/legacy template evidence based on numbered master-template structure.
- initial authorization and revision dates with raw text, ISO values, and an explicit extraction basis.
- complete indication, pediatric, geriatric, and contraindication Markdown blocks with exact page slices and embedded HTTP(S) URLs.

## Deterministic and fail-closed behavior

- Detection is insensitive to Markdown decoration, case, punctuation, whitespace, and common English/French accents. Returned Markdown is never normalized.
- Explicit date labels outrank chronology. Unlabelled chronological fallback is returned only for one or two dates on product-monograph title pages and is marked `chronologicalInference`.
- Dotted-leader and table-of-contents headings are excluded from section selection.
- Modern classification requires numbered core sections; a patient-information title alone is not enough.
- Conflicting languages, numbers, or dates are not guessed. They produce `needsReview` or an ambiguous field status.
- Missing pages, truncation, low-confidence OCR, and unsupported OCR propagate to affected sections and the document disposition.
- A page with low-confidence or unsupported OCR cannot contribute consumable text even if an upstream payload includes text accidentally.

## Input validation and limits

- Pages must be strictly ascending and unique.
- Each page needs a positive number in `metadata.pageNumber` or `pageNumber`, `contentType: text/markdown`, and a string `text` value.
- Repeated document metadata (`pageCount`, `fileName`, `pdfVersion`, `fileSizeBytes`, and title) must be consistent.
- At most 1,000 page chunks and 6,291,456 UTF-16 characters per page are accepted.
- `sourceUrl` is optional, must be HTTP(S), and is retained only at document level. Section URL arrays contain links embedded in the section text.

## Deployment

Import `apiDefinition.swagger.json`, paste `script.csx` into the connector Code tab, and enable custom code for `ParseProductMonograph`. No authentication or connection parameters are required because the connector transforms the request locally. The placeholder host is not contacted.

The script uses one supported Power Automate C# file, stays below the 1 MB custom-code limit, and must finish within the platform's two-minute execution limit.
