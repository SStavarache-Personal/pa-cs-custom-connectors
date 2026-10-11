# Deployment and operations

This connector follows the repository's standard deployment workflow (see `docs/DEPLOYMENT_GUIDE.md`, `DEPLOYMENT.md` and `.github/skills/power-automate-connector-deployment/`). This page covers what is specific to it.

## Status of this release

| Item | Status |
| --- | --- |
| Connector files (`apiDefinition.swagger.json`, `apiProperties.json`, `script.csx`) | Import-ready |
| Script compiles against .NET Standard 2.0 / C# 7.3 and Microsoft's documented script interface | Verified locally (`testing/DocxTemplateGeneratorCompile`) |
| Script size (≈127 KB of the 1 MB limit), allowed namespaces, operation alignment | Verified by the repository harness |
| Functional matrix, Open XML SDK validation of every generated package | Verified locally (`dotnet test`) |
| Layout of generated samples | Rendered with LibreOffice only |
| **Import into a Power Platform environment, Test tab calls, cloud-flow run, Power Apps call** | **Not yet performed.** No tenant access was available while building this release. |
| **Opening outputs in desktop Word (no repair prompt, checkbox toggling, save/reopen)** | **Not yet performed.** Use the review samples. |

Until the [tenant verification checklist](#tenant-verification-checklist) is complete, treat the connector as *import-ready and locally verified*, not *production-verified*.

## Supported environments

- Any Power Platform environment where custom connectors with **custom code** can be created. Custom code is not available through the on-premises data gateway, and this connector does not need one.
- Callers: Power Automate cloud flows, and Power Apps canvas apps wherever custom connectors are allowed.
- **Virtual network environments:** the connector makes no outbound calls, so the `Context.SendAsync` restriction documented for virtual network environments does not apply.

## Licensing and DLP

- Custom connectors are **premium**. Flow owners and app users need licences that include premium connectors (Power Automate Premium/Process, Power Apps Premium/per-app, or pay-as-you-go). Confirm with your licensing administrator.
- DLP: an administrator must classify **DOCX Template Generator** in the same data group as the connectors it is combined with (normally **Business**, alongside SharePoint/OneDrive). Otherwise flows that use both are blocked. New custom connectors start in the policy's default group.
- The connector has no connection credentials (**No authentication**). It cannot reach any data the caller has not already passed into it.

## Install or update

### Scripted (recommended)

```powershell
pac auth create                      # once per tenant/user
.\deploy-connector.ps1 -ConnectorName "DocxTemplateGenerator" -Environment "<env-guid-or-url>" [-SolutionUniqueName "<solution>"]
```

The script uploads `apiDefinition.swagger.json`, `apiProperties.json` (whose `scriptOperations` select the three operations for custom code) and `script.csx`. It creates the connector, or updates it when it already exists. As the repository README notes, the deploy script reads `.env`, and `.env.example` is not tracked in this repository: create `.env` yourself, or pass `-Environment` and `-SolutionUniqueName`.

### Portal

1. **Custom connectors → New custom connector → Import an OpenAPI file**: name it, then upload `apiDefinition.swagger.json`.
2. **General:** keep host `api.example.com` and base URL `/`. Because custom code handles all three operations and never forwards the request, nothing is sent to that host. It is the repository's reserved placeholder convention for transformation connectors, and `example.com` is reserved by RFC 2606.
3. **Security:** **No authentication**.
4. **Definition:** confirm the three actions `GenerateDocument`, `ValidateTemplate` and `ListPlaceholders`.
5. **Code:** turn **Code** on, paste the entire `script.csx`, and select **all three operations**. An operation left unselected would be forwarded to the placeholder host and fail. The connector would not work, but no document data would be processed by anything else.
6. **Create connector.** If saving fails with an internal server error, the code usually did not compile. Rebuild locally with `dotnet build testing/DocxTemplateGeneratorCompile/DocxTemplateGeneratorCompile.csproj`.
7. **Test:** create a connection (there are no credentials), then run `ListPlaceholders` with the base64 of `examples/ProductSummaryTemplate.docx`.

### Solutions and environments

For dev → test → prod, add the connector to a solution and move it with managed solutions. Custom code is part of the connector definition and travels with the solution. This repository does not package a solution artifact, so no solution file is included.

## Timeouts and performance

Microsoft's current custom-code reference documents a **2-minute** limit for newly created connectors and a 1 MB script, and says existing connectors must be updated to get the new timeout. An older tutorial still mentions **5 seconds**. Settle this in the target tenant during verification:

1. Generate the example template, then a 1,000-row document, and record the action durations shown in the flow run history.
2. If an older connector was updated in place and calls fail at about 5 seconds, recreate the connector (or update it, as Microsoft describes) so the 2-minute limit applies.

The script stops itself after **90 seconds** and returns `RESOURCE_LIMIT_EXCEEDED` (HTTP 413), so a slow request ends with a clear error instead of a platform timeout. Locally, the common example takes about 44 ms and 1,000 rows about 0.5 s, comfortably within 5 seconds. Sandbox performance will be slower and must be measured.

Payloads: the template (≤ 5 MiB) arrives base64-encoded inside JSON, and the output is base64 as well (≤ 20 MiB, so ≈ 27 MiB as text). Check that your flow's actions and any Power Apps calls accept the sizes you need. Large-document limits of the Power Platform request path are **not** documented and were not measured. To change the caps, edit the `Limits` class in `script.csx`, run the tests, and redeploy.

## Tenant verification checklist

Record results, dates and environment names in `IMPLEMENTATION_NOTES.md` under *Verification log*.

1. Import with the scripted or portal path. The connector saves without an internal server error.
2. **Test tab:** `ListPlaceholders` with the example template returns the fields listed in `README.md`. `ValidateTemplate` with `examples/ProductSummary.data.json` returns `valid: true`.
3. **Test tab:** `GenerateDocument` with the same data returns HTTP 200. Decode `fileBase64` and open it in **desktop Word**:
   - no repair or "unreadable content" prompt,
   - headers on page 1, page 2 and the landscape appendix,
   - three product rows with styling,
   - checkboxes that can be toggled, saved and reopened.
4. **Cloud flow:** SharePoint Get file content → Generate document → SharePoint Create file. Open the saved file in Word desktop and Word for the web. Record which `templateBase64` mapping worked (direct **File content** or `?['$content']`) and correct [INTEGRATION.md](INTEGRATION.md) if needed.
5. **Error path:** remove `CompanyName` from the data. The action fails with 422 `MISSING_FIELD`, and the error body is readable in the run history.
6. **Timing and size:** run the 1,000-row case and a near-limit template, and record durations and any platform payload errors.
7. **Power Apps** (if licensed): call `GenerateDocument` directly and through a flow.
8. Confirm in the run history that no outbound HTTP calls appear and the connection has no credentials.

## Rollout and rollback

- **Rollout:** deploy to a test environment, complete the checklist, then promote through your solution pipeline. Keep the previous `script.csx` and Swagger (the git tag or commit) for rollback.
- **Template changes** need no redeployment. Validate new templates with `ValidateTemplate` before replacing the production template file.
- **Rollback:** redeploy the previous commit's three files with `deploy-connector.ps1 -ConnectorId <id>`, or paste the previous `script.csx` into the Code tab. The action contract is versioned in the Swagger `info.version`. Additive changes keep existing flows working; a breaking change requires a new major version or a new connector.

## Operator troubleshooting

| Symptom | Likely cause | Action |
| --- | --- | --- |
| Internal server error when saving the connector | Script did not compile in the sandbox | Build `testing/DocxTemplateGeneratorCompile`; make sure the whole file was pasted |
| Action returns `UNKNOWN_OPERATION` | Operation renamed in Swagger, or code not selected for that operation | Re-import the Swagger and select all three operations on the Code tab |
| Action fails at the placeholder host / DNS error | Code not enabled for that operation | Enable code and select the operation |
| `INVALID_BASE64` | Wrong value mapped (file path, URL, or double-encoded base64) | Map the **File content** output or `?['$content']` |
| Saved file will not open / is text | `base64ToBinary()` missing on Create file | Use `base64ToBinary(...fileBase64)` |
| `MISSING_FIELD`, `TYPE_MISMATCH` | Data does not match the template | Run `ValidateTemplate` with the payload; see the template guide's diagnosis table |
| `UNSUPPORTED_TEMPLATE_STRUCTURE`, `INVALID_PLACEHOLDER` | Template authoring | Use `field` and `location`; fix in Word |
| `RESOURCE_LIMIT_EXCEEDED` (413) | Template, data, row count, output or time cap hit | Reduce input, or raise the cap in `Limits` after measuring |
| `RENDER_FAILED` (500) | Internal fault or failed integrity check | Collect the `correlationId`, template and data (securely). Reproduce locally with `dotnet test`. |
| Flow blocked by DLP | Connector in a different DLP group than SharePoint | Ask the admin to classify it as Business |

Diagnostics never contain document text or data values. The script logs only an exception type and correlation ID on internal faults, through `Context.Logger`.
