# AI Agent Reference

This repository now keeps its detailed AI-agent workflows in workspace skills.

## Use These Skills

- `.github/skills/power-automate-connector-authoring/` for creating or updating a connector end to end
- `.github/skills/power-automate-connector-deployment/` for deploying a connector with the repo script, `pac`, or the portal

## Always-On Constraints

- Use `apiDefinition.swagger.json` for OpenAPI 2.0 definitions
- Include `apiProperties.json`, `script.csx`, and `README.md` in every connector folder
- Keep `operationId` values and `apiProperties.json` `scriptOperations` in sync
- Implement `public class Script : ScriptBase` with `ExecuteAsync()` returning `Task<HttpResponseMessage>`
- Use `Context.SendAsync` for outbound HTTP requests
- Stay inside Power Automate limits: one script file, .NET Standard 2.0-compatible namespaces, 1 MB max script size, 2 minute execution timeout
- Validate manually in a Power Automate dev environment because there is no automated test suite in this repo

## Repo References

- `connectors/RegexExtractor/` shows a well-documented multi-operation transformation connector
- `connectors/InesssMedicaments/` shows a public scraping connector with real host settings, pagination, and deployment notes
- `deploy-connector.ps1` is the primary automation path for deployment

This file is an index, not the primary workflow document. Keep new procedural guidance in skills.