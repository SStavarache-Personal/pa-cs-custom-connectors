---
description: "Use when creating or editing Power Automate connector assets such as apiDefinition.swagger.json, apiProperties.json, script.csx, or connector README files. Covers OpenAPI 2.0 constraints, operationId/scriptOperations alignment, runtime limits, and reference patterns in this repo."
applyTo:
  - "connectors/**/apiDefinition.swagger.json"
  - "connectors/**/apiProperties.json"
  - "connectors/**/script.csx"
  - "connectors/**/README.md"
  - "_template/apiDefinition.swagger.json"
  - "_template/apiProperties.json"
  - "_template/script.csx"
  - "_template/README.md"
---

# Connector Asset Guidelines

- Treat each connector folder as one deployable unit: keep `apiDefinition.swagger.json`, `apiProperties.json`, `script.csx`, and `README.md` aligned.
- OpenAPI files must remain OpenAPI 2.0 JSON. Keep `info.title` within 30 characters. See `docs/SWAGGER_GUIDE.md`.
- Keep `operationId` values in PascalCase, implement matching handlers in `ExecuteAsync()`, and list every scripted operation in `apiProperties.json` `scriptOperations`.
- `script.csx` must use `public class Script : ScriptBase`, return `Task<HttpResponseMessage>`, and call `Context.SendAsync` for outbound HTTP work.
- Stay within Power Automate limits: one script file, .NET Standard 2.0-compatible namespaces, 1 MB maximum script size, and 2 minute execution timeout. See `docs/PLATFORM_LIMITATIONS.md`.
- There is no automated test suite in this repo. Validate connector behavior manually in a Power Automate dev environment and in the connector Test tab.
- Reuse repository examples instead of inventing patterns: `connectors/RegexExtractor/` for multi-operation transformations and `connectors/InesssMedicaments/` for scraping, pagination, and host configuration.
- For full workflows, load `.github/skills/power-automate-connector-authoring/SKILL.md` or `.github/skills/power-automate-connector-deployment/SKILL.md` instead of duplicating procedures here.