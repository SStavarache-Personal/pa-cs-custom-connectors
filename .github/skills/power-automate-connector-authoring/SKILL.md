---
name: power-automate-connector-authoring
description: 'Create or update a Power Automate custom connector in this repo with OpenAPI 2.0 JSON, apiProperties.json, script.csx, and README.md. Use when adding a connector, refactoring connector files, mapping operationId to scriptOperations, or modeling work after RegexExtractor or InesssMedicaments.'
argument-hint: 'Connector name, scenario, operations, and whether to start from the template or an existing connector'
---

# Power Automate Connector Authoring

## When to Use

- Add a new connector under `connectors/`
- Refactor an existing connector to match repo conventions
- Translate a working idea into `apiDefinition.swagger.json`, `apiProperties.json`, `script.csx`, and `README.md`
- Reuse a proven pattern from `RegexExtractor`, `InesssMedicaments`, `HttpRequestAdvanced`, or `EmailTemplateRenderer`

## Procedure

1. Pick the closest starting point.
   - Start from `_template/` for a net-new connector.
   - Start from an existing connector when the behavior already matches one of these shapes: pure transformation, outbound HTTP/proxy, or HTML/data scraping.
2. Define the connector contract in `apiDefinition.swagger.json`.
   - Use OpenAPI 2.0 only.
   - Keep `info.title` at 30 characters or fewer.
   - Use PascalCase `operationId` values.
   - Add `summary`, `description`, and response schemas for every operation.
   - Prefer reusable definitions over repeating inline schemas once the payload gets non-trivial.
3. Mirror the contract in `apiProperties.json`.
   - Add every scripted operation to `properties.scriptOperations`.
   - Keep the file minimal unless connection settings are actually required.
4. Implement `script.csx` around the required runtime shape.
   - Use `public class Script : ScriptBase`.
   - Decode `Context.OperationId` before switching on it.
   - Branch to one handler per operation.
   - Parse and validate JSON requests early.
   - Use `Context.SendAsync` for outbound HTTP.
   - Return structured error payloads instead of throwing raw exceptions back to callers.
5. Write `README.md` for the connector.
   - List operations.
   - Show at least one representative request/response example when the connector transforms data.
   - Document limits, data-size assumptions, authentication expectations, and any deployment-specific notes.
6. Validate the connector before stopping.
   - Confirm the four core files exist.
   - Confirm every `operationId` is implemented in `script.csx`.
   - Confirm every implemented scripted operation appears in `apiProperties.json`.
   - Parse `apiDefinition.swagger.json` and `apiProperties.json` as valid JSON.
   - Check platform constraints: one script file, supported namespaces, timeout and size limits.

## Decision Points

- If the connector only transforms input data and does not call an external service, use a placeholder host like `api.example.com` and keep the script entirely request-driven.
- If the connector calls an external API or website, use the real host when Power Automate needs it for the imported definition, and centralize outbound request creation in helper methods.
- If the response shape repeats across operations, define shared response objects in Swagger and helper methods in the script.
- If the source system is scraped HTML, make the README explicit about selector assumptions, pagination, and why the parsing strategy is stable enough for production use.

## Completion Checks

- The connector folder contains `apiDefinition.swagger.json`, `apiProperties.json`, `script.csx`, and `README.md`.
- Swagger `operationId` values, `apiProperties.json` `scriptOperations`, and the `ExecuteAsync()` switch all match.
- The script stays within Power Automate runtime constraints.
- The README gives a future editor enough context to understand payloads and deployment expectations.

## References

- [Authoring constraints](./references/authoring-constraints.md)
- [Authoring examples](./references/example-patterns.md)