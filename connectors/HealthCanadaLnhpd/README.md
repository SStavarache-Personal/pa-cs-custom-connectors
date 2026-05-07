# Health Canada LNHPD

This connector surfaces Health Canada's Licensed Natural Health Products Database endpoints and adds bulk-friendly helpers for Power Automate.

## Actions

- `GetMedicinalIngredients`: Direct query for medicinal ingredients with optional `id`, `page`, `lang`, and JSON/CSV output selection.
- `GetNonMedicinalIngredients`: Direct query for non-medicinal ingredients by LNHPD product identifier with JSON/CSV output selection.
- `GetProductDoses`: Direct query for product doses by LNHPD product identifier with JSON/CSV output selection.
- `GetProductLicences`: Direct query for product licences by licence number with JSON/CSV output selection.
- `GetProductPurposes`: Direct query for product purposes with optional `id`, `page`, `lang`, and JSON/CSV output selection.
- `GetProductRisks`: Direct query for product risks with optional `id`, `page`, `lang`, and JSON/CSV output selection.
- `GetProductRoutes`: Direct query for product routes by LNHPD product identifier with JSON/CSV output selection.
- `BulkMedicinalIngredients`: Fetch multiple medicinal ingredient records by ID list or by page range/time-sliced pagination.
- `BulkProductPurposes`: Fetch multiple product purpose records by ID list or by page range/time-sliced pagination.
- `BulkProductRisks`: Fetch multiple product risk records by ID list or by page range/time-sliced pagination.
- `BulkProductLicences`: Fetch product licence data by licence-number list or full-dataset export, with optional `If Revised Since` filtering and JSON/CSV output.

## Response shape

All actions normalize the Health Canada responses into a consistent envelope:

- `endpoint`: Source endpoint name.
- `lang`: Requested language.
- `format`: `json` or `csv`.
- `itemCount`: Number of returned records.
- `metadata`: Upstream metadata when the API provides it.
- `data`: Array of records when `format=json`.
- `csvContent`: CSV text when `format=csv`.

Bulk actions also return a `summary` object with pagination progress, page coverage, completion status, and elapsed time. For paginated bulk actions, when `End Page` is omitted the connector stops after roughly 100 seconds and returns `summary.nextStartPage` for the next call.

## Notes

- The upstream API's `type` parameter is fixed to JSON in this connector.
- `BulkProductLicences` is intended for large exports. CSV is usually the safer format for full extracts.
- The upstream API is inconsistent about envelopes. The connector normalizes bare arrays and bare objects into a common `data` array.