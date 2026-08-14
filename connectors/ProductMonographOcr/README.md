# Product Monograph OCR

`ProductMonographOcr` is a request-driven Power Automate custom connector for Health Canada product-monograph PDFs. It reuses the proven PDF object, font, text-layer, and rich-Markdown extraction design from `PdfTextExtractor`, but it is a separate connector and does not alter that connector.

The connector uses a usable PDF text layer whenever one exists. Only an empty or obviously unusable page enters the embedded OCR path. OCR is deliberately fail-closed: uncertain, unsupported, or deadline-limited pages return an empty `text` value with an explicit `ocrStatus`, confidence, image metadata, and warnings.

## Operation

| Operation ID | Method and path | Result |
| --- | --- | --- |
| `ExtractProductMonographOcr` | `POST /monographs/ocr` | Continuation-aware wrapper containing PdfTextExtractor-compatible page chunks plus OCR provenance |

Example request:

```json
{
  "contentBytes": "<PDF file content>",
  "fileName": "00003161.PDF",
  "startPage": 1,
  "maxPagesPerCall": 2,
  "languageHint": "fr"
}
```

Bind **PDF Content** directly to the binary output of SharePoint, OneDrive, or another file action. Do not wrap it in a Power Automate `base64()` expression.

Example response shape:

```json
{
  "pageCount": 22,
  "startPage": 1,
  "pagesReturned": 2,
  "nextStartPage": 3,
  "hasMorePages": true,
  "deadlineReached": false,
  "elapsedMilliseconds": 8421,
  "pages": [
    {
      "metadata": {
        "fileName": "00003161.PDF",
        "pageCount": 22,
        "pageNumber": 1
      },
      "page": {
        "width": 610.56,
        "height": 790.92,
        "rotation": 0
      },
      "contentType": "text/markdown",
      "text": "",
      "characterCount": 0,
      "tableCount": 0,
      "usedTaggedStructure": false,
      "hasTextLayer": false,
      "truncated": false,
      "extractionMethod": "ocr",
      "ocrStatus": "lowConfidence",
      "confidence": 0.73,
      "sourceImage": {
        "filter": "CCITTFaxDecode",
        "width": 1696,
        "height": 2197,
        "dpi": 200
      },
      "warnings": [
        "OCR confidence was below the required threshold; uncertain text was suppressed."
      ]
    }
  ],
  "warnings": []
}
```

Use `nextStartPage` as the next call's `startPage` while `hasMorePages` is true. The connector processes at most ten pages per invocation and stops at a 100-second soft deadline so it can return structured continuation data before Power Automate's two-minute hard timeout.

## Recognition design

The OCR implementation is original, dependency-free connector code:

1. Resolve the PDF page and preserve tagged Markdown when the text layer is usable.
2. Find image XObjects recursively and require one raster to represent at least 65% of total image area with a page-compatible aspect ratio.
3. Decode the dominant raster page-at-a-time.
4. Binarize using an Otsu threshold, detect text-line and glyph projections, and normalize glyphs.
5. Compare glyphs with compact sans-serif, serif, and monospaced English/French prototypes rendered in memory.
6. Build page-specific adaptive prototypes from high-confidence first-pass glyphs and reclassify uncertain glyphs in a second pass.
7. Return text only when the combined average and lower-decile confidence reaches `minimumConfidence`.

This is intentionally not Tesseract. No traineddata, native binary, NuGet package, filesystem, process launch, HTTP request, or external OCR service is used at runtime.

## Supported raster subset

- CCITT Group 3 one-dimensional, Group 3 mixed two-dimensional, and Group 4 streams through an in-memory TIFF envelope and the supported `System.Drawing` codec.
- DCT/JPEG, including ASCIIHex/ASCII85/RunLength wrappers before DCT, through `System.Drawing`.
- Unfiltered, Flate, and RunLength images with existing PDF PNG/TIFF predictor handling.
- One-bit or eight-bit `DeviceGray`, and eight-bit `DeviceRGB`.
- PDF page rotation at 0, 90, 180, or 270 degrees.
- A dominant full-page image, with smaller logos tolerated.

The connector fails closed for JBIG2, JPEG 2000/JPX, CMYK, indexed/ICC color spaces, unsupported bit depths, image masks, tiled pages without one dominant raster, vector-only facsimiles, and codec/runtime failures.

## Status contract

- `notRequired`: usable text layer returned.
- `recognized`: OCR exceeded the confidence threshold and text is returned.
- `lowConfidence`: OCR ran but candidate text was suppressed.
- `noImage`: no raster candidate was present.
- `unsupportedImageFilter`: filter, color space, or bit depth is unsupported.
- `unsupportedPageComposition`: no dominant full-page raster.
- `codecUnavailable` / `imageDecodeFailed`: an in-memory image codec could not decode the raster.
- `classifierUnavailable` / `recognitionFailed`: OCR could not run safely.
- `resourceLimit`: the image exceeded the 40-million-pixel budget.
- `deadlineExceeded`: the soft deadline interrupted OCR and partial text was suppressed.

A successful HTTP 200 means the batch was classified; it does not imply every page produced text. Flow logic must inspect each page's `ocrStatus` and `truncated`.

## Limits and deployment notes

- Decoded PDF hard limit: 64 MiB.
- Default pages per call: 2; maximum: 10.
- Default output limit: 2 Mi UTF-16 characters; absolute limit: 6 Mi.
- Maximum pages in a PDF: 1,000.
- OCR image budget: 40 million pixels per page.
- Soft deadline: 100 seconds.
- Script runtime: one `script.csx`, supported .NET Standard 2.0 namespaces only, no outbound calls.

Power Automate's custom-connector gateway currently documents a much smaller request-content limit than this connector's 64 MiB internal guard. A large PDF can therefore be rejected before custom code starts. The 64 MiB guard protects local execution and any host that can deliver the payload; it does not override platform transport limits.

The `System.Drawing` namespace is on Microsoft's supported custom-code list, but actual codec availability must be verified in every target Power Platform environment. Codec absence produces a structured fail-closed status.

## Testing

Saved scenarios cover:

- text-layer Markdown passthrough and provenance;
- start-page/max-pages continuation;
- invalid base64, pages without images, and unsupported JBIG2 filters;
- synthetic Flate grayscale, DCT/JPEG RGB, CCITT Group 4, and RunLength grayscale pages;
- 90-degree page rotation and fail-closed low-confidence suppression;
- zero outbound requests.

Run all repository tests:

```powershell
dotnet test testing/ConnectorTesting.sln
```

Deployment still requires a development-environment import and Test-tab validation, particularly for CCITT and DCT codec availability.
