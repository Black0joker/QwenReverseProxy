using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using QwenProxy.Config;
using QwenProxy.Logging;
using QwenProxy.Middleware;
using QwenProxy.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Qwen Proxy API",
        Version = "1.0.0",
        Description =
            "Reverse-proxy REST API for the Qwen Chat service. " +
            "Each endpoint maps directly to a function in module.py (QwenClient).",
    });

    // Bearer security scheme (port of HTTPBearer in api/dependencies.py): Swagger
    // UI renders an "Authorize" input; the token is forwarded to Qwen as a
    // `token` cookie.
    c.AddSecurityDefinition("QwenToken", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "Qwen JWT",
        In = ParameterLocation.Header,
        Description =
            "Paste your Qwen token. Swagger sends it as 'Authorization: Bearer <token>' " +
            "and the proxy forwards it to Qwen as a `token` cookie.",
    });

    // Global security requirement: without this, Swagger UI renders the
    // Authorize button but does NOT attach the Authorization header to requests.
    c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("QwenToken", document)] = []
    });
    });

// OpenAPI document for Scalar (Microsoft.OpenApi pipeline). Scalar's UI
// fetches it via its built-in "openapi/{documentName}.json" source route.
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info ??= new OpenApiInfo();
        document.Info.Title = "Qwen Proxy API";
        document.Info.Version = "1.0.0";
        document.Info.Description =
            "Reverse-proxy REST API for the Qwen Chat service. " +
            "Each endpoint maps directly to a function in module.py (QwenClient).";

        var components = document.Components ?? new OpenApiComponents();
        document.Components = components;
        components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        components.SecuritySchemes["QwenToken"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            In = ParameterLocation.Header,
            Scheme = "bearer",
            BearerFormat = "Qwen JWT",
            Name = "Authorization",
            Description =
                "Paste your Qwen token. The proxy forwards it to Qwen as a `token` cookie.",
        };
        return Task.CompletedTask;
    });
});

// Singletons shared across per-request QwenClient instances.
builder.Services.AddSingleton<ITokenEncryptionService, TokenEncryptionService>();
builder.Services.AddSingleton(_ => new CookieStore(builder.Environment.ContentRootPath));
builder.Services.AddSingleton(_ => new QwenLogger(builder.Environment.ContentRootPath));
builder.Services.AddSingleton(sp => new ChatExchangeLogger(
    sp.GetRequiredService<QwenLogger>(), builder.Environment.ContentRootPath));

var app = builder.Build();

// Global exception handling: the proxy ALWAYS answers with a clean JSON payload.
app.UseMiddleware<ErrorHandlingMiddleware>();

// AES token decryption: extracts Bearer token, decrypts, parses JSON -> TokenClass.
app.UseTokenDecryption();

// Swagger UI at /docs.
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Qwen Proxy API");
    c.RoutePrefix = "docs";
    c.DocumentTitle = "Qwen Proxy API";
});

// Scalar API reference. The UI itself lives at /scalar/v1 (one page per
// OpenAPI document); /scalar is a convenience redirect so both work.
app.MapOpenApi(); // serves /openapi/v1.json (Microsoft pipeline, used by Scalar)
app.MapScalarApiReference(options =>
{
    options
        .WithTitle("Qwen Proxy API")
        .AddDocument("v1", "Qwen Proxy API")
        .AddPreferredSecuritySchemes("QwenToken");
});
app.MapGet("/scalar", () => Results.Redirect("/scalar/v1"));

app.MapGet("/", () => new
{
    service = "Qwen Proxy API",
    docs = "/docs",
    scalar = "/scalar",
    openapi = "/swagger/v1/swagger.json",
});

app.MapControllers();

app.Run();
