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
  "maxPagesPerCall": 25,
  "languageHint": "fr"
}
```

Bind **PDF Content** directly to the binary output of SharePoint, OneDrive, or another file action. Do not wrap it in a Power Automate `base64()` expression.

Example response shape:

```json
{
  "pageCount": 22,
  "sourcePageCount": 22,
  "startPage": 1,
  "pagesReturned": 2,
  "processedRange": {
    "startPage": 1,
    "endPage": 2,
    "pagesReturned": 2,
    "ocrPagesProcessed": 2
  },
  "completed": false,
  "nextPage": 3,
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

Use `nextPage` as the next call's `startPage` until `completed` is true. `nextStartPage` and `hasMorePages` remain compatibility aliases. The connector passes through every usable text-layer page from `startPage`, processes at most 25 OCR-candidate pages per invocation, and stops at a 100-second soft deadline so it can return structured continuation data before Power Automate's two-minute hard timeout.

## Recognition design

The OCR implementation is original, dependency-free connector code:

1. Resolve the PDF page and preserve tagged Markdown when the text layer is usable.
2. Find image XObjects recursively and require one raster to represent at least 65% of total image area with a page-compatible aspect ratio.
3. Decode the dominant raster page-at-a-time.
4. Remove border rules and isolated scan noise, select global or adaptive binarization from the image's tonal range, and apply conservative projection-based deskew only when it materially improves line alignment.
5. Segment connected components, associate detached French diacritics, test broken/touching-glyph hypotheses, and preserve large horizontal gutters as separate reading-order segments.
6. Compare normalized glyph topology and shape against an embedded English/French serif/sans prototype model.
7. Build page-specific adaptive prototypes from high-confidence first-pass glyphs and reclassify uncertain glyphs in a second pass.
8. Apply narrow context corrections for the bilingual regulatory title, month-bearing dates, numeric-only control numbers, and percent notation.
9. Return text only when the combined average and lower-decile confidence reaches `minimumConfidence`.

This is intentionally not Tesseract. No traineddata, native binary, NuGet package, filesystem, process launch, HTTP request, or external OCR service is used at runtime.

### Embedded prototype-model provenance

The checked-in model contains normalized raster prototypes only; it does not contain or load font files. It was generated offline from open-font faces in [Noto](https://github.com/notofonts/noto-fonts) (SIL Open Font License), DejaVu Sans (Bitstream Vera-derived permissive license), and [Liberation Fonts 2.1.5](https://github.com/liberationfonts/liberation-fonts) (SIL Open Font License). The generator sampled regular, bold, and italic serif/sans faces at 30 px and 42 px with two binarization thresholds over the connector's English/French character set.

The resulting `OCR2` payload contains 5,148 unique 16-by-24-bit prototypes: 278,000 bytes before compression and 91,237 bytes after raw DEFLATE compression (121,652 Base64 characters; SHA-256 `075a239ecef826a104524873ccec811b2871bad0b9414ed5de02e954f4d430a0`). It is embedded in `script.csx`, decompressed in memory once per invocation, and makes no runtime font or network request. The complete script is 380,411 bytes in this revision, below the repository's 950 KiB safety target.

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
- Default and maximum OCR-candidate pages per call: 25. Usable text-layer pages do not consume this budget.
- Default output limit: 2 Mi UTF-16 characters; absolute limit: 6 Mi.
- Maximum pages in a PDF: 1,000.
- OCR image budget: 40 million pixels per page.
- Soft deadline: 100 seconds.
- Script runtime: one `script.csx`, supported .NET Standard 2.0 namespaces only, no outbound calls.

These connectors are cloud-only and do not use an on-premises data gateway. The 64 MiB decoded-PDF guard is an internal safety limit selected to leave room for Base64 and JSON overhead under Power Automate's 100 MB message-size limit.

The `System.Drawing` namespace is on Microsoft's supported custom-code list, but actual codec availability must be verified in every target Power Platform environment. Codec absence produces a structured fail-closed status.

## Testing

Saved scenarios cover:

- text-layer Markdown passthrough and provenance;
- start-page/max-pages continuation, including the 25 OCR-candidate page budget while text-layer pages pass through without consuming that budget;
- invalid base64, pages without images, and unsupported JBIG2 filters;
- synthetic Flate grayscale, DCT/JPEG RGB, CCITT Group 4, and RunLength grayscale pages;
- 90-degree page rotation and fail-closed low-confidence suppression;
- zero outbound requests.

The image-only 22-page Health Canada fixture `00003161.PDF` was also exercised as a corpus check without adding it to this repository. Against page-1 rendered visual ground truth, a deliberately relaxed diagnostic threshold of `0.50` produced all four required anchors (`MONOGRAPHIE DE PRODUIT`, `OXIZOLE`, `15 janvier 2004`, and `089170`) at reported confidence `0.780654`. That diagnostic result is not production acceptance: at the default `0.86` threshold all 22 pages were classified `lowConfidence`, all candidate text was suppressed, and continuation still completed. The connector therefore remains fail-closed, but this difficult scan is an explicit known fidelity gap rather than a claimed successful full-document transcription.

Run all repository tests:

```powershell
dotnet test testing/ConnectorTesting.sln
```

Deployment still requires a development-environment import and Test-tab validation, particularly for CCITT and DCT codec availability.
