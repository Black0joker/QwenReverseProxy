using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace QwenProxy.Models;

// ---------------------------------------------------------------------------
// Completions
// ---------------------------------------------------------------------------

/// <summary>
/// A file attachment for a prompt (port of api/schemas/completion.FileAttachment).
///
/// Two accepted shapes:
/// 1. ALREADY UPLOADED: pass the file object returned by POST /files/upload or
///    /files/upload-image unchanged - it carries a real non-empty 'url' and is
///    forwarded to Qwen as-is (extra properties pass through).
/// 2. INLINE CONTENT (auto-upload): provide 'content' (and optionally 'filename').
///    The proxy uploads it to Qwen first and attaches the resulting file object.
///
/// An entry with neither a non-empty 'url' nor 'content' is treated as a
/// placeholder and skipped (no attachment is sent).
/// </summary>
public sealed class FileAttachment
{
    /// <summary>
    /// Nullable on purpose: Swagger UI auto-generates sample entries with
    /// "type": null. Null/blank is normalized to "file" on use, and entries
    /// with neither a non-empty url nor content are skipped entirely.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; } = "file";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    /// <summary>Extra properties are preserved and forwarded to Qwen (extra="allow").</summary>
    [JsonExtensionData]
    public JsonObject? Extra { get; set; }
}

public sealed class SendPromptRequest
{
    [JsonPropertyName("prompt")]
    [MinLength(1, ErrorMessage = "prompt must not be empty")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("chat_id")]
    [MinLength(1, ErrorMessage = "chat_id must not be empty")]
    public string ChatId { get; set; } = "";

    /// <summary>Parent message ID for threading. Empty/blank is treated as null.</summary>
    [JsonPropertyName("parent_message_id")]
    public string? ParentMessageId { get; set; }

    [JsonPropertyName("file_ids")]
    public List<string>? FileIds { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "qwen3.8-max";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "normal";

    public string? NormalizedParentMessageId =>
        string.IsNullOrWhiteSpace(ParentMessageId) ? null : ParentMessageId;
}

public sealed class StopCompletionRequest
{
    [JsonPropertyName("chat_id")]
    [Required]
    public string ChatId { get; set; } = "";

    [JsonPropertyName("parent_message_id")]
    [Required]
    public string ParentMessageId { get; set; } = "";
}

public sealed class StopCompletionResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

// ---------------------------------------------------------------------------
// Chats
// ---------------------------------------------------------------------------

public sealed class CreateChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "qwen3.8-max";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "normal";
}

public sealed class CreateChatResponse
{
    [JsonPropertyName("chat_id")]
    public string ChatId { get; set; } = "";
}

public sealed class ChatListResponse
{
    [JsonPropertyName("chats")]
    public JsonNode? Chats { get; set; }
}

public sealed class ChatParentIdResponse
{
    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }
}

public sealed class ImportChatRequest
{
    [JsonPropertyName("data")]
    [Required]
    public JsonObject Data { get; set; } = new();
}

/// <summary>Request body for uploading a chat from the internal cache format.</summary>
public sealed class UploadCacheChatRequest
{
    [JsonPropertyName("chat_id")]
    [Required]
    public string ChatId { get; set; } = "";

    [JsonPropertyName("messages")]
    [Required]
    public JsonArray Messages { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Files
// ---------------------------------------------------------------------------

public sealed class UploadFileContentRequest
{
    [JsonPropertyName("content")]
    [Required]
    public string Content { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "tool_output.txt";
}

public sealed class UploadFileResponse
{
    [JsonPropertyName("file")]
    public JsonNode? File { get; set; }

    [JsonPropertyName("file_id")]
    public string FileId { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

// ---------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------

public sealed class SystemPromptRequest
{
    [JsonPropertyName("instruction")]
    [Required]
    public string Instruction { get; set; } = "";
}
