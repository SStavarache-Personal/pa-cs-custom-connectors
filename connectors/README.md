# Connectors Directory

This folder contains all custom connectors for Power Automate.

## Structure

Each connector has its own folder:

```
connectors/
├── ConnectorName/
│   ├── README.md                    # Documentation
│   ├── apiDefinition.swagger.yaml   # OpenAPI 2.0 definition
│   ├── script.csx                   # C# custom code
│   └── icon.png                     # Optional icon
```

## Creating a New Connector

1. Copy the `_template` folder from the repository root
2. Rename it to your connector name (PascalCase)
3. Move it into this `connectors/` directory
4. Update all files with your implementation

## Available Connectors

| Connector | Description | Status |
|-----------|-------------|--------|
| *None yet* | Add connectors here | - |

---

*Update this table as connectors are added to the repository.*
