# Authoring Examples

Use these repo examples as working patterns instead of inventing a new structure from scratch.

## RegexExtractor

Best for: pure transformation connectors with several related operations.

Lessons to reuse:

- `apiDefinition.swagger.json` groups multiple operations around a single capability and keeps request and response definitions explicit.
- `apiProperties.json` cleanly mirrors all scripted operations.
- `script.csx` routes by decoded `operationId`, validates request data early, and returns consistent structured errors.
- `README.md` includes concrete request and response examples, which makes the connector easier to test in Power Automate.

Files to inspect:

- `connectors/RegexExtractor/apiDefinition.swagger.json`
- `connectors/RegexExtractor/apiProperties.json`
- `connectors/RegexExtractor/script.csx`
- `connectors/RegexExtractor/README.md`

## InesssMedicaments

Best for: public website scraping or connectors that call a real external host.

Lessons to reuse:

- `apiDefinition.swagger.json` uses the real host and models pagination explicitly.
- `script.csx` builds outbound requests with headers and uses `Context.SendAsync`.
- The implementation isolates fetch and parsing helpers so each operation stays focused.
- `README.md` documents output shape, source-column mapping, and deployment assumptions.

Files to inspect:

- `connectors/InesssMedicaments/apiDefinition.swagger.json`
- `connectors/InesssMedicaments/apiProperties.json`
- `connectors/InesssMedicaments/script.csx`
- `connectors/InesssMedicaments/README.md`

## EmailTemplateRenderer

Best for: single-operation, request-driven transformation connectors.

Lessons to reuse:

- Keep Swagger compact when one operation owns the entire connector.
- Validate request fields and report unresolved business rules in a structured error payload.

Files to inspect:

- `connectors/EmailTemplateRenderer/apiDefinition.swagger.json`
- `connectors/EmailTemplateRenderer/script.csx`