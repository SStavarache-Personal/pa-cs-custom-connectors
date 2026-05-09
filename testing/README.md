# Connector Testing Workbench

This folder provides one shared local test system for the custom connectors in this repository.

It covers three execution modes:

1. `dotnet test` for repeatable programmatic validation.
2. `dotnet run --project testing/src/ConnectorTestWorkbench` for a Swagger-backed local workbench UI.
3. GitHub Actions for CI execution and test result publishing.

## Project layout

- `testing/src/ConnectorTestKit/`: shared discovery, validation, script compilation, runtime emulation, and test case runner.
- `testing/src/ConnectorTestWorkbench/`: local web UI that rewrites each connector's Swagger 2.0 document to target the local runtime harness and surfaces seeded examples beside Swagger UI.
- `testing/tests/ConnectorTestKit.Tests/`: xUnit tests that validate connector constraints and execute saved scenarios.

## What gets validated

The harness validates every connector under `connectors/` for:

- required files present
- OpenAPI version is `2.0`
- `info.title` length stays within 30 characters
- `script.csx` stays under the 1 MB limit
- Swagger `operationId` values align with `apiProperties.json` `scriptOperations`
- the script appears to reference each scripted operation
- `using` directives stay within the repo's documented Power Automate namespace constraints

## Saved scenario format

Add connector-level test cases under `connectors/<ConnectorName>/tests/*.json`.

Example:

```json
{
  "name": "SearchDrugBenefits maps the upstream payload",
  "includeInAutomatedRun": true,
  "operationId": "SearchDrugBenefits",
  "method": "POST",
  "relativePath": "/dbl/items/search",
  "requestBody": {
    "itemNumber": "12345",
    "language": "en"
  },
  "outboundStubs": [
    {
      "method": "POST",
      "urlContains": "/elasticclient/api/v1/onlineDrugBenefit/getOnlineDrugBenefitList",
      "requestBodyContains": "12345",
      "response": {
        "statusCode": 200,
        "contentType": "application/json",
        "body": "{\"status\":true,\"data\":[{\"DIN\":\"12345\"}]}"
      }
    }
  ],
  "expected": {
    "statusCode": 200,
    "bodyJson": {
      "status": true,
      "count": 1
    },
    "jsonPathEquals": {
      "items[0].DIN": "12345"
    },
    "outboundRequestCount": 1
  }
}
```

### Notes

- Prefer stubbed outbound responses for CI stability.
- Use `allowLiveNetwork: true` only for deliberate local probing.
- Set `includeInAutomatedRun: false` for live-network examples that should appear in the local workbench but stay out of `dotnet test` and GitHub Actions.
- Every connector should carry at least one seeded example so the local workbench sidebar always has something runnable.
- `bodyJson` is a partial JSON subset assertion.
- `jsonPathEquals` uses JSONPath expressions against the final connector response.

## Local commands

Run the full connector suite:

```powershell
dotnet test testing/ConnectorTesting.sln
```

Run only the harness tests:

```powershell
dotnet test testing/tests/ConnectorTestKit.Tests/ConnectorTestKit.Tests.csproj
```

Start the local UI:

```powershell
dotnet run --project testing/src/ConnectorTestWorkbench/ConnectorTestWorkbench.csproj
```

By default the workbench is available on the local ASP.NET Core development URL shown in the terminal output.

## Workbench behavior

- The main panel is Swagger UI, generated from each connector's existing OpenAPI 2.0 spec.
- Swagger `Try it out` sends requests to the local harness under `/runtime/<ConnectorName>/...`.
- Saved examples are listed in the sidebar and can be run directly against the same harness.
- Public scraping connectors should include at least one live example with `includeInAutomatedRun: false` and `allowLiveNetwork: true` so manual validation stays one click away without destabilizing CI.

## Recommended test strategy per connector

- Add at least one offline or stubbed happy-path scenario per operation.
- Add one failure-path scenario for input validation or upstream error handling.
- For scraping or live API connectors, stub upstream responses in CI and keep live probing manual.
- For public scraping connectors, add at least one `includeInAutomatedRun: false` live example so the workbench can exercise the real source.
- Keep assertions focused on the connector contract, not incidental formatting.

## CI contract

The GitHub Actions workflow in `.github/workflows/connector-tests.yml` runs:

- restore
- build
- connector harness tests with TRX output
- test result publication in the GitHub Checks UI
- artifact upload for TRX and coverage/debug outputs