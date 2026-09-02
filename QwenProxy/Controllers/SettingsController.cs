using Microsoft.AspNetCore.Mvc;
using QwenProxy.Config;
using QwenProxy.Models;

namespace QwenProxy.Controllers;

[ApiController]
[Route("api/v1/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly CookieStore _cookieStore;

    public SettingsController(CookieStore cookieStore) => _cookieStore = cookieStore;

    /// <summary>Update account tool settings.</summary>
    [HttpPost]
    public async Task<IActionResult> UpdateSettings(CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        var result = await client.UpdateSettingsAsync(ct);
        return new JsonResult(result);
    }

    /// <summary>Update system prompt / instruction.</summary>
    [HttpPost("system-prompt")]
    public async Task<IActionResult> UpdateSystemPrompt([FromBody] SystemPromptRequest body, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        var result = await client.UpdateSystemPromptAsync(body.Instruction, ct);
        return new JsonResult(result);
    }
}
