# Power Automate C# Custom Connectors Collection

A collection of Power Automate custom connectors with C# script code for enhanced functionality. This repository serves as a centralized hub for developing, documenting, and deploying custom connectors to Microsoft Power Platform.

---

## 📁 Repository Structure

```
pa-cs-custom-connectors/
├── README.md                           # This file - repository overview
├── .gitignore                          # Git ignore rules
├── docs/                               # Documentation
│   ├── PLATFORM_LIMITATIONS.md         # Platform constraints and limits
│   ├── NAMING_CONVENTIONS.md           # C# and file naming standards
│   ├── SWAGGER_GUIDE.md                # OpenAPI 2.0 specification guide
│   └── DEPLOYMENT_GUIDE.md             # Manual deployment instructions
├── .ai/                                # AI agent reference index
│   └── INSTRUCTIONS.md                 # Skill index and hard constraints
├── .github/
│   ├── copilot-instructions.md         # Concise always-on agent guidance
│   └── skills/                         # On-demand agent workflows
│       ├── power-automate-connector-authoring/
│       └── power-automate-connector-deployment/
├── _template/                          # Template for new connectors
│   ├── README.md                       # Template documentation
│   ├── apiDefinition.swagger.json      # OpenAPI 2.0 template
│   ├── apiProperties.json              # Connector metadata template
│   └── script.csx                      # C# script template
└── connectors/                         # Individual connector folders
    └── <ConnectorName>/                # One folder per connector
        ├── README.md                   # Connector-specific documentation
        ├── apiDefinition.swagger.json  # OpenAPI 2.0 definition (Swagger)
        ├── apiProperties.json          # Connector metadata and script bindings
        ├── script.csx                  # C# custom code script
        └── icon.png                    # Connector icon (optional)
```

---

## 🔧 What Each Connector Requires

Each connector folder **must** contain the following files for manual import into Power Automate:

| File | Description | Required |
|------|-------------|----------|
| `apiDefinition.swagger.json` | OpenAPI 2.0 (Swagger) definition file describing API endpoints, operations, and data models | ✅ Yes |
| `apiProperties.json` | Connector metadata and scripted operation bindings | ✅ Yes |
| `script.csx` | Single C# script file containing custom code logic | ✅ Yes |
| `README.md` | Connector documentation with purpose, usage, and configuration | ✅ Yes |
| `icon.png` | Connector icon (32x32 or 64x64 pixels, PNG format) | ⬜ Optional |

### Important Notes

- **Only ONE script file** per connector is supported by Power Platform
- **OpenAPI 2.0 format only** - OpenAPI 3.0 is NOT supported
- Script file must be **under 1 MB** in size
- Script execution must complete **within 2 minutes**

---

## 🚀 Deployment Options

### Option 1: Automated Deployment (Recommended)

Use the PowerShell deployment script with the Power Platform CLI:

```powershell
# 1. Configure your environment
copy .env.example .env
# Edit .env and add your POWER_PLATFORM_ENVIRONMENT and SOLUTION_UNIQUE_NAME

# 2. Authenticate to Power Platform (one-time setup)
pac auth create

# 3. Deploy a connector
.\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced"
```

**Prerequisites:**
- [Power Platform CLI](https://aka.ms/PowerPlatformCLI) installed
- Authenticated to Power Platform (`pac auth create`)
- `.env` file configured with environment settings

**Script Features:**
- ✅ Validates connector files before deployment
- ✅ Loads configuration from `.env` file
- ✅ Supports environment and solution overrides
- ✅ Deploys API definition, script, and optional icon
- ✅ Color-coded output with deployment status

**Command Options:**
```powershell
# Basic usage
.\deploy-connector.ps1 -ConnectorName "YourConnector"

# Override environment
.\deploy-connector.ps1 -ConnectorName "YourConnector" -Environment "env-guid-or-url"

# Override solution
.\deploy-connector.ps1 -ConnectorName "YourConnector" -SolutionUniqueName "MySolution"

# Override both
.\deploy-connector.ps1 -ConnectorName "YourConnector" -Environment "env-guid" -SolutionUniqueName "MySolution"
```

---

### Option 2: Manual Deployment (Portal Method)

If you prefer the UI or don't have CLI access:

#### Step 1: Create the Custom Connector

1. Sign in to [Power Automate](https://make.powerautomate.com)
2. Navigate to **Data** → **Custom connectors**
3. Select **New custom connector** → **Import an OpenAPI file**
4. Name your connector and upload the `apiDefinition.swagger.json` file
5. Click **Continue**

#### Step 2: Add the C# Script

1. In the connector wizard, navigate to the **Code** tab
2. Enable **Code** toggle
3. Copy the entire contents of `script.csx`
4. Paste into the code editor
5. Select operations that should use the custom code

#### Step 3: Create and Test Connection

1. Navigate to the **Test** tab
2. Create a new connection
3. Test each operation to verify functionality

See [DEPLOYMENT_GUIDE.md](docs/DEPLOYMENT_GUIDE.md) for detailed manual deployment steps.

---

## 📋 Quick Links

| Document | Description |
|----------|-------------|
| [Platform Limitations](docs/PLATFORM_LIMITATIONS.md) | Supported namespaces, execution limits, and restrictions |
| [Naming Conventions](docs/NAMING_CONVENTIONS.md) | C# and file naming standards |
| [Swagger Guide](docs/SWAGGER_GUIDE.md) | OpenAPI 2.0 specification requirements |
| [Deployment Guide](docs/DEPLOYMENT_GUIDE.md) | Step-by-step manual deployment |
| [Testing Workbench](testing/README.md) | Local harness, Swagger-based workbench UI, and CI workflow for connector testing |
| [AI Agent Reference](.ai/INSTRUCTIONS.md) | Skill index and hard constraints for AI agents |

---

## 🧪 Local Testing

This repo now includes a shared connector testing harness under `testing/`.

- Run all connector validation and saved scenarios with `dotnet test testing/ConnectorTesting.sln`
- Launch the local Swagger-based workbench UI with `dotnet run --project testing/src/ConnectorTestWorkbench/ConnectorTestWorkbench.csproj`
- Add seeded scenarios under `connectors/<ConnectorName>/tests/*.json`, using `includeInAutomatedRun: false` for live-only manual probes

See [testing/README.md](testing/README.md) for the scenario format and CI behavior.

---

## 🤖 For AI Agents

If you are an AI agent creating or deploying connectors in this repository, start with these on-demand skills:

- `.github/skills/power-automate-connector-authoring/`
- `.github/skills/power-automate-connector-deployment/`

Use [.ai/INSTRUCTIONS.md](.ai/INSTRUCTIONS.md) as the short index of hard constraints and entry points.

---

## 📜 License

This repository is for internal/educational use. Individual connectors may connect to third-party APIs with their own licensing terms.

---

## 🔗 Official Documentation

- [Write code in a custom connector](https://learn.microsoft.com/en-us/connectors/custom-connectors/write-code)
- [Create a custom connector from scratch](https://learn.microsoft.com/en-us/connectors/custom-connectors/define-blank)
- [OpenAPI 2.0 Specification](https://spec.openapis.org/oas/v2.0.html)
- [Connector coding standards](https://learn.microsoft.com/en-us/connectors/custom-connectors/coding-standards)
