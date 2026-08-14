# Product-monograph corpus harness

This folder contains the committed, secret-free evidence needed to reproduce the read-only corpus inventory and selected local validation. Downloaded PDFs, extracted Markdown, connector responses, and resumable execution logs belong under `testing/corpus/tmp/`, which is ignored by the repository.

## Snapshot and reconciliation

Run from the Market Access repository's configured Python environment. The scripts read its existing SharePoint configuration and invoke its repository-owned DAX endpoint skill; they do not copy or print credentials.

```powershell
python testing/corpus/snapshot_sharepoint.py --output testing/corpus/manifests/sharepoint-product-monographs-YYYY-MM-DD.jsonl
python testing/corpus/snapshot_semantic_labels.py --output testing/corpus/manifests/semantic-monograph-links-YYYY-MM-DD.jsonl
python testing/corpus/build_corpus_manifest.py --sharepoint <sharepoint-jsonl> --semantic <semantic-jsonl> --output <corpus-jsonl> --report <summary-json>
python testing/corpus/select_parser_candidates.py --manifest <corpus-jsonl> --output <candidate-jsonl>
```

SharePoint enumeration uses `RenderListDataAsStream` to avoid the list-view threshold. All source access is read-only and resumable.

## Local selected-document run

Build the two existing local runners, download a bounded selection, extract Markdown with the unchanged `PdfTextExtractor`, then execute the parser twice per document:

```powershell
dotnet build testing/PdfTextExtractorRunner/PdfTextExtractorRunner.csproj -c Release
dotnet build testing/ProductMonographParserRunner/ProductMonographParserRunner.csproj -c Release
python testing/corpus/download_selected_pdfs.py --selection <selection-jsonl> --output-dir testing/corpus/tmp/pdfs --results testing/corpus/tmp/download-results.jsonl
python testing/corpus/extract_selected_markdown.py --selection <selection-jsonl> --pdf-dir testing/corpus/tmp/pdfs --output-dir testing/corpus/tmp/markdown --results testing/corpus/tmp/extraction-results.jsonl --runner testing/PdfTextExtractorRunner/bin/Release/net8.0/PdfTextExtractorRunner.dll
python testing/corpus/run_parser_corpus.py --selection <selection-jsonl> --extraction-results testing/corpus/tmp/extraction-results.jsonl --markdown-dir testing/corpus/tmp/markdown --output-dir testing/corpus/tmp/parser --results testing/corpus/tmp/parser-results.jsonl --report testing/corpus/reports/parser-smoke-YYYY-MM-DD.json --runner testing/ProductMonographParserRunner/bin/Release/net8.0/ProductMonographParserRunner.dll
python testing/corpus/run_ocr_corpus.py --selection <selection-jsonl> --pdf-dir testing/corpus/tmp/pdfs --output-dir testing/corpus/tmp/ocr --results testing/corpus/tmp/ocr-results.jsonl --report testing/corpus/reports/ocr-smoke-YYYY-MM-DD.json --runner testing/ProductMonographOcrRunner/bin/Release/net10.0-windows/ProductMonographOcrRunner.dll
python testing/corpus/validate_gold.py --annotation testing/corpus/gold/00063498.annotation.json --response testing/corpus/tmp/parser-gold-00063498.json --output testing/corpus/reports/gold-validation-YYYY-MM-DD.json
```

Files above the unchanged extractor's 32 MiB cap receive `requiresOcr`; they are not silently dropped. Every selection row receives a terminal harness disposition. A semantic-model language match is useful evidence, but it is not a gold label for control numbers, dates, template families, or exact section boundaries.

## Gold evidence

`gold/` contains human-verifiable annotations and exact UTF-8 SHA-256 hashes for section Markdown. Do not promote automatically inferred parser output into gold. A 400-document acceptance set is only locked once all four requested strata have 100 independently verified annotations and tuning/holdout assignment is immutable.
