using Microsoft.AspNetCore.Mvc;
using QwenProxy.Config;
using QwenProxy.Models;

namespace QwenProxy.Controllers;

[ApiController]
[Route("api/v1/chats")]
public sealed class ChatsController : ControllerBase
{
    private readonly CookieStore _cookieStore;

    public ChatsController(CookieStore cookieStore) => _cookieStore = cookieStore;

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

    /// <summary>Fetch all chats with full history.</summary>
    [HttpGet("all")]
    public async Task<IActionResult> FetchAllChats(CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        var data = await client.FetchAllChatsAsync(ct);
        return new JsonResult(data);
    }

    /// <summary>Get chat parent/current ID.</summary>
    [HttpGet("{chatId}/parent")]
    public async Task<ActionResult<ChatParentIdResponse>> GetChatParentId(string chatId, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        string? parentId = await client.GetChatParentIdAsync(chatId, ct);
        return new ChatParentIdResponse { ParentId = parentId };
    }

    /// <summary>Get full chat history.</summary>
    [HttpGet("{chatId}/history")]
    public async Task<IActionResult> GetChatHistory(string chatId, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        var data = await client.GetChatHistoryAsync(chatId, ct);
        return new JsonResult(data);
    }

    /// <summary>Get chat messages.</summary>
    [HttpGet("{chatId}/messages")]
    public async Task<IActionResult> GetChatMessages(string chatId, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        var data = await client.GetChatMessagesAsync(chatId, ct);
        return new JsonResult(data);
    }

    /// <summary>Delete a chat session.</summary>
    [HttpDelete("{chatId}")]
    public async Task<IActionResult> DeleteChat(string chatId, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        var data = await client.DeleteChatAsync(chatId, ct);
        return new JsonResult(data);
    }

    /// <summary>Import/upload a chat.</summary>
    [HttpPost("import")]
    public async Task<IActionResult> ImportChat([FromBody] ImportChatRequest body, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        var data = await client.UploadChatAsync(body.Data, ct);
        return new JsonResult(data);
    }
}
