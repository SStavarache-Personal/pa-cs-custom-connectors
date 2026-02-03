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
├── .ai/                                # AI agent instructions
│   └── INSTRUCTIONS.md                 # Rules for AI code generation
├── _template/                          # Template for new connectors
│   ├── README.md                       # Template documentation
│   ├── apiDefinition.swagger.yaml      # OpenAPI 2.0 template
│   └── script.csx                      # C# script template
└── connectors/                         # Individual connector folders
    └── <ConnectorName>/                # One folder per connector
        ├── README.md                   # Connector-specific documentation
        ├── apiDefinition.swagger.yaml  # OpenAPI 2.0 definition (Swagger)
        ├── script.csx                  # C# custom code script
        └── icon.png                    # Connector icon (optional)
```

---

## 🔧 What Each Connector Requires

Each connector folder **must** contain the following files for manual import into Power Automate:

| File | Description | Required |
|------|-------------|----------|
| `apiDefinition.swagger.yaml` | OpenAPI 2.0 (Swagger) definition file describing API endpoints, operations, and data models | ✅ Yes |
| `script.csx` | Single C# script file containing custom code logic | ✅ Yes |
| `README.md` | Connector documentation with purpose, usage, and configuration | ✅ Yes |
| `icon.png` | Connector icon (32x32 or 64x64 pixels, PNG format) | ⬜ Optional |

### Important Notes

- **Only ONE script file** per connector is supported by Power Platform
- **OpenAPI 2.0 format only** - OpenAPI 3.0 is NOT supported
- Script file must be **under 1 MB** in size
- Script execution must complete **within 2 minutes**

---

## 🚀 Manual Deployment (Copy-Paste Method)

### Step 1: Create the Custom Connector

1. Sign in to [Power Automate](https://make.powerautomate.com)
2. Navigate to **Data** → **Custom connectors**
3. Select **New custom connector** → **Import an OpenAPI file**
4. Name your connector and upload the `apiDefinition.swagger.yaml` file
5. Click **Continue**

### Step 2: Add the C# Script

1. In the connector wizard, navigate to the **Code** tab
2. Enable **Code** toggle
3. Copy the entire contents of `script.csx`
4. Paste into the code editor
5. Select operations that should use the custom code

### Step 3: Create and Test Connection

1. Navigate to the **Test** tab
2. Create a new connection
3. Test each operation to verify functionality

---

## 📋 Quick Links

| Document | Description |
|----------|-------------|
| [Platform Limitations](docs/PLATFORM_LIMITATIONS.md) | Supported namespaces, execution limits, and restrictions |
| [Naming Conventions](docs/NAMING_CONVENTIONS.md) | C# and file naming standards |
| [Swagger Guide](docs/SWAGGER_GUIDE.md) | OpenAPI 2.0 specification requirements |
| [Deployment Guide](docs/DEPLOYMENT_GUIDE.md) | Step-by-step manual deployment |
| [AI Instructions](.ai/INSTRUCTIONS.md) | Guidelines for AI agents creating connectors |

---

## 🤖 For AI Agents

If you are an AI agent creating new connectors for this repository, **read [.ai/INSTRUCTIONS.md](.ai/INSTRUCTIONS.md) first**. This file contains:

- Allowed C# namespaces
- Required class structure
- Naming conventions
- File templates
- Platform constraints

---

## 📜 License

This repository is for internal/educational use. Individual connectors may connect to third-party APIs with their own licensing terms.

---

## 🔗 Official Documentation

- [Write code in a custom connector](https://learn.microsoft.com/en-us/connectors/custom-connectors/write-code)
- [Create a custom connector from scratch](https://learn.microsoft.com/en-us/connectors/custom-connectors/define-blank)
- [OpenAPI 2.0 Specification](https://spec.openapis.org/oas/v2.0.html)
- [Connector coding standards](https://learn.microsoft.com/en-us/connectors/custom-connectors/coding-standards)
