# Regex Extractor

Custom connector for extracting, testing, and replacing text using regular expressions directly within Power Automate flows.

## Operations

| Operation | Description |
|-----------|-------------|
| `ExtractMatches` | Apply a regex pattern and return all matches with captured groups (named and numbered) |
| `TestPattern` | Test whether a pattern matches the input text; returns a boolean and the first matched value |
| `ReplaceMatches` | Replace regex matches in the text with a substitution string (supports backreferences) |
| `ExtractFrenchDates` | Extract French-format dates (e.g. "1er avril 2026") and return parsed day/month/year with ISO equivalents |

## Parameters

All operations accept:

| Parameter | Required | Description |
|-----------|----------|-------------|
| `text` | Yes | The input string to process |
| `pattern` | Yes | A .NET-compatible regular expression |
| `options` | No | Comma-separated regex options: `IgnoreCase`, `Multiline`, `Singleline`, `ExplicitCapture` |

The **ReplaceMatches** operation additionally requires:

| Parameter | Required | Description |
|-----------|----------|-------------|
| `replacement` | Yes | Replacement string; use `$1`, `$2`, or `${name}` for backreferences |

## Examples

### Extract email addresses

**Request:**
```json
{
  "text": "Contact us at alice@example.com or bob@test.org",
  "pattern": "[\\w.+-]+@[\\w-]+\\.[\\w.]+",
  "options": "IgnoreCase"
}
```

**Response:**
```json
{
  "matchCount": 2,
  "matches": [
    { "value": "alice@example.com", "index": 14, "groups": { "0": "alice@example.com" } },
    { "value": "bob@test.org", "index": 36, "groups": { "0": "bob@test.org" } }
  ]
}
```

### Test a date pattern

**Request:**
```json
{
  "text": "Due date: 2026-03-30",
  "pattern": "\\d{4}-\\d{2}-\\d{2}"
}
```

**Response:**
```json
{
  "isMatch": true,
  "matchedValue": "2026-03-30"
}
```

### Replace with named groups

**Request:**
```json
{
  "text": "30/03/2026",
  "pattern": "(?<day>\\d{2})/(?<month>\\d{2})/(?<year>\\d{4})",
  "replacement": "${year}-${month}-${day}"
}
```

**Response:**
```json
{
  "result": "2026-03-30",
  "replacementCount": 1
}
```

### Extract French dates

**Request:**
```json
{
  "text": "La date limite est le 1er avril 2026 et la prochaine réunion est le 4 mai 2023."
}
```

**Response:**
```json
{
  "dateCount": 2,
  "dates": [
    { "original": "1er avril 2026", "day": 1, "month": "avril", "year": 2026, "iso": "2026-04-01", "index": 22 },
    { "original": "4 mai 2023", "day": 4, "month": "mai", "year": 2023, "iso": "2023-05-04", "index": 64 }
  ]
}
```

## Limitations

- Input text is capped at 500,000 characters.
- Regex evaluation times out after 10 seconds to prevent catastrophic backtracking.
- Only .NET `System.Text.RegularExpressions` syntax is supported.
