# Connectors Directory

This folder contains all custom connectors for Power Automate.

## Structure

Each connector has its own folder:

```
connectors/
|-- ConnectorName/
|   |-- README.md                    # Documentation
|   |-- apiDefinition.swagger.yaml   # OpenAPI 2.0 definition
|   |-- script.csx                   # C# custom code
|   `-- icon.png                     # Optional icon
```

## Creating a New Connector

1. Copy the `_template` folder from the repository root
2. Rename it to your connector name (PascalCase)
3. Move it into this `connectors/` directory
4. Update all files with your implementation

## Available Connectors

| Connector | Description | Status |
|-----------|-------------|--------|
| [HttpRequestAdvanced](HttpRequestAdvanced/) | Enhanced HTTP connector with automatic redirect handling, custom headers, authentication, and full HTTP method support | Ready |
| [DatasetSqlQuery](DatasetSqlQuery/) | In-script SQL query execution over user-provided JSON/CSV datasets with joins, aggregates, windows, and multiple output modes | Ready |
| [EmailTemplateRenderer](EmailTemplateRenderer/) | Render subject, HTML body, and deduplication key templates with simple placeholder replacement and validation | Ready |
| [InessMedicaments](InessMedicaments/) | Scrapes INESSS medication evaluation tables (génériques, innovateurs, autres travaux, sollicitations, produits évalués with pagination) | Ready |
| [RegexExtractor](RegexExtractor/) | Extract, test, and replace text using regular expressions with support for named groups and common regex options | Ready |

---

*Update this table as connectors are added to the repository.*
