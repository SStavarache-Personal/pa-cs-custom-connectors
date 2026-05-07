---
name: power-automate-connector-deployment
description: 'Deploy or update a Power Automate custom connector from this repo using deploy-connector.ps1, pac CLI, or the manual portal flow. Use when validating connector files, authenticating pac, handling create-vs-update behavior, or wiring scriptOperations in the Code tab.'
argument-hint: 'Connector name, target environment, optional solution, and whether to use scripted or manual deployment'
---

# Power Automate Connector Deployment

## When to Use

- Deploy a connector in this repository with `deploy-connector.ps1`
- Update an existing connector after editing Swagger or script files
- Recover from authentication, connector-exists, or manual portal issues
- Walk through the portal deployment flow when CLI deployment is not enough

## Procedure

1. Run a preflight check.
   - Confirm the connector folder exists under `connectors/`.
   - Confirm `apiDefinition.swagger.json` or `apiDefinition.swagger.yaml`, `apiProperties.json`, and `script.csx` exist.
   - Confirm `.env` values, `pac` authentication, and optional solution settings are ready.
2. Choose the deployment path.
   - Use `deploy-connector.ps1` by default for repo-managed deployment.
   - Use the Power Automate portal when you need to inspect imported actions, confirm code-tab selections, or work around CLI limits.
3. For scripted deployment, run the repo command.
   - `./deploy-connector.ps1 -ConnectorName "Name"`
   - Add `-Environment` and `-SolutionUniqueName` when needed.
   - If the connector already exists, let the script fall back to update behavior or provide `-ConnectorId` explicitly.
4. Handle the common branches.
   - If `pac` authentication is expired, rerun `pac auth create` and retry.
   - If the connector already exists, let the script look it up and update it.
   - If file validation fails, fix the missing or mismatched connector files before retrying.
5. For manual portal deployment, import and verify carefully.
   - Import `apiDefinition.swagger.json`.
   - Paste `script.csx` into the Code tab.
   - Select every operation listed in `apiProperties.json` `scriptOperations`.
   - Save the connector before testing.
6. Validate after deployment.
   - Create a connection if required.
   - Test representative operations from the Power Automate Test tab.
   - Confirm the observed response shape matches the connector README examples or documented expectations.

## Decision Points

- Use the script first when the goal is to deploy a repo folder exactly as stored.
- Use the portal when you need to manually confirm action metadata, security configuration, or code-tab operation selection.
- For public scraping connectors like `InesssMedicaments`, choose "No authentication" and make sure the imported host is the real external host.

## Completion Checks

- The connector saves or updates successfully.
- The Code tab is enabled for the intended operations.
- A representative test operation succeeds in the target environment.
- Any environment-specific security configuration has been recreated after import.

## References

- [Deployment workflow](./references/deployment-workflow.md)
- [Deployment troubleshooting](./references/deployment-troubleshooting.md)