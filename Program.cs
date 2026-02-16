using Microsoft.AspNetCore.RateLimiting;
using PrdAiAssistant.Api.Models.Configuration;
using PrdAiAssistant.Api.Services;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AnthropicSettings>(
    builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<JiraSettings>(
    builder.Configuration.GetSection("Jira"));
builder.Services.Configure<PrdTemplateSettings>(
    builder.Configuration.GetSection("PrdTemplate"));

builder.Services.AddHttpClient("Anthropic", client =>
{
    var cfg = builder.Configuration.GetSection("Anthropic");
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    client.DefaultRequestHeaders.Add("x-api-key", cfg["ApiKey"]);
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    client.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddHttpClient("Jira", client =>
{
    var cfg = builder.Configuration.GetSection("Jira");
    var baseUrl = cfg["BaseUrl"] ?? "https://your-domain.atlassian.net";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    var email = cfg["Email"] ?? "";
    var token = cfg["ApiToken"] ?? "";
    var creds = Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes($"{email}:{token}"));
    client.DefaultRequestHeaders.Add("Authorization", $"Basic {creds}");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

builder.Services.AddSingleton<ConversationManager>();
builder.Services.AddScoped<ClaudeService>();
builder.Services.AddScoped<PrdTemplateEngine>();
builder.Services.AddScoped<JiraService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opts =>
{
    opts.SwaggerDoc("v1", new()
    {
        Title = "PRD AI Assistant API",
        Version = "v1",
        Description = "AI-powered stakeholder interview system for generating PRDs"
    });
});

builder.Services.AddCors(opts =>
{
    opts.AddPolicy("Frontend", policy =>
    {
        var origins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? ["http://localhost:3000", "http://localhost:5173"];
        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddRateLimiter(opts =>
{
    opts.AddFixedWindowLimiter("api", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 5;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseRateLimiter();

app.UseExceptionHandler(error =>
{
    error.Run(async ctx =>
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = "An unexpected error occurred.",
            traceId = ctx.TraceIdentifier
        });
    });
});

app.MapControllers()
   .RequireRateLimiting("api");

app.Logger.LogInformation("PRD AI Assistant API starting on {Urls}",
    string.Join(", ", app.Urls));

app.Run();
