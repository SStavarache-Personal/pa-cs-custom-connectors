# Repository Guidelines

## Scope
- This repo stores Power Automate custom connectors under `connectors/<ConnectorName>/`; `_template/` is the scaffold for new connectors.
- Use this file as the single always-on instruction surface for the workspace. Keep detailed procedures in skills or docs.

## Use Existing Workflows
- Load `.github/skills/power-automate-connector-authoring/SKILL.md` before creating or refactoring a connector.
- Load `.github/skills/power-automate-connector-deployment/SKILL.md` before validating or deploying with `deploy-connector.ps1`, `pac`, or the portal.

## Commands Agents Should Reach For
- `Get-Help .\deploy-connector.ps1 -Detailed`
- `pac auth create`
- `.\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced"`
- There is no automated test suite in this repo; validate manually in a Power Automate dev environment and in the connector Test tab.

## Non-Negotiable Constraints
- Keep each connector folder internally consistent: `apiDefinition.swagger.json`, `apiProperties.json`, `script.csx`, and `README.md` must describe the same operations.
- OpenAPI definitions stay in OpenAPI 2.0 JSON, and Swagger `info.title` must stay within 30 characters.
- `script.csx` must define `public class Script : ScriptBase` and implement `public override async Task<HttpResponseMessage> ExecuteAsync()`.
- Keep Swagger `operationId`, `apiProperties.json` `scriptOperations`, and `ExecuteAsync()` routing aligned.
- Use `Context.SendAsync` for outbound HTTP work and stay inside Power Automate runtime limits.
- Deployment docs reference `.env.example`, but the template is not tracked in this repo; do not assume it exists locally.

## References
- Runtime limits and supported namespaces: `docs/PLATFORM_LIMITATIONS.md`
- Naming rules: `docs/NAMING_CONVENTIONS.md`
- OpenAPI structure and examples: `docs/SWAGGER_GUIDE.md`
- Manual deployment flow: `docs/DEPLOYMENT_GUIDE.md`
- Example connectors: `connectors/RegexExtractor/` and `connectors/InesssMedicaments/`
