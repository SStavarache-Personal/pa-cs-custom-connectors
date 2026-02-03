# Connector Template

This folder contains templates for creating new Power Automate custom connectors with C# script code.

## Files

| File | Purpose |
|------|---------|
| `apiDefinition.swagger.yaml` | OpenAPI 2.0 definition template |
| `script.csx` | C# script template |

## How to Use

1. Copy this entire `_template` folder
2. Rename the folder to your connector name (PascalCase)
3. Move it to the `connectors/` directory
4. Update the files with your connector's implementation
5. Update this README with your connector's documentation

---

## Connector Documentation Template

### Overview

[Describe what this connector does and its purpose]

### Operations

| Operation | Description |
|-----------|-------------|
| `OperationName` | What this operation does |

### Prerequisites

- [List any required API keys, credentials, or setup]

### Configuration

[Explain any configuration needed]

### Examples

```
[Show example usage]
```

### Limitations

[Document any known limitations]
