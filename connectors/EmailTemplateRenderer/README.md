# Email Template Renderer

Render simple email templates with `{{VariableName}}` placeholders directly inside Power Automate custom connector code.

## Operation

| Operation | Description |
|-----------|-------------|
| `RenderEmailTemplate` | Validates required variables, renders the subject/body/deduplication key templates, and returns either the rendered output or a structured validation error. |

## Request contract

```json
{
  "subjectTemplate": "Request {{RecordId}} is ready for review",
  "bodyHtmlTemplate": "<p>Hello {{RecipientDisplayName}}, request {{RecordId}} is ready.</p>",
  "deduplicationKeyTemplate": "{{RecordId}}::{{NotificationType}}::v{{SourceVersion}}",
  "variableDefinition": {
    "required": [
      "RecordId",
      "NotificationType",
      "SourceVersion",
      "RecipientDisplayName"
    ],
    "optional": [
      "AssignedToDisplayName",
      "ManagerEmail",
      "RequestDueDate"
    ]
  },
  "context": {
    "RecordId": "12345",
    "NotificationType": "RequestReadyForReview",
    "SourceVersion": "18",
    "RecipientDisplayName": "Jane Smith"
  }
}
```

## Success response

```json
{
  "subject": "Request 12345 is ready for review",
  "bodyHtml": "<p>Hello Jane Smith, request 12345 is ready.</p>",
  "deduplicationKey": "12345::RequestReadyForReview::v18"
}
```

## Validation error response

```json
{
  "error": {
    "code": "TEMPLATE_VALIDATION_FAILED",
    "message": "Template validation failed.",
    "details": {
      "missingRequiredVariables": [
        "RecipientDisplayName"
      ],
      "unresolvedPlaceholders": [
        {
          "templateField": "bodyHtmlTemplate",
          "placeholder": "RecipientDisplayName",
          "reason": "Variable is missing from the context object."
        }
      ]
    }
  }
}
```

## Notes

- Only direct placeholder replacement is supported.
- Placeholders must use `{{VariableName}}` syntax.
- Required variables are validated even if they are not referenced by the templates.
- Missing placeholders are reported as unresolved, including placeholders that are not declared in `variableDefinition`.
