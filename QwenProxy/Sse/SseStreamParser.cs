using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QwenProxy.Logging;

namespace QwenProxy.Sse;

/// <summary>
/// Port of parser.parse_stream: consumes the raw Qwen SSE stream and yields
/// structured SseEvents (response_id, tokens, reasoning, answer_chunk,
/// summary_title, function, finished, warning/error, closed).
/// Also writes the raw stream to logs/{chat_id}/stream_log_{response_id}.log.
/// </summary>
public static class SseStreamParser
{
    private static readonly Lazy<QwenLogger> FallbackLog = new(() => new QwenLogger());

    /// <summary>
    /// Routes through the DI-registered shared logger (same logs/agent.log as
    /// controllers) when available; falls back to a local instance only before
    /// the host finishes bootstrapping.
    /// </summary>
    private static QwenLogger Log => QwenLogger.Shared ?? FallbackLog.Value;

    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        Stream stream,
        string logBaseDir,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        bool responseIdSent = false;
        string? lastSummaryTitle = null;
        string? lastSummaryThought = null;

        string? responseId = null;
        bool errorScanned = false;

        bool reasoningFound = false;
        string? chatId = null;
        int keepAliveCount = 0;

        var logBuffer = new StreamLogBuffer(logBaseDir);
        bool stop = false;

        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while (!stop && (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                if (line.Length == 0) continue;

                if (responseId != null && chatId != null)
                    logBuffer.Write(line);

                if (!errorScanned)
                {
                    JsonNode? errorObj = null;
                    try
                    {
                        errorObj = JsonNode.Parse(line);
                    }
                    catch (JsonException)
                    {
                        errorScanned = true;
                    }

                    if (errorObj is JsonObject eo &&
                        eo.TryGetPropertyValue("success", out var successNode) &&
                        successNode is not null &&
                        successNode.GetValueKind() == JsonValueKind.False)
                    {
                        var data = eo["data"] as JsonObject;
                        string? dataCode = GetString(data, "code");
                        if (dataCode == "RateLimited")
                        {
                            string template = GetString(data, "template") ?? "";
                            string num = GetString(data, "num") ?? "";
                            string value = template.Replace("{{num}}", num);
                            yield return SseEvent.Error(dataCode, JsonValue.Create(value));
                        }

                        yield return SseEvent.Error(dataCode, data?["details"]);
                        stop = true;
                        break;
                    }
                }

                if (!line.StartsWith("data: ")) continue;

                string payload = line[6..].Trim();

                if (payload == "[DONE]") break;

                JsonObject? ev;
                try
                {
                    ev = JsonNode.Parse(payload) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }
                if (ev is null) continue;

                if (ev["error"] is not null)
                {
                    var err = ev["error"] as JsonObject;
                    yield return SseEvent.Error(GetString(err, "code"), err?["details"]);
                    stop = true;
                    break;
                }

                // Check for keep_alive response.info events and track count
                if (ev["response.info"] is JsonObject respInfo &&
                    GetString(respInfo, "action") == "keep_alive")
                {
                    keepAliveCount++;
                    if (keepAliveCount >= 5)
                    {
                        yield return new SseEvent
                        {
                            Type = "warnning",
                            Code = "Timeout",
                            Value = JsonValue.Create(
                                $"Stream timeout: received {keepAliveCount} keep_alive signals without progress"),
                            HasValue = true,
                        };
                    }

                    if (keepAliveCount >= 10)
                    {
                        yield return SseEvent.Error(
                            "Timeout",
                            JsonValue.Create(
                                $"Stream timeout: received {keepAliveCount} keep_alive signals without progress"));
                        stop = true;
                        break;
                    }
                    continue;
                }

                if (!responseIdSent)
                {
                    string? parentId = null;
                    if (ev["response.created"] is JsonObject respCreated)
                    {
                        responseId = GetString(respCreated, "response_id");
                        chatId = GetString(respCreated, "chat_id");
                        parentId = GetString(respCreated, "parent_id");
                    }

                    if (!string.IsNullOrEmpty(responseId) && !string.IsNullOrEmpty(chatId))
                    {
                        responseIdSent = true;
                        logBuffer.SetTarget(chatId!, responseId!);
                        logBuffer.Write(line);
                        yield return SseEvent.OfValue("response_id", JsonValue.Create(responseId));
                        if (!string.IsNullOrEmpty(parentId))
                            yield return SseEvent.OfValue("parent_message_id", JsonValue.Create(parentId));
                    }
                }

                if (!string.Equals(responseId, GetString(ev, "response_id"), StringComparison.Ordinal))
                    continue;

                if (ev["usage"] is JsonObject usage && usage["total_tokens"] is not null)
                {
                    yield return SseEvent.OfValue("tokens", usage.DeepClone());
                }

                var choices = ev["choices"] as JsonArray;

                if (!reasoningFound && choices is { Count: > 0 })
                {
                    if (choices[0] is JsonObject choc &&
                        choc["delta"] is JsonObject firstDelta &&
                        firstDelta["content"] is JsonNode contNode)
                    {
                        string? cont = contNode.GetValue<string>();
                        if (cont is not null && cont.Contains("reason"))
                        {
                            reasoningFound = true;
                            yield return new SseEvent { Type = "reasoning", Value = null, HasValue = true };
                        }
                    }
                }

                bool fExit = false;
                if (choices is not null)
                {
                    foreach (var choiceNode in choices)
                    {
                        if (choiceNode is not JsonObject choice) continue;
                        var delta = choice["delta"] as JsonObject;
                        string? phase = GetString(delta, "phase");
                        string? status = GetString(delta, "status");
                        string? role = GetString(delta, "role");

                        if (phase == "answer" && status == "finished" && role == "assistant")
                        {
                            yield return SseEvent.Bare("finished");
                            fExit = true;
                            break;
                        }

                        if (role == "function")
                        {
                            yield return new SseEvent
                            {
                                Type = "function",
                                Name = GetString(delta, "name"),
                                HasName = true,
                            };
                            fExit = true;
                            break;
                        }

                        if (phase == "thinking_summary")
                        {
                            var titles = (delta?["extra"] as JsonObject)?["summary_title"]?["content"] as JsonArray;
                            if (titles is { Count: > 0 })
                            {
                                string? title = titles[^1]?.GetValue<string>();
                                if (title != lastSummaryTitle)
                                {
                                    lastSummaryTitle = title;
                                    yield return SseEvent.OfValue("summary_title", JsonValue.Create(title));
                                }
                            }

                            var thoughts = (delta?["extra"] as JsonObject)?["summary_thought"]?["content"] as JsonArray;
                            if (thoughts is { Count: > 0 })
                            {
                                string? thought = thoughts[^1]?.GetValue<string>();
                                if (thought != lastSummaryThought)
                                {
                                    lastSummaryThought = thought;
                                    yield return SseEvent.OfValue("summary_thought", JsonValue.Create(thought));
                                }
                            }
                        }
                        else if (phase == "answer")
                        {
                            if (role == "function")
                            {
                                yield return new SseEvent
                                {
                                    Type = "function",
                                    Name = GetString(delta, "name") ?? "",
                                    HasName = true,
                                };
                                fExit = true;
                                break;
                            }

                            yield return SseEvent.OfValue(
                                "answer_chunk",
                                JsonValue.Create(GetString(delta, "content") ?? ""));
                        }
                    }
                }

                if (fExit) break;
            }
        }
        finally
        {
            logBuffer.Close();
        }

        yield return SseEvent.Bare("closed");
    }

    private static string? GetString(JsonObject? obj, string key)
    {
        if (obj is null) return null;
        if (!obj.TryGetPropertyValue(key, out var node) || node is null) return null;
        try
        {
            return node.GetValue<string>();
        }
        catch (Exception)
        {
            return node.ToJsonString();
        }
    }

    /// <summary>
    /// Buffered writer for SSE stream logs (port of _StreamLogBuffer).
    /// Opens the log file lazily and flushes every FlushEvery lines.
    /// </summary>
    private sealed class StreamLogBuffer
    {
        private const int FlushEvery = 50;

        private readonly string _baseDir;
        private readonly List<string> _buffer = new();
        private StreamWriter? _file;
        private string? _logPath;

        public StreamLogBuffer(string baseDir) => _baseDir = baseDir;

        /// <summary>Set the target log file once chat_id/response_id are known.</summary>
        public void SetTarget(string chatId, string responseId)
        {
            if (_logPath is not null) return;
            _logPath = Path.Combine(_baseDir, "logs", chatId, $"stream_log_{responseId}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        }

        /// <summary>Buffer a line; flush to disk when the buffer reaches FlushEvery.</summary>
        public void Write(string line)
        {
            if (_logPath is null) return;
            // UTF8Encoding(false): no BOM, matching Python open(..., encoding="utf-8").
            _file ??= new StreamWriter(_logPath, append: true, new UTF8Encoding(false));
            _buffer.Add(line);
            if (_buffer.Count >= FlushEvery) Flush();
        }

        public void Flush()
        {
            if (_buffer.Count > 0 && _file is not null)
            {
                foreach (var line in _buffer) _file.WriteLine(line);
                _file.Flush();
                _buffer.Clear();
            }
        }

        public void Close()
        {
            try
            {
                Flush();
            }
            finally
            {
                if (_file is not null)
                {
                    try { _file.Dispose(); }
                    catch { /* ignore */ }
                    _file = null;
                }
            }
        }
    }
}
