# NIHB Drug Benefit List

This connector wraps the public NIHB Drug Benefit List endpoints hosted at `nihbservices.express-scripts.ca`.

It provides three operations:

| Operation | Description |
|-----------|-------------|
| `ListDrugBenefitListCsvDocuments` | Lists the CSV documents currently visible through the public NIHB document search endpoint. At the moment this appears to be the latest active CSV only. |
| `DownloadDrugBenefitListCsv` | Downloads the latest visible NIHB Drug Benefit List CSV, or a specific visible `documentId`, and can return the payload as base64 or plain text. |
| `SearchDrugBenefits` | Calls the NIHB `getOnlineDrugBenefitList` endpoint with a DIN or other search criteria and returns the matching items. |
| `HydrateDrugBenefitList` | Starts from the latest CSV, extracts distinct DINs, fetches detail records for up to 100 seconds, and returns both fetched records and `remainingItemNumbers` for the next run. |

## Prerequisites

- No authentication is required for the public NIHB endpoints used by this connector.
- Validate the connector manually in a Power Automate development environment and in the connector Test tab.

## Notes

- `DownloadDrugBenefitListCsv` accepts `responseFormat=Text` or `responseFormat=Base64`. `Text` avoids base64 expansion and usually produces the smaller response body. `Base64` remains useful when you want to write the file as binary.
- `DownloadDrugBenefitListCsv` also accepts an optional `documentId`, but only for documents currently visible in the public NIHB search response.
- The CSV currently contains a disclaimer row followed by the header row. The script detects the `DIN` column dynamically instead of assuming a fixed line number.
- A recent live check of the public CSV returned 9,698 distinct DIN values.
- A recent live check of the public document search response exposed one visible CSV document. The connector includes `ListDrugBenefitListCsvDocuments` so if NIHB later exposes older CSV versions through the same endpoint, you can discover them and then download one by `documentId`.
- The bulk hydration action uses a 100 second safety window so it stays under the platform timeout. Call the same action again with the previous run's `remainingItemNumbers` until that array is empty.
- The bulk hydration response can be large. If your flow stores every batch, append the records to external storage between runs instead of keeping everything in a single in-memory array variable.

## Example Flow Pattern

1. Run `HydrateDrugBenefitList` with an empty body.
2. Process or persist the returned `records`.
3. If `remainingItemNumbers` is not empty, run `HydrateDrugBenefitList` again and pass that array into the request body.
4. Repeat until `remainingItemNumbers` is empty.

## Implementation Notes

- `searchPublicDoc` is used to locate the latest active CSV document.
- `doc` returns the CSV as base64 in `Data[0]`.
- `getOnlineDrugBenefitList` returns a `status/data[]` envelope for item detail lookups.
- `operationId` values, `apiProperties.json` `scriptOperations`, and `script.csx` routing are kept aligned for deployment.