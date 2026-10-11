# Changelog

## 1.0.0 — 2026-10-11

Initial release.

- `GenerateDocument`, `ValidateTemplate` and `ListPlaceholders` actions, implemented entirely in custom code with no outbound requests.
- Text tags with run-fragmentation support, nested paths, `Root.` scope, deterministic value formatting, line breaks and tabs.
- Repeated table rows (0…1,000 items) in the body, headers and footers; Boolean table rows and paragraph blocks, including nesting and per-item conditions.
- Word checkbox content controls bound through their Tag, including inside repeated rows, with unique control IDs.
- Strict and non-strict missing-value handling, structured diagnostics with part, field and location, and resource limits.
- Package hardening (ZIP bomb, path traversal, duplicate and encrypted entries, DTDs, macro documents) and an integrity check on every generated output.
- Example template, independent cross-check fixtures, test matrix, compile project and documentation.
