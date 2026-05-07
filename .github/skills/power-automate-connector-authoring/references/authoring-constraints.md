# Authoring Constraints

Use these rules when creating or updating connectors in this repository.

- OpenAPI version: 2.0 only
- Preferred definition file: `apiDefinition.swagger.json`
- Required connector files: `apiDefinition.swagger.json`, `apiProperties.json`, `script.csx`, `README.md`
- Required script shape:
  - `public class Script : ScriptBase`
  - `public override async Task<HttpResponseMessage> ExecuteAsync()`
- Outbound HTTP must use `Context.SendAsync`
- Power Automate runtime constraints:
  - one script file per connector
  - roughly .NET Standard 2.0-compatible namespaces only
  - no NuGet packages
  - 1 MB max script size
  - 2 minute execution timeout
- Naming rules:
  - connector folders in PascalCase
  - `operationId` values in PascalCase
  - async methods end with `Async`
  - private fields use `_camelCase`
- Quality gates:
  - `info.title` should stay within 30 characters
  - `apiProperties.json` `scriptOperations` must match the scripted operations
  - README should explain operations, examples, and deployment notes

Primary repo references:

- `AGENTS.md`
- `docs/NAMING_CONVENTIONS.md`
- `docs/SWAGGER_GUIDE.md`
- `_template/`