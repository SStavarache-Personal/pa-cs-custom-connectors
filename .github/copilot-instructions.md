# Power Automate C# Custom Connectors - AI Agent Guidelines

This repository stores Power Automate custom connectors implemented with four core files: `apiDefinition.swagger.json`, `apiProperties.json`, `script.csx`, and `README.md`.

Use the workspace skills for the detailed workflows instead of duplicating those steps in always-on instructions:

- `.github/skills/power-automate-connector-authoring/` for creating or updating connectors
- `.github/skills/power-automate-connector-deployment/` for deploying with `deploy-connector.ps1`, `pac`, or the portal

Non-negotiable repository rules:

- OpenAPI definitions use OpenAPI 2.0 and should be stored as `apiDefinition.swagger.json`
- Every connector must include `apiProperties.json` with `scriptOperations` aligned to implemented `operationId` values
- Every `script.csx` must define `public class Script : ScriptBase` and implement `public override async Task<HttpResponseMessage> ExecuteAsync()`
- Use `Context.SendAsync` for outbound HTTP calls
- Stay within the Power Automate runtime limits: one script file, .NET Standard 2.0-compatible namespaces, 1 MB script size, 2 minute execution timeout
- Keep Swagger `info.title` within 30 characters

Reference examples already used successfully in this repo:

- `connectors/RegexExtractor/` for a multi-operation transformation connector with strong request/response examples
- `connectors/InesssMedicaments/` for a public-site scraping connector with real host configuration, pagination, and deployment notes

Keep always-on guidance concise. Put repeatable, task-specific workflows in skills.