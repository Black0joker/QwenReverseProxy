using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QwenProxy.Logging;

namespace QwenProxy.Sse;

/// <summary>
/// Port of parser.parse_answer: converts streamed answer_chunk events into
/// structured protocol events (status, reasoning, action, stream_text, final).
/// Emits incrementally:
///  - status:   as soon as the status string value is fully received
///  - action:   as soon as EACH individual action object is fully received
///  - final:    when the entire outer JSON object is complete
/// </summary>
public static class AnswerParser
{
    private static readonly Lazy<QwenLogger> FallbackLog = new(() => new QwenLogger());

    /// <summary>
    /// Routes through the DI-registered shared logger (same logs/agent.log as
    /// controllers) when available; falls back to a local instance only before
    /// the host finishes bootstrapping.
    /// </summary>
    private static QwenLogger Log => QwenLogger.Shared ?? FallbackLog.Value;

    private static readonly Dictionary<char, string> EscapeMap = new()
    {
        ['n'] = "\n",
        ['t'] = "\t",
        ['r'] = "\r",
        ['"'] = "\"",
        ['\\'] = "\\",
        ['/'] = "/",
        ['b'] = "\b",
        ['f'] = "\f",
    };

    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        IAsyncEnumerable<SseEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        bool collecting = false;
        int outerBraceDepth = 0;
        bool inString = false;
        bool escape = false;

        var jsonBuffer = new StringBuilder();

        // State machine for tracking keys and arrays
        string currentString = "";
        string? lastString = null;
        string? currentKey = null;
        bool inActionsArray = false;

        // State machine for tracking individual action objects
        bool inActionObject = false;
        int actionBraceDepth = 0;
        var actionBuffer = new StringBuilder();

        bool statusSent = false;
        bool reasoningSent = false;
        var actionsSent = new HashSet<string>();

        // Tool text streaming state: stream ask_user question / user_response description
        string? currentTool = null;
        bool streamingToolText = false;

        JsonObject? ParseActionBuffer(string bufferText)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(bufferText);
            }
            catch (JsonException e)
            {
                // Attempt to repair malformed action JSON before giving up
                try
                {
                    node = JsonNode.Parse(JsonRepair.Repair(bufferText));
                }
                catch (Exception repairErr)
                {
                    Log.Error($"[!] Failed to parse action JSON: {e.Message} (repair failed: {repairErr.Message})");
                    return null;
                }
            }
            if (node is not JsonObject obj)
            {
                Log.Warn($"[!] Parsed action is not a dict (got {(node is null ? "null" : node.GetType().Name)}), skipping.");
                return null;
            }
            return obj;
        }

        SseEvent? NewActionEvent(JsonObject obj)
        {
            string? actionId = GetString(obj, "id");
            if (!string.IsNullOrEmpty(actionId) && actionsSent.Add(actionId))
            {
                return SseEvent.OfValue("action", obj);
            }
            return null;
        }

        var enumerator = events.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                var ev = enumerator.Current;

                // Pass through function calls / errors / closed immediately
                if (ev.Type is "function" or "error" or "closed")
                {
                    yield return ev;
                    // Release the underlying stream generator so SseStreamParser's
                    // cleanup runs even on early bailout (yield break triggers finally).
                    yield break;
                }

                // Pass through everything except answer chunks
                if (ev.Type != "answer_chunk")
                {
                    yield return ev;
                    continue;
                }

                string chunk = ev.Value?.GetValue<string>() ?? "";

                foreach (char ch in chunk)
                {
                    // 1. Ignore everything until the first JSON object starts
                    if (!collecting)
                    {
                        if (ch == '{')
                        {
                            collecting = true;
                            outerBraceDepth = 1;
                            jsonBuffer.Clear().Append('{');
                        }
                        continue;
                    }

                    // Add to the main buffer (for the final fallback parse)
                    jsonBuffer.Append(ch);

                    // Add to the action buffer if we are inside an action object
                    if (inActionObject) actionBuffer.Append(ch);

                    // 2. Handle escaping inside strings
                    if (escape)
                    {
                        escape = false;
                        if (streamingToolText)
                        {
                            string decoded = EscapeMap.TryGetValue(ch, out var esc) ? esc : ch.ToString();
                            yield return SseEvent.OfValue("stream_text", JsonValue.Create(decoded));
                        }
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escape = true;
                        continue;
                    }

                    // 3. Handle string state
                    if (ch == '"')
                    {
                        if (inString)
                        {
                            inString = false;
                            lastString = currentString;
                            currentString = "";

                            // Yield status immediately when its string value is complete
                            if (currentKey == "status" && !statusSent)
                            {
                                statusSent = true;
                                yield return SseEvent.OfValue("status", JsonValue.Create(lastString));
                                currentKey = null;
                            }
                            else if (currentKey == "reasoning" && !reasoningSent)
                            {
                                reasoningSent = true;
                                yield return SseEvent.OfValue("reasoning", JsonValue.Create(lastString));
                                currentKey = null;
                            }
                            // Capture tool name for stream_text detection
                            else if (currentKey == "tool")
                            {
                                currentTool = lastString;
                                currentKey = null;
                            }

                            // End streaming when the question/description string closes
                            if (streamingToolText) streamingToolText = false;
                        }
                        else
                        {
                            inString = true;
                            currentString = "";
                            // Start streaming tool text if this is the value for
                            // ask_user's "question" or user_response's "description"
                            if (currentKey == "question" && currentTool == "ask_user") streamingToolText = true;
                            else if (currentKey == "description" && currentTool == "user_response") streamingToolText = true;
                        }
                        continue;
                    }

                    if (inString)
                    {
                        currentString += ch;
                        if (streamingToolText)
                        {
                            yield return SseEvent.OfValue("stream_text", JsonValue.Create(ch.ToString()));
                        }
                        continue;
                    }

                    // 4. Outside string logic: track keys and arrays
                    if (ch == ':')
                    {
                        currentKey = lastString;
                        lastString = null;
                    }
                    else if (ch is ',' or '}' or ']')
                    {
                        lastString = null;
                    }

                    if (ch == '[')
                    {
                        if (currentKey == "actions")
                        {
                            inActionsArray = true;
                            currentKey = null;
                        }
                    }
                    else if (ch == ']')
                    {
                        if (inActionsArray)
                        {
                            if (inActionObject)
                            {
                                // The actions array closed while an action object was
                                // still open (missing closing brace in the stream).
                                // Salvage the buffered action before resetting.
                                var salvaged = ParseActionBuffer(actionBuffer.ToString()[..^1]);
                                if (salvaged is not null)
                                {
                                    var salvagedEvent = NewActionEvent(salvaged);
                                    if (salvagedEvent is not null) yield return salvagedEvent;
                                }
                                inActionObject = false;
                                actionBuffer.Clear();
                                actionBraceDepth = 0;
                            }
                            inActionsArray = false;
                        }
                    }

                    // 5. Track braces for outer JSON and inner action objects
                    if (ch == '{')
                    {
                        if (inActionsArray && !inActionObject)
                        {
                            // Starting a new action object
                            inActionObject = true;
                            actionBraceDepth = 1;
                            actionBuffer.Clear().Append('{');
                        }
                        else
                        {
                            // Nested brace
                            if (inActionObject) actionBraceDepth++;
                            else outerBraceDepth++;
                        }
                    }
                    else if (ch == '}')
                    {
                        if (inActionObject)
                        {
                            actionBraceDepth--;

                            // Action object is fully complete!
                            if (actionBraceDepth == 0)
                            {
                                var obj = ParseActionBuffer(actionBuffer.ToString());
                                if (obj is not null)
                                {
                                    var actionEvent = NewActionEvent(obj);
                                    if (actionEvent is not null) yield return actionEvent;
                                }

                                // Reset action state for the next action in the array
                                inActionObject = false;
                                actionBuffer.Clear();
                                currentTool = null;
                                streamingToolText = false;
                            }
                        }
                        else
                        {
                            outerBraceDepth--;
                        }
                    }

                    // 6. Entire outer JSON object received
                    if (collecting && outerBraceDepth == 0 && !inActionObject)
                    {
                        JsonNode? parsed;
                        string buffered = jsonBuffer.ToString();
                        try
                        {
                            parsed = JsonNode.Parse(buffered);
                        }
                        catch (JsonException e)
                        {
                            // Attempt to repair malformed final JSON before giving up
                            try
                            {
                                parsed = JsonNode.Parse(JsonRepair.Repair(buffered));
                                Log.Warn($"[!] Repaired malformed final JSON ({e.Message}) => {buffered}");
                            }
                            catch (Exception repairErr)
                            {
                                Log.Error($"[!] Failed to parse final JSON: {e.Message} (repair failed: {repairErr.Message}) => {buffered}");
                                collecting = false;
                                jsonBuffer.Clear();
                                continue;
                            }
                        }

                        if (parsed is JsonObject finalObj)
                        {
                            // Fallback for status if it wasn't caught as a string
                            if (!statusSent && finalObj.ContainsKey("status"))
                            {
                                statusSent = true;
                                yield return SseEvent.OfValue("status", finalObj["status"]);
                            }

                            if (!reasoningSent && finalObj.ContainsKey("reasoning"))
                            {
                                reasoningSent = true;
                                yield return SseEvent.OfValue("reasoning", finalObj["reasoning"]);
                            }

                            // Fallback for any actions that might have been missed
                            if (finalObj["actions"] is JsonArray actionsArr)
                            {
                                foreach (var actionNode in actionsArr)
                                {
                                    if (actionNode is not JsonObject actionObj)
                                    {
                                        Log.Warn($"[!] Action in fallback is not a dict (got {(actionNode is null ? "null" : actionNode.GetType().Name)}), skipping. => {buffered}");
                                        continue;
                                    }
                                    string? actionId = GetString(actionObj, "id");
                                    if (!string.IsNullOrEmpty(actionId) && actionsSent.Add(actionId))
                                    {
                                        yield return SseEvent.OfValue("action", actionObj);
                                    }
                                }
                            }

                            // Yield the final complete object
                            yield return SseEvent.OfValue("final", finalObj);
                        }

                        // Reset ALL state for potential subsequent JSON objects in the stream
                        collecting = false;
                        jsonBuffer.Clear();
                        outerBraceDepth = 0;
                        inString = false;
                        escape = false;
                        currentString = "";
                        lastString = null;
                        currentKey = null;
                        inActionsArray = false;
                        inActionObject = false;
                        actionBraceDepth = 0;
                        actionBuffer.Clear();
                        statusSent = false;
                        reasoningSent = false;
                        actionsSent = new HashSet<string>();
                        currentTool = null;
                        streamingToolText = false;
                    }
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string? GetString(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null) return null;
        try
        {
            return node.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// High-level pipeline used by the API (port of parser.stream_events):
/// parse_answer(parse_stream(response)).
/// </summary>
public static class StreamEventsPipeline
{
    public static IAsyncEnumerable<SseEvent> StreamEvents(
        Stream responseStream,
        string logBaseDir,
        CancellationToken cancellationToken = default)
    {
        return AnswerParser.ParseAsync(
            SseStreamParser.ParseAsync(responseStream, logBaseDir, cancellationToken),
            cancellationToken);
    }
}
