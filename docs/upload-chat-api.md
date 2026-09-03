# Upload Chat API

Uploads a chat conversation to Qwen (chat.qwen.ai) via the proxy. The proxy accepts the **internal cache format** and automatically maps it to the Qwen import format before uploading.

---

## Authentication

All endpoints require an `Authorization` header with an **AES-encrypted Bearer token**.

```
Authorization: Bearer <AES-encrypted-base64-token>
```

The decrypted payload must be JSON containing the Qwen `token` (and optionally `s5_token`). The proxy decrypts it and forwards it to Qwen as cookies.

| Error | Meaning |
|---|---|
| `401` | Missing/invalid token, bad Base64, decryption failed, or decrypted payload is not valid JSON |

---

## `POST /api/v1/chats/upload-cache`

Upload a chat from the **cache format**. The proxy maps it to the Qwen messages/import format and uploads it.

### Request

```
POST /api/v1/chats/upload-cache
Content-Type: application/json
Authorization: Bearer <token>
```

### Body schema

| Field | Type | Required | Description |
|---|---|---|---|
| `chat_id` | string | ✅ | The chat session ID to upload under |
| `messages` | array | ✅ | Ordered list of message objects (see below) |

### Message object

| Field | Type | Required | Description |
|---|---|---|---|
| `message_id` | string | ✅ | Unique ID of this message |
| `parent_message_id` | string \| null | ✅ | ID of the parent message (`null` for the first message) |
| `role` | string | ✅ | `"user"` or `"assistant"` |
| `model_name` | string | ✅ | e.g. `"qwen3.8-max"` |
| `thinking_mode` | boolean | ✅ | Whether thinking was enabled for this turn |
| `content` | string \| object | ✅ | User: plain string. Assistant: object `{ "status": ..., "actions": [...] }` |
| `thinking` | object | ⛔ optional | Assistant only. `{ "summary_title": { "content": [...] }, "summary_thought": { "content": [...] } }` |

**Notes on mapping:**
- `childrenIds` are computed automatically from `parent_message_id` relationships.
- Assistant `content` objects are serialized and wrapped in a ` ```json ` code fence (Qwen's answer-phase format).
- `feature_config` is derived from `thinking_mode` (`Thinking` vs `Fast`).
- The last message in the array becomes `currentId`.

### Example request

```json
{
  "chat_id": "b733cf8f-d890-4b41-919c-589a570e47f1",
  "messages": [
    {
      "message_id": "8e5aa9ae-5d82-4b55-a216-4fa95c1aea79",
      "parent_message_id": null,
      "role": "user",
      "model_name": "qwen3.8-max",
      "thinking_mode": true,
      "content": "hi"
    },
    {
      "message_id": "6f68a769-88f8-48de-8913-67bec5387343",
      "parent_message_id": "8e5aa9ae-5d82-4b55-a216-4fa95c1aea79",
      "role": "assistant",
      "model_name": "qwen3.8-max",
      "thinking_mode": true,
      "thinking": {
        "summary_title": {
          "content": ["Awaiting user's next input"]
        },
        "summary_thought": {
          "content": ["I am preparing a thoughtful response to maintain the flow of conversation."]
        }
      },
      "content": {
        "status": "waiting",
        "actions": [
          {
            "id": "1",
            "tool": "ask_user",
            "arguments": {
              "question": "What task would you like me to help you with?"
            }
          }
        ]
      }
    }
  ]
}
```

### Success response

`200 OK` — returns the upstream Qwen import result:

```json
{
  "success": true,
  "data": { ... }
}
```

### Errors

| Status | Meaning |
|---|---|
| `400` | Invalid request body / missing required fields |
| `401` | Authentication failure |
| `502` | Upstream Qwen request failed |

---

## `POST /api/v1/chats/import` (advanced)

Upload a chat that is **already in the Qwen messages/import format** (no mapping applied).

### Body schema

| Field | Type | Required | Description |
|---|---|---|---|
| `data` | object | ✅ | Full Qwen chat object (the `{ success, request_id, data: [...] }` structure) |

Use this only when you already have the fully-mapped Qwen format. Prefer `upload-cache` for the internal cache format.

---

## Usage summary

```
Cache format  →  POST /api/v1/chats/upload-cache   (proxy maps + uploads)
Qwen format   →  POST /api/v1/chats/import          (uploads as-is)
```
