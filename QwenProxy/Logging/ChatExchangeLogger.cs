using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QwenProxy.Models;

namespace QwenProxy.Logging;

/// <summary>
/// Structured logging for chat completion exchanges (requests to Qwen and
/// their responses). For every exchange it writes one JSON file under
/// logs/{chat_id}/exchange_{timestamp}_{id}.json and prints a concise
/// console summary through QwenLogger.
///
/// Secrets (tokens/cookies) are never logged. Inline file content is
/// truncated to keep log files small.
/// </summary>
public sealed class ChatExchangeLogger
{
    private const int PromptPreviewChars = 120;
    private const int MaxLoggedContentChars = 2000;

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _baseDir;
    private readonly QwenLogger _console;

    public ChatExchangeLogger(QwenLogger console, string? baseDir = null)
    {
        _console = console;
        _baseDir = baseDir ?? AppContext.BaseDirectory;
    }

    /// <summary>Mutable state carried through one exchange.</summary>
    public sealed class ExchangeHandle
    {
        public string ExchangeId { get; init; } = "";
        public string ChatId { get; init; } = "";
        public Stopwatch Timer { get; } = Stopwatch.StartNew();
        public JsonObject Request { get; set; } = new();
        public JsonObject Upstream { get; } = new();
        public JsonObject EventCounts { get; } = new();
        public JsonNode? Final { get; set; }
        public JsonNode? ErrorEvent { get; set; }
        public string? Error { get; set; }
        public string Outcome { get; set; } = "unknown";
        public int TotalEvents { get; set; }
    }

    /// <summary>Log the outgoing request and start the exchange timer.</summary>
    public ExchangeHandle Begin(SendPromptRequest body, List<JsonObject>? filesPayload)
    {
        var handle = new ExchangeHandle
        {
            ExchangeId = Guid.NewGuid().ToString(),
            ChatId = body.ChatId,
        };

        var filesSummary = new JsonArray();
        if (filesPayload is not null)
        {
            foreach (var f in filesPayload)
            {
                var entry = new JsonObject
                {
                    ["type"] = f["type"]?.DeepClone(),
                    ["filename"] = f["filename"]?.DeepClone(),
                    ["url"] = f["url"]?.DeepClone(),
                };
                string? content = null;
                try { content = f["content"]?.GetValue<string>(); }
                catch { /* non-string content */ }
                if (content is not null)
                {
                    entry["content_chars"] = content.Length;
                    entry["content_preview"] = Truncate(content, MaxLoggedContentChars);
                }
                filesSummary.Add(entry);
            }
        }

        handle.Request = new JsonObject
        {
            ["model"] = body.Model,
            ["mode"] = body.Mode,
            ["chat_id"] = body.ChatId,
            ["parent_message_id"] = body.NormalizedParentMessageId,
            ["prompt"] = body.Prompt,
            ["files"] = filesSummary,
        };

        _console.Info(
            $"[REQ] chat={Short(body.ChatId)} model={body.Model} mode={body.Mode} " +
            $"files={filesSummary.Count} prompt={Preview(body.Prompt)}");

        return handle;
    }

    /// <summary>Record upstream HTTP status/content type once headers arrive.</summary>
    public void SetUpstream(ExchangeHandle handle, int statusCode, string? contentType)
    {
        handle.Upstream["status_code"] = statusCode;
        handle.Upstream["content_type"] = contentType;
    }

    /// <summary>Count one parsed SSE protocol event.</summary>
    public void TrackEvent(ExchangeHandle handle, string eventType)
    {
        handle.TotalEvents++;
        int current = 0;
        try { current = handle.EventCounts[eventType]?.GetValue<int>() ?? 0; }
        catch { /* not an int counter yet */ }
        handle.EventCounts[eventType] = current + 1;
    }

    /// <summary>Capture the final protocol object (last one wins).</summary>
    public void CaptureFinal(ExchangeHandle handle, JsonNode final) =>
        handle.Final = final.DeepClone();

    /// <summary>Capture an error event emitted by the upstream stream.</summary>
    public void CaptureErrorEvent(ExchangeHandle handle, JsonNode error) =>
        handle.ErrorEvent = error.DeepClone();

    /// <summary>Record a failure (exception message) and write the log immediately.</summary>
    public void Fail(ExchangeHandle handle, string outcome, string error)
    {
        handle.Outcome = outcome;
        handle.Error = error;
        Write(handle);
    }

    /// <summary>Record a non-streaming JSON outcome (upstream error / captcha).</summary>
    public void CompleteJson(ExchangeHandle handle, string outcome, JsonNode? payload)
    {
        handle.Outcome = outcome;
        if (payload is not null)
        {
            handle.Upstream["json_payload"] = payload.DeepClone();
        }
        Write(handle);
    }

    /// <summary>Finish a streamed exchange and write the log file.</summary>
    public void Complete(ExchangeHandle handle)
    {
        handle.Outcome = handle.ErrorEvent is not null ? "stream_error" : "streamed";
        Write(handle);
    }

    /// <summary>Path of the exchange log file for a handle.</summary>
    public string LogPath(ExchangeHandle handle) => Path.Combine(
        _baseDir, "logs", Sanitize(handle.ChatId),
        $"exchange_{DateTime.Now:yyyyMMdd-HHmmss}_{handle.ExchangeId[..8]}.json");

    private void Write(ExchangeHandle handle)
    {
        handle.Timer.Stop();
        string path = LogPath(handle);

        var response = new JsonObject
        {
            ["outcome"] = handle.Outcome,
            ["events_total"] = handle.TotalEvents,
            ["event_counts"] = handle.EventCounts,
        };
        if (handle.Final is not null) response["final"] = handle.Final;
        if (handle.ErrorEvent is not null) response["error_event"] = handle.ErrorEvent;
        if (handle.Error is not null) response["error"] = handle.Error;

        var record = new JsonObject
        {
            ["exchange_id"] = handle.ExchangeId,
            ["started_at"] = DateTime.Now.ToString("o"),
            ["duration_ms"] = handle.Timer.ElapsedMilliseconds,
            ["request"] = handle.Request,
            ["upstream"] = handle.Upstream,
            ["response"] = response,
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, record.ToJsonString(PrettyJson), new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            _console.Error($"[LOG ERROR] Could not write exchange log: {e.Message}");
            return;
        }

        string summary =
            $"[RES] chat={Short(handle.ChatId)} outcome={handle.Outcome} " +
            $"elapsed={handle.Timer.ElapsedMilliseconds}ms events={handle.TotalEvents}";
        if (handle.Error is not null)
        {
            _console.Error($"{summary} error={handle.Error} log={path}");
        }
        else if (handle.Outcome == "stream_error")
        {
            _console.Warn($"{summary} log={path}");
        }
        else
        {
            _console.Success($"{summary} log={path}");
        }
    }

    private static string Preview(string text)
    {
        string flat = text.Replace("\r", " ").Replace("\n", " ");
        string clipped = Truncate(flat, PromptPreviewChars);
        return "\"" + clipped.Replace("\"", "\\\"") + "\"";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + $"... (+{value.Length - max} chars)";

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        }
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }
}
