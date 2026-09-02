using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using QwenProxy.Config;
using QwenProxy.Middleware;
using QwenProxy.Models;
using QwenProxy.Services;

namespace QwenProxy.Controllers;

[ApiController]
[Route("api/v1/files")]
[EnableRateLimiting(RateLimitPolicies.Files)]
public sealed class FilesController : ControllerBase
{
    private readonly CookieStore _cookieStore;

    public FilesController(CookieStore cookieStore) => _cookieStore = cookieStore;

    /// <summary>Upload text content as a file.</summary>
    [HttpPost("upload")]
    public async Task<ActionResult<UploadFileResponse>> UploadFileContent(
        [FromBody] UploadFileContentRequest body, CancellationToken ct)
    {
        string token = TokenSupport.ExtractToken(HttpContext);
        try
        {
            var fileObj = await QwenFileUpload.UploadFileContentAsync(
                body.Content, body.Filename, token, ct);

            return new UploadFileResponse
            {
                File = fileObj,
                Url = fileObj["url"]?.GetValue<string>() ?? "",
                Filename = fileObj["name"]?.GetValue<string>() ?? body.Filename,
                Size = fileObj["size"]?.GetValue<long>() ?? 0,
            };
        }
        catch (Exception e) when (e is not HttpApiException)
        {
            throw new HttpApiException(502, e.Message);
        }
    }

    /// <summary>Upload an image file.</summary>
    [HttpPost("upload-image")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage(IFormFile file, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);

        try
        {
            string filename = string.IsNullOrEmpty(file.FileName) ? "image.png" : file.FileName;
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var fileObj = await client.UploadImageAsync(ms.ToArray(), filename, ct);
            return new JsonResult(fileObj);
        }
        catch (HttpRequestException e)
        {
            throw new HttpApiException(502, $"Upstream Qwen request failed: {e.Message}");
        }
        catch (Exception e) when (e is not HttpApiException)
        {
            throw new HttpApiException(500, e.Message);
        }
    }
}
