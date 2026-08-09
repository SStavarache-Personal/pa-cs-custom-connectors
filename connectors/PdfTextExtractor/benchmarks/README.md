# Accuracy benchmark

This benchmark executes the unmodified `script.csx` through the local C# `ScriptBase` shim and compares its output with PyMuPDF4LLM 1.28.2 on the same pinned PDFs. PyMuPDF4LLM is called with `use_ocr=False`, `header=True`, `footer=True`, and `table_format="grid"`.

Both PDFs report `Creator: Microsoft Word for Microsoft 365` and `Producer: Microsoft Word for Microsoft 365`. The benchmark verifies each download before execution, so a changed upstream document fails instead of silently moving the baseline.

| Health Canada fixture | SHA-256 | Pages | Bytes | Token F1 | Ordered-token ratio | C# internal time | Result |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| [Auro-Irbesartan, `00083665`](https://pdf.hres.ca/dpd_pm/00083665.PDF) | `193e309f9cbe17bfd3e9e77d9ff3a2e73b1772f4bac4cd2a4001f3bc6243a25e` | 34 | 656,821 | 0.992283 | 0.964985 | 154 ms | Pass |
| [Oxaliplatin, `00075769`](https://pdf.hres.ca/dpd_pm/00075769.PDF) | `0a6e96b2ee9428a320220d90d208b7ee0ded1a419b221f01270885536a7547fe` | 37 | 826,561 | 0.995584 | 0.972450 | 179 ms | Pass |

The baseline was recorded on 2026-08-09 with .NET SDK 8.0.423. `baseline-results.json` contains the complete machine-readable report. Wall time includes launching the .NET process; internal time covers connector execution. Timing is informational except for the hard 120-second platform ceiling.

## Metrics

Text is Unicode NFKC-normalized and case-folded, page markers are removed, and word tokens retain internal apostrophes and hyphens.

- **Token precision/recall/F1** use multiset token overlap. This measures content completeness without punishing harmless line-wrap differences.
- **Ordered-token ratio** uses Python's `difflib.SequenceMatcher`. This exposes reading-order mistakes that multiset overlap would miss.
- Every fixture must reach F1 `0.975`, ordered-token ratio `0.94`, and connector wall time below 120 seconds.

This is a deliberately demanding comparison: PyMuPDF4LLM is a mature native PDF stack, while the candidate is a single dependency-free C# custom-code file constrained to the Power Automate runtime.

## Run

From the repository root:

```bash
python -m venv .venv
. .venv/bin/activate
pip install -r connectors/PdfTextExtractor/benchmarks/requirements.txt
dotnet build testing/PdfTextExtractorRunner/PdfTextExtractorRunner.csproj -c Release
python connectors/PdfTextExtractor/benchmarks/benchmark.py \
  --runner-dll testing/PdfTextExtractorRunner/bin/Release/net8.0/PdfTextExtractorRunner.dll
```

Use `--fixture <id>` to run one pinned document. The default is `all`.
