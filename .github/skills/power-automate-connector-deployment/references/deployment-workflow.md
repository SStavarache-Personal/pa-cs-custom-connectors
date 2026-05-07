# Deployment Workflow

## Scripted Path

Preferred command:

```powershell
.\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced"
```

Useful variants:

```powershell
.\deploy-connector.ps1 -ConnectorName "Name" -Environment "guid-or-url"
.\deploy-connector.ps1 -ConnectorName "Name" -SolutionUniqueName "SolutionName"
.\deploy-connector.ps1 -ConnectorName "Name" -ConnectorId "connector-guid"
```

What the script already does:

- validates required connector files
- prefers `apiDefinition.swagger.json` and falls back to YAML when needed
- loads `.env`
- attempts create, then falls back to update when the connector already exists
- retries after `pac auth create` when the token is expired

## Manual Portal Path

1. Open Power Automate.
2. Create a new custom connector from an OpenAPI file.
3. Import `apiDefinition.swagger.json`.
4. Review general settings and security.
5. On the Code tab, paste `script.csx`.
6. Select operations matching `apiProperties.json` `scriptOperations`.
7. Save the connector.
8. Create a connection and test representative operations.

## Example Notes

- `InesssMedicaments` deploys with no authentication and a real external host.
- `RegexExtractor` is a good smoke-test connector shape because each operation is easy to test with small request bodies.