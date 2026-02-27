# Repository Guidelines

## Project Structure & Module Organization
- `connectors/`: One folder per connector (PascalCase), e.g. `connectors/HttpRequestAdvanced/`.
- `connectors/<ConnectorName>/`: Keep connector assets together:
  - `apiDefinition.swagger.json` (or `.yaml`) for OpenAPI 2.0
  - `apiProperties.json` for connector metadata/script bindings
  - `script.csx` for C# custom code
  - `README.md` for connector-specific docs
  - `icon.png` optional
- `_template/`: Starting point for new connectors.
- `docs/`: Standards and references (`SWAGGER_GUIDE.md`, `NAMING_CONVENTIONS.md`, etc.).
- `deploy-connector.ps1`: Primary deployment entry point.

## Build, Test, and Development Commands
- `copy .env.example .env`: Create local deployment config.
- `pac auth create`: Authenticate Power Platform CLI.
- `.\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced"`: Validate files and deploy connector.
- `.\deploy-connector.ps1 -ConnectorName "Name" -Environment "<env>" -SolutionUniqueName "<solution>"`: Override `.env` values.
- `Get-Help .\deploy-connector.ps1 -Detailed`: Full script options and examples.

## Coding Style & Naming Conventions
- Use 4-space indentation in `script.csx`; keep methods small and operation-focused.
- Required script shape:
  - `public class Script : ScriptBase`
  - `public override async Task<HttpResponseMessage> ExecuteAsync()`
- Route by `OperationId`; ensure `operationId` values match `ExecuteAsync()` handlers.
- Naming:
  - PascalCase: connector folders, classes, methods, OpenAPI `operationId`
  - camelCase: locals/parameters
  - `_camelCase`: private fields
  - `Async` suffix for async methods
- Platform rules: OpenAPI **2.0 only**; prefer `Context.SendAsync` over direct `HttpClient`.

## Testing Guidelines
- No automated test suite is configured; validate manually in a Power Automate dev environment.
- Before PR:
  - Confirm `operationId` <-> handler mapping in `script.csx`
  - Confirm `apiProperties.json` `scriptOperations` matches implemented operations
  - Run end-to-end tests in the connector **Test** tab (success and error cases)

## Commit & Pull Request Guidelines
- Follow existing history style: concise, imperative commits; optional Conventional Commit prefix (`feat:`).
- Keep commits scoped (one connector or doc concern per commit).
- PRs should include:
  - What changed and why
  - Paths touched (for example `connectors/HttpRequestAdvanced/`)
  - Validation evidence (manual test notes, request/response samples, or screenshots)
  - Any required `.env`/environment assumptions

## Security & Configuration Tips
- Do not commit secrets or populated `.env` values.
- Keep credentials in Power Platform connection settings, not in `script.csx`.
- Use production-safe defaults (for example SSL validation enabled unless explicitly required otherwise).
