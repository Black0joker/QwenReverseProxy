using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using QwenProxy.Config;
using QwenProxy.Logging;
using QwenProxy.Middleware;
using QwenProxy.Models;
using QwenProxy.Services;
using QwenProxy.Sse;

namespace QwenProxy.Controllers;

[ApiController]
[Route("api/v1/chat")]
[EnableRateLimiting(RateLimitPolicies.Completions)]
public sealed class ChatCompletionsController : ControllerBase
{
    private readonly CookieStore _cookieStore;
    private readonly string _logBaseDir;
    private readonly ChatExchangeLogger _exchangeLogger;
    private readonly ITokenEncryptionService _encryptionService;

    /// <summary>
    /// Action tools that keep the "action" event type; every other tool is
    /// reclassified as a "tool" event when re-emitted to the client.
    /// </summary>
    private static readonly HashSet<string> NonToolActions = new() { "user_response", "ask_user" };

    public ChatCompletionsController(
        CookieStore cookieStore, IWebHostEnvironment env, ChatExchangeLogger exchangeLogger,
        ITokenEncryptionService encryptionService)
    {
        _cookieStore = cookieStore;
        _logBaseDir = env.ContentRootPath;
        _exchangeLogger = exchangeLogger;
        _encryptionService = encryptionService;
    }

    /// <summary>
    /// Decrypts each encrypted fileId from the request, parses the resulting
    /// JSON strings back into JsonObjects, and returns them as the files payload.
    /// </summary>
    private List<JsonObject>? BuildFilesPayload(SendPromptRequest body)
    {
        if (body.FileIds is null || body.FileIds.Count == 0) return null;

        var filesPayload = new List<JsonObject>();

        foreach (string encryptedId in body.FileIds)
        {
            if (string.IsNullOrWhiteSpace(encryptedId)) continue;

            try
            {
                string json = _encryptionService.Decrypt(encryptedId);
                var fileObj = JsonNode.Parse(json)?.AsObject()
                    ?? throw new InvalidOperationException("Decrypted fileId is not a JSON object.");
                filesPayload.Add(fileObj);
            }
            catch (FormatException e)
            {
                throw new HttpApiException(400, $"Invalid fileId encoding: {e.Message}");
            }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                throw new HttpApiException(400, $"Failed to decrypt fileId: {e.Message}");
            }
        }

        return filesPayload.Count > 0 ? filesPayload : null;
    }

    /// <summary>Reclassify 'action' events as 'tool' unless the tool is user_response/ask_user.</summary>
    private static JsonObject ClassifyEvent(SseEvent ev)
    {
        var json = ev.ToJson();
        if (ev.Type == "action" && ev.Value is JsonObject action)
        {
            string? tool = null;
            try { tool = action["tool"]?.GetValue<string>(); }
            catch { /* non-string tool */ }
            if (tool is null || !NonToolActions.Contains(tool))
            {
                json["type"] = "tool";
            }
        }
        return json;
    }

    /// <summary>Send a prompt (SSE streaming, parsed via StreamEventsPipeline).</summary>
    [HttpPost("completions")]
    public async Task Completions([FromBody] SendPromptRequest body, CancellationToken ct)
    {
        if (!ModelMapper.IsDarktechAlias(body.Model))
            throw new HttpApiException(400,
                $"Unsupported model '{body.Model}'. Allowed models: {ModelMapper.AllowedModels}.");

        TokenClass tokenClass = TokenSupport.ExtractTokenClass(HttpContext);
        string token = tokenClass.Token;
        var client = new QwenClient(body.Model, body.Mode, token, tokenClass.S5Token, _cookieStore);

        // Extract the whole solved captcha cookies from the request body;
        // they are attached to the upstream request as cookies. Falls back to
        // the legacy single 'x5sec' field when the dictionary is not supplied.

        // Resolve attachments: decrypt the encrypted fileId into the upstream file object.
        var filesPayload = BuildFilesPayload(body);

        // Structured request/response logging for this exchange.
        var exchange = _exchangeLogger.Begin(body, filesPayload);

        SendPromptResult result;
        try
        {
            result = await client.SendPromptAsync(
                body.Prompt, body.ChatId, body.NormalizedParentMessageId, filesPayload, ct: ct);
        }
        catch (HttpRequestException e)
        {
            _exchangeLogger.Fail(exchange, "upstream_error", e.Message);
            throw new HttpApiException(502, $"Upstream Qwen request failed: {e.Message}");
        }

        // Captcha path returned by module.send_prompt
        if (result.Captcha is not null)
        {
            // Encrypt the captcha URL before returning it to the client
            var urlNode = result.Captcha["url"];
            if (urlNode is not null)
            {
                string plainUrl = urlNode.GetValue<string>();
                if (!string.IsNullOrEmpty(plainUrl))
                {
                    result.Captcha["url"] = _encryptionService.Encrypt(plainUrl);
                }
            }
            _exchangeLogger.CompleteJson(exchange, "captcha", result.Captcha);
            throw new HttpApiException(429, result.Captcha);
        }

        // Non-streaming JSON response (e.g. upstream error)
        if (result.JsonPayload is not null || result.StreamResponse is null)
        {
            _exchangeLogger.CompleteJson(exchange, "json", result.JsonPayload);
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(result.JsonPayload, cancellationToken: ct);
            return;
        }

        // Parse the upstream Qwen SSE into structured protocol events via
        // StreamEventsPipeline (parse_stream -> parse_answer). Each parsed event
        // is re-emitted as its own SSE `data:` line.
        using (result.StreamResponse)
        {
            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = "text/event-stream";
            HttpContext.Response.Headers.CacheControl = "no-cache";
            HttpContext.Response.Headers["X-Accel-Buffering"] = "no";

            _exchangeLogger.SetUpstream(
                exchange,
                (int)result.StreamResponse.StatusCode,
                result.StreamResponse.Content.Headers.ContentType?.MediaType);

            var responseStream = await result.StreamResponse.Content.ReadAsStreamAsync(ct);
            var responseBody = HttpContext.Response.Body;

            // Buffer per-character stream_text events and flush in chunks so we
            // don't emit one SSE line per character.
            var textBuf = new StringBuilder();
            const int textFlushAt = 16;

            async Task WriteSseAsync(JsonObject obj)
            {
                byte[] bytes = Encoding.UTF8.GetBytes($"data: {obj.ToJsonString()}\n\n");
                await responseBody.WriteAsync(bytes, ct);
                await responseBody.FlushAsync(ct);
            }

            async Task FlushTextAsync()
            {
                if (textBuf.Length == 0) return;
                var merged = new JsonObject
                {
                    ["type"] = "stream_text",
                    ["value"] = textBuf.ToString(),
                };
                textBuf.Clear();
                await WriteSseAsync(merged);
            }

            try
            {
                await foreach (var ev in StreamEventsPipeline.StreamEvents(responseStream, _logBaseDir, ct))
                {
                    _exchangeLogger.TrackEvent(exchange, ev.Type ?? "?");
                    if (ev.Type == "final" && ev.Value is not null)
                        _exchangeLogger.CaptureFinal(exchange, ev.Value);
                    else if (ev.Type == "error" && ev.Value is not null)
                        _exchangeLogger.CaptureErrorEvent(exchange, ev.Value);
                    else if (ev.Type == "tokens" && ev.Value is not null)
                        _exchangeLogger.CaptureUsage(exchange, ev.Value);

                    if (ev.Type == "stream_text")
                    {
                        string? val = ev.Value?.GetValue<string>();
                        if (val is not null) textBuf.Append(val);
                        if (textBuf.Length >= textFlushAt) await FlushTextAsync();
                        continue;
                    }

                    await FlushTextAsync();
                    await WriteSseAsync(ClassifyEvent(ev));
                }

                await FlushTextAsync();
                _exchangeLogger.Complete(exchange);
            }
            catch (Exception streamErr)
            {
                _exchangeLogger.Fail(exchange, "stream_failed", streamErr.Message);
                throw;
            }
        }
    }

    /// <summary>Stop prompt generation.</summary>
    [HttpPost("completions/stop")]
    public async Task<ActionResult<StopCompletionResponse>> StopCompletion(
        [FromBody] StopCompletionRequest body, CancellationToken ct)
    {
        var client = TokenSupport.CreateClient(HttpContext, _cookieStore);
        try
        {
            await client.StopSendingPromptAsync(body.ChatId, body.ParentMessageId, ct);
            return new StopCompletionResponse { Success = true };
        }
        catch (HttpRequestException e)
        {
            throw new HttpApiException(502, $"Upstream Qwen request failed: {e.Message}");
        }
    }
}
