# Deployment Troubleshooting

## Missing files

If validation reports a missing API definition, `apiProperties.json`, or `script.csx`, stop and fix the connector folder contents before retrying.

## Expired authentication

Symptoms:

- `AADSTS70043`
- refresh token expired or invalid

Action:

```powershell
pac auth create
```

Then rerun `deploy-connector.ps1`.

## Connector already exists

Use one of these paths:

- let `deploy-connector.ps1` fall back from create to update
- pass `-ConnectorId` when you already know the target connector ID

## Manual portal issues

- No Code tab: verify environment and licensing support for custom code.
- Operation not using script: re-check the Code tab selections against `apiProperties.json`.
- Imported definition looks wrong: re-open `apiDefinition.swagger.json` and verify the host, paths, and summaries before reimporting.