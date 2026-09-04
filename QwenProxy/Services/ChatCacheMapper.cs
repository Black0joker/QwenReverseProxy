using System.Text.Json;
using System.Text.Json.Nodes;

namespace QwenProxy.Services;

/// <summary>
/// Maps the internal cache format (chat_id + flat message array) to the Qwen
/// chat import/messages format (nested history dictionary with content_list).
/// </summary>
public static class ChatCacheMapper
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Converts a cache JSON object into the full Qwen messages/import format.
    /// </summary>
    public static JsonObject MapToMessagesFormat(JsonObject cache)
    {
        string chatId = cache["chat_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("cache is missing 'chat_id'");

        var Title = cache["title"]?.GetValue<string>()
            ?? throw new InvalidOperationException("cache is missing 'Title' ");

        var cacheMessages = cache["messages"] as JsonArray
            ?? throw new InvalidOperationException("cache is missing 'messages' array");

        // Build a children map: parent_id -> list of child message_ids
        var childrenMap = new Dictionary<string, List<string>>();
        string? lastMessageId = null;

        foreach (var msgNode in cacheMessages)
        {
            if (msgNode is not JsonObject msg) continue;
            string? msgId = msg["message_id"]?.GetValue<string>();
            string? parentId = msg["parent_message_id"]?.GetValue<string>();

            if (msgId is not null) lastMessageId = msgId;

            if (parentId is not null && msgId is not null)
            {
                if (!childrenMap.TryGetValue(parentId, out var list))
                {
                    list = new List<string>();
                    childrenMap[parentId] = list;
                }
                list.Add(msgId);
            }
        }

        // Build the messages dictionary and the ordered messages array.
        // The dict preserves insertion order for the history lookup, while the
        // array provides Qwen with an explicit chronological display order.
        var messagesDict = new JsonObject();
        var messagesArray = new JsonArray();
        long baseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var msgNode in cacheMessages)
        {
            if (msgNode is not JsonObject msg) continue;
            string? msgId = msg["message_id"]?.GetValue<string>();
            if (msgId is null) continue;

            string role = msg["role"]?.GetValue<string>() ?? "user";
            string? parentId = msg["parent_message_id"]?.GetValue<string>();
            string modelName = msg["model_name"]?.GetValue<string>() ?? "qwen3.8-max";
            bool thinkingMode = msg["thinking_mode"]?.GetValue<bool>() ?? false;

            var children = childrenMap.TryGetValue(msgId, out var childList)
                ? childList
                : new List<string>();

            JsonObject mapped;
            if (role == "user")
            {
                mapped = BuildUserMessage(msg, msgId, parentId, modelName, thinkingMode, children, baseTimestamp);
            }
            else
            {
                mapped = BuildAssistantMessage(msg, msgId, parentId, modelName, thinkingMode, children, baseTimestamp);
            }

            messagesDict[msgId] = mapped;
            messagesArray.Add(mapped.DeepClone());
        }

        // Assemble the full messages format
        var history = new JsonObject
        {
            ["messages"] = messagesDict,
            ["currentId"] = lastMessageId,
            ["currentResponseIds"] = new JsonArray(lastMessageId is not null ? JsonValue.Create(lastMessageId) : null),
            ["pagination"] = null,
        };

        var chatObj = new JsonObject
        {
            ["history"] = history,
            ["models"] = new JsonArray("qwen3.8-max"),
            ["messages"] = messagesArray,
        };

        long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var chatEntry = new JsonObject
        {
            ["id"] = chatId,
            ["user_id"] = "",
            ["title"] = Title,
            ["chat"] = chatObj,
            ["updated_at"] = baseTimestamp,
            ["created_at"] = baseTimestamp,
            ["share_id"] = null,
            ["archived"] = false,
            ["pinned"] = false,
            ["meta"] = new JsonObject { ["timestamp"] = timestampMs },
            ["folder_id"] = null,
            ["currentResponseIds"] = new JsonArray(lastMessageId is not null ? JsonValue.Create(lastMessageId) : null),
            ["currentId"] = lastMessageId,
            ["chat_type"] = "t2t",
            ["models"] = null,
            ["project_id"] = null,
        };

        return new JsonObject
        {
            ["success"] = true,
            ["request_id"] = Guid.NewGuid().ToString(),
            ["data"] = new JsonArray(chatEntry),
        };
    }

    private static JsonObject BuildFeatureConfig(bool thinkingMode)
    {
        if (thinkingMode)
        {
            return new JsonObject
            {
                ["thinking_enabled"] = true,
                ["output_schema"] = "phase",
                ["instructions"] = null,
                ["research_mode"] = "normal",
                ["auto_thinking"] = true,
                ["thinking_mode"] = "Thinking",
                ["thinking_format"] = "summary",
                ["auto_search"] = true,
            };
        }

        return new JsonObject
        {
            ["thinking_enabled"] = false,
            ["output_schema"] = "phase",
            ["instructions"] = null,
            ["research_mode"] = "normal",
            ["auto_thinking"] = false,
            ["thinking_mode"] = "Fast",
            ["auto_search"] = true,
        };
    }

    private static JsonObject BuildUserMessage(
        JsonObject msg, string msgId, string? parentId, string modelName,
        bool thinkingMode, List<string> children, long timestamp)
    {
        string content = msg["content"]?.GetValue<string>() ?? "";

        var childrenArr = new JsonArray();
        foreach (var c in children) childrenArr.Add(JsonValue.Create(c));

        return new JsonObject
        {
            ["id"] = msgId,
            ["role"] = "user",
            ["content"] = content,
            ["models"] = new JsonArray(modelName),
            ["chat_type"] = "t2t",
            ["sub_chat_type"] = "t2t",
            ["edited"] = false,
            ["error"] = null,
            ["extra"] = new JsonObject
            {
                ["meta"] = new JsonObject { ["subChatType"] = "t2t" },
            },
            ["feature_config"] = BuildFeatureConfig(thinkingMode),
            ["parentId"] = parentId,
            ["turn_id"] = null,
            ["childrenIds"] = childrenArr,
            ["files"] = new JsonArray(),
            ["timestamp"] = timestamp,
        };
    }

    private static JsonObject BuildAssistantMessage(
        JsonObject msg, string msgId, string? parentId, string modelName,
        bool thinkingMode, List<string> children, long timestamp)
    {
        // Resolve usage from the source message; fall back to zero usage only
        // when the cache entry carries no token data.
        JsonObject usage = msg["usage"] is JsonObject srcUsage
            ? srcUsage.DeepClone().AsObject()
            : BuildZeroUsage();

        var contentList = new JsonArray();

        // Thinking summary phase
        var thinking = msg["thinking"] as JsonObject;
        if (thinkingMode && thinking is not null)
        {
            var thinkingEntry = new JsonObject
            {
                ["content"] = "",
                ["phase"] = "thinking_summary",
                ["status"] = "finished",
                ["extra"] = thinking.DeepClone(),
                ["role"] = "assistant",
                ["response_id"] = msgId,
                ["usage"] = usage.DeepClone(),
                ["timestamp"] = timestamp,
            };
            contentList.Add(thinkingEntry);
        }

        // Answer phase: serialize the content object wrapped in a json code fence
        var contentObj = msg["content"];
        string answerContent = "";
        if (contentObj is not null)
        {
            string serialized = contentObj.ToJsonString(SerializerOptions);
            answerContent = $"```json\n{serialized}\n```";
        }

        var answerEntry = new JsonObject
        {
            ["content"] = answerContent,
            ["phase"] = "answer",
            ["status"] = "finished",
            ["extra"] = null,
            ["role"] = "assistant",
            ["response_id"] = msgId,
            ["usage"] = usage.DeepClone(),
            ["timestamp"] = timestamp,
        };
        contentList.Add(answerEntry);

        var childrenArr = new JsonArray();
        foreach (var c in children) childrenArr.Add(JsonValue.Create(c));

        // Capitalize each dash-separated segment: "qwen3.8-max" -> "Qwen3.8-Max"
        string displayName = string.Join("-", modelName.Split('-')
            .Select(part => part.Length == 0 ? part : char.ToUpper(part[0]) + part[1..]));

        return new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = "",
            ["reasoning_content"] = null,
            ["chat_type"] = "t2t",
            ["sub_chat_type"] = "t2t",
            ["model"] = modelName,
            ["modelName"] = displayName,
            ["modelIdx"] = 0,
            ["id"] = msgId,
            ["parentId"] = parentId,
            ["childrenIds"] = childrenArr,
            ["feature_config"] = BuildFeatureConfig(thinkingMode),
            ["content_list"] = contentList,
            ["is_stop"] = false,
            ["edited"] = false,
            ["error"] = null,
            ["meta"] = new JsonObject(),
            ["extra"] = new JsonObject
            {
                ["meta"] = new JsonObject { ["subChatType"] = "t2t" },
                ["endTime"] = (double)timestamp,
            },
            ["feedbackId"] = null,
            ["turn_id"] = null,
            ["annotation"] = null,
            ["done"] = true,
            ["info"] = null,
            ["timestamp"] = timestamp,
        };
    }

    private static JsonObject BuildZeroUsage()
    {
        return new JsonObject
        {
            ["input_tokens"] = 0,
            ["output_tokens"] = 0,
            ["characters"] = 0,
            ["total_tokens"] = 0,
            ["input_tokens_details"] = new JsonObject { ["text_tokens"] = 0 },
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0, ["text_tokens"] = 0 },
            ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
        };
    }
}
