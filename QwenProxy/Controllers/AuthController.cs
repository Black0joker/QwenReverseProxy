using Microsoft.AspNetCore.Mvc;
using QwenProxy.Config;

namespace QwenProxy.Controllers;

[ApiController]
[Route("api/v1/auths")]
public sealed class AuthController : ControllerBase
{
    private readonly CookieStore _cookieStore;

    public AuthController(CookieStore cookieStore) => _cookieStore = cookieStore;

    /// <summary>Get authenticated user info.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        var result = await client.MeAsync(ct);
        return new JsonResult(result);
    }
}
