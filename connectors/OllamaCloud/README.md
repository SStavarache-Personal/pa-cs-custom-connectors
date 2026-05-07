# Ollama Cloud

Custom connector for calling Ollama's hosted cloud API with an API key. It supports direct chat and generation requests, embeddings, model discovery, and a Power Automate-friendly batch chat operation.

## Operations

| Operation | Description |
|-----------|-------------|
| `GenerateChatResponse` | Send a non-streaming `/api/chat` request with messages, tools, structured output settings, and model options |
| `GenerateTextResponse` | Send a non-streaming `/api/generate` request with prompt-oriented generation inputs and structured output settings |
| `GenerateEmbeddings` | Call `/api/embed` for a single text input or an array of inputs |
| `ListModels` | List models currently available through Ollama's cloud API using `/api/tags` |
| `GenerateBatchChatResponses` | Execute multiple `/api/chat` requests inside one connector action and return each result together |

## Prerequisites

- Create an Ollama API key at <https://ollama.com/settings/keys>
- Configure the connector security value with your API key
- The script automatically adds the `Bearer ` prefix if your saved value does not already include it

## Configuration

### Authentication

This connector uses the `Authorization` header. Save either:

- your raw Ollama API key, or
- the full `Bearer <api-key>` value

### Request behavior

- The connector targets `https://ollama.com` and calls the cloud `/api/*` endpoints directly
- Streaming is intentionally blocked because Power Automate custom connectors return one response payload per action
- Structured outputs are supported through `formatMode` and `responseSchema`
- Common tuning inputs such as `maxTokens`, `temperature`, `topP`, `seed`, and `stop` are mapped into Ollama's `options` object automatically
- Use `options` for any additional model parameters supported by Ollama
- Use `extraBody` to pass future top-level request fields without waiting for a connector update

## Examples

### Chat with structured output

**Request**
```json
{
  "model": "gpt-oss:120b",
  "messages": [
    {
      "role": "user",
      "content": "Return a JSON object with a summary and a sentiment."
    }
  ],
  "formatMode": "schema",
  "responseSchema": {
    "type": "object",
    "properties": {
      "summary": { "type": "string" },
      "sentiment": { "type": "string" }
    },
    "required": ["summary", "sentiment"]
  },
  "maxTokens": 300,
  "temperature": 0.2
}
```

**Response**
```json
{
  "model": "gpt-oss:120b",
  "message": {
    "role": "assistant",
    "content": "{\"summary\":\"...\",\"sentiment\":\"positive\"}"
  },
  "parsedMessageContent": {
    "summary": "...",
    "sentiment": "positive"
  },
  "done": true
}
```

### Batch chat request

**Request**
```json
{
  "stopOnError": false,
  "requests": [
    {
      "model": "gpt-oss:120b",
      "messages": [
        { "role": "user", "content": "Summarize the Eiffel Tower in one sentence." }
      ],
      "maxTokens": 120
    },
    {
      "model": "gpt-oss:120b",
      "messages": [
        { "role": "user", "content": "Summarize the Great Wall of China in one sentence." }
      ],
      "maxTokens": 120
    }
  ]
}
```

**Response**
```json
{
  "totalRequests": 2,
  "successCount": 2,
  "failureCount": 0,
  "results": [
    {
      "index": 0,
      "isSuccess": true,
      "statusCode": 200,
      "response": {
        "model": "gpt-oss:120b",
        "message": {
          "role": "assistant",
          "content": "..."
        },
        "done": true
      }
    },
    {
      "index": 1,
      "isSuccess": true,
      "statusCode": 200,
      "response": {
        "model": "gpt-oss:120b",
        "message": {
          "role": "assistant",
          "content": "..."
        },
        "done": true
      }
    }
  ]
}
```

## Limits and notes

- Batch requests run sequentially and are capped at 10 chat requests per action to stay within Power Automate runtime limits
- Chat and generate actions require `stream` to be `false` or omitted
- The connector adds parsed JSON helper fields when Ollama returns JSON content inside `message.content` or `response`
- Large prompts or large batch sizes can still hit the 2 minute Power Automate execution timeout
- `ListModels` returns whatever Ollama currently exposes through the cloud `/api/tags` endpoint
