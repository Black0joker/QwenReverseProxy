using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using QwenProxy.Config;
using QwenProxy.Logging;
using QwenProxy.Models;
using QwenProxy.Services;

namespace QwenProxy.Controllers;

[ApiController]
[Route("api/v1/chats")]
[EnableRateLimiting(RateLimitPolicies.Chats)]
public sealed class ChatsController : ControllerBase
{
    private readonly CookieStore _cookieStore;
    private readonly string _contentRootPath;
    private readonly QwenLogger _log;

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ChatsController(CookieStore cookieStore, IWebHostEnvironment env, QwenLogger log)
    {
        _cookieStore = cookieStore;
        _contentRootPath = env.ContentRootPath;
        _log = log;
    }

    /// <summary>Create a new chat.</summary>
    [HttpPost("new")]
    public async Task<ActionResult<CreateChatResponse>> CreateChat(
        [FromBody] CreateChatRequest body, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore, body.Model, body.Mode);
        string chatId = await client.CreateChatAsync(ct);
        return new CreateChatResponse { ChatId = chatId };
    }

    /// <summary>List chat sessions.</summary>
    [HttpGet]
    public async Task<ActionResult<ChatListResponse>> FetchChats(CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        var chats = await client.FetchChatsAsync(ct);
        return new ChatListResponse { Chats = chats };
    }

    

    /// <summary>Get chat parent/current ID.</summary>
    [HttpGet("{chatId}/parent")]
    public async Task<ActionResult<ChatParentIdResponse>> GetChatParentId(string chatId, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        string? parentId = await client.GetChatParentIdAsync(chatId, ct);
        return new ChatParentIdResponse { ParentId = parentId };
    }

   

    [HttpGet("{chatId}/chatTitle")]
    public async Task<IActionResult> GetChatTitle(string chatId, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        var title = await client.GetChatTitleAsync(chatId, ct);
        return new JsonResult(new { Title = title });
    }

   
    /// <summary>Delete a chat session.</summary>
    [HttpDelete("{chatId}")]
    public async Task<IActionResult> DeleteChat(string chatId, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        var data = await client.DeleteChatAsync(chatId, ct);
        return new JsonResult(data);
    }


    /// <summary>Upload a chat from the internal cache format (auto-mapped to Qwen import format).</summary>
    [HttpPost("upload-cache")]
    public async Task<IActionResult> UploadCacheChat([FromBody] UploadCacheChatRequest body, CancellationToken ct)
    {
        
        var cache = new JsonObject
        {
            ["chat_id"] = body.ChatId,
            ["title"] = body.Title ?? "New Chat",
            ["messages"] = body.Messages.DeepClone(),
        };

        var messagesFormat = ChatCacheMapper.MapToMessagesFormat(cache);
        _log.Info($"[DIAG] Mapping complete for body.ChatId={body.ChatId}");

        // Persist the mapped chat locally for diagnostics (before upload so it
        // is always available even if the upstream call fails).
        SaveMappedChat(body.ChatId, messagesFormat);

        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        await client.UploadChatAsync(messagesFormat, ct);

        // Fetch all chats and return the first chat_id (most recently created).
        var chats = await client.FetchChatsAsync(ct);
        string? firstChatId = null;
        if (chats is JsonArray chatsArray && chatsArray.Count > 0)
        {
            firstChatId = chatsArray[0]?["id"]?.GetValue<string>();
        }

        _log.Info($"[DIAG] Resolved chat_id from get-all-chats: {firstChatId}");

        // Delete the original chat that was passed in the request body.
        await client.DeleteChatAsync(body.ChatId, ct);
        _log.Info($"[DIAG] Deleted previous chat_id={body.ChatId}");

        // Get the parent/current message ID for the newly created chat.
        string? parentId = firstChatId is not null
            ? await client.GetChatParentIdAsync(firstChatId, ct)
            : null;
        _log.Info($"[DIAG] parent_message_id for {firstChatId}: {parentId}");

        return new JsonResult(new { chat_id = firstChatId, parent_message_id = parentId });
    }

    /// <summary>
    /// Writes the mapped chat JSON to the local chats/ folder for diagnostics.
    /// Failures are logged but never block the upload.
    /// </summary>
    private void SaveMappedChat(string chatId, JsonObject mapped)
    {
        try
        {
            string dir = Path.Combine(_contentRootPath, "chats");
            Directory.CreateDirectory(dir);

            string filename = $"{chatId}.json";
            string path = Path.Combine(dir, filename);

            string json = mapped.ToJsonString(PrettyJson);
            System.IO.File.WriteAllText(path, json, new UTF8Encoding(false));

            _log.Info($"[DIAG] Mapped chat saved: {path}");
        }
        catch (Exception e)
        {
            _log.Error($"[DIAG] Failed to save mapped chat '{chatId}': {e.Message}");
        }
    }
}
