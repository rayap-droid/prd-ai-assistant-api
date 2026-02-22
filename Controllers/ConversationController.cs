using Microsoft.AspNetCore.Mvc;
using PrdAiAssistant.Api.Models.DTOs;
using PrdAiAssistant.Api.Models.Enums;
using PrdAiAssistant.Api.Services;

namespace PrdAiAssistant.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ConversationController : ControllerBase
{
    private readonly ConversationManager _conversations;
    private readonly ClaudeService _claude;
    private readonly PrdTemplateEngine _templates;
    private readonly ILogger<ConversationController> _log;

    public ConversationController(ConversationManager conversations, ClaudeService claude, PrdTemplateEngine templates, ILogger<ConversationController> log)
    {
        _conversations = conversations;
        _claude = claude;
        _templates = templates;
        _log = log;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartConversationRequest request, CancellationToken ct)
    {
        var session = _conversations.CreateSession(request.TemplateName, request.ProjectContext);
        var template = _templates.LoadTemplate(session.TemplateName);
        var welcomePrompt = BuildWelcomePrompt(request.ProjectContext);
        session.AddMessage("user", welcomePrompt);
        var response = await _claude.ChatAsync(session, template, ct);
        session.AddMessage("assistant", response.Reply);
        session.Messages.RemoveAt(0);
        _conversations.ProcessClaudeResponse(session.Id, response, template);
        return Ok(new StartConversationResponse(session.Id, response.Reply, session.CurrentPhase));
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] ChatMessageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return BadRequest(new { error = "Message cannot be empty." });
        if (string.IsNullOrWhiteSpace(request.SessionId)) return BadRequest(new { error = "SessionId is required." });
        try
        {
            _conversations.AddUserMessage(request.SessionId, request.Message);
            var session = _conversations.GetSession(request.SessionId);
            var template = _templates.LoadTemplate(session.TemplateName);
            var response = await _claude.ChatAsync(session, template, ct);
            session.AddMessage("assistant", response.Reply);
            var result = _conversations.ProcessClaudeResponse(request.SessionId, response, template);
            PrdPreview? preview = null;
            if (session.ExtractedData.Count > 0) preview = _conversations.BuildPreview(session, template);
            return Ok(new ChatMessageResponse(session.Id, response.Reply, result.CurrentPhase, result.CompletionPercent, result.IsInterviewComplete, preview));
        }
        catch (SessionNotFoundException) { return NotFound(new { error = $"Session '{request.SessionId}' not found." }); }
        catch (SessionExpiredException) { return BadRequest(new { error = $"Session '{request.SessionId}' has expired." }); }
        catch (ClaudeApiException ex) { _log.LogError(ex, "Claude API error"); return StatusCode(502, new { error = "AI service temporarily unavailable.", detail = ex.Message }); }
    }

    [HttpGet("{sessionId}")]
    public IActionResult GetSession(string sessionId)
    {
        if (!_conversations.TryGetSession(sessionId, out var session))
            return NotFound(new { error = $"Session '{sessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        var preview = session.ExtractedData.Count > 0 ? _conversations.BuildPreview(session, template) : null;
        return Ok(new ConversationDetailResponse(
            new ConversationInfo(session.Id, session.Status, session.CurrentPhase, session.Messages.Count, session.CreatedAt, session.LastActivity),
            session.Messages.Select(m => new MessageDto(m.Role, m.Content, m.Timestamp)).ToList(),
            preview, session.ExtractedData.Keys.ToList()));
    }

    [HttpGet]
    public IActionResult ListSessions() => Ok(_conversations.ListActiveSessions());

    [HttpDelete("{sessionId}")]
    public IActionResult CancelSession(string sessionId)
    {
        try { _conversations.CancelSession(sessionId); return NoContent(); }
        catch (SessionNotFoundException) { return NotFound(new { error = $"Session '{sessionId}' not found." }); }
    }

    [HttpGet("{sessionId}/preview")]
    public IActionResult GetPreview(string sessionId)
    {
        if (!_conversations.TryGetSession(sessionId, out var session))
            return NotFound(new { error = $"Session '{sessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        return Ok(_conversations.BuildPreview(session, template));
    }
[HttpPost("/api/prd/generate")]
    public async Task<IActionResult> GeneratePrd([FromBody] GeneratePrdRequest request, CancellationToken ct)
    {
        if (!_conversations.TryGetSession(request.SessionId, out var session))
            return NotFound(new { error = $"Session '{request.SessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        
        // Ask Claude to generate the full PRD
        session.AddMessage("user", "Based on everything we've discussed, please generate a complete, professional PRD in markdown format. Include all sections with the information gathered so far. For sections where information is missing, add placeholder notes.");
        var response = await _claude.ChatAsync(session, template, ct);
        session.AddMessage("assistant", response.Reply);
        
        var prd = _templates.BuildPrdFromData(session.Id, session.ExtractedData, template, response.Reply);
        return Ok(prd);
    }

    [HttpPost("/api/prd/generate/quick")]
    public IActionResult GenerateQuickPrd([FromBody] GeneratePrdRequest request)
    {
        if (!_conversations.TryGetSession(request.SessionId, out var session))
            return NotFound(new { error = $"Session '{request.SessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        var prd = _templates.BuildPrdFromData(session.Id, session.ExtractedData, template);
        return Ok(prd);
    }

    [HttpGet("/api/prd/{sessionId}/export/markdown")]
    public IActionResult ExportMarkdown(string sessionId)
    {
        if (!_conversations.TryGetSession(sessionId, out var session))
            return NotFound(new { error = $"Session '{sessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        var prd = _templates.BuildPrdFromData(session.Id, session.ExtractedData, template);
        return File(System.Text.Encoding.UTF8.GetBytes(prd.Markdown), "text/markdown", "PRD.md");
    }

    [HttpGet("/api/prd/{sessionId}/export/html")]
    public IActionResult ExportHtml(string sessionId)
    {
        if (!_conversations.TryGetSession(sessionId, out var session))
            return NotFound(new { error = $"Session '{sessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        var prd = _templates.BuildPrdFromData(session.Id, session.ExtractedData, template);
        return File(System.Text.Encoding.UTF8.GetBytes(prd.Html), "text/html", "PRD.html");
    }

[HttpPost("suggest")]
    public async Task<IActionResult> SuggestAnswer([FromBody] SuggestAnswerRequest request, CancellationToken ct)
    {
        if (!_conversations.TryGetSession(request.SessionId, out var session))
            return NotFound(new { error = $"Session '{request.SessionId}' not found." });

        var lastAssistantMsg = session!.Messages.LastOrDefault(m => m.Role == "assistant")?.Content ?? "";
        
        var suggestPrompt = $@"You are helping auto-answer an interview question for a PRD. 
Based on the feature request context and any information you can find, suggest a comprehensive answer.

FEATURE REQUEST CONTEXT:
{request.FeatureRequestContext}

CURRENT INTERVIEW QUESTION:
{lastAssistantMsg}

Please provide a detailed, professional answer to this interview question. 
Be specific and include concrete details where possible.
Write the answer as if you are the stakeholder being interviewed.
Keep it concise but thorough - 2-4 paragraphs.";

        try
        {
            var httpFactory = HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
            var client = httpFactory.CreateClient("Anthropic");
            
            var body = new
            {
                model = "claude-sonnet-4-20250514",
                max_tokens = 1000,
                messages = new[] { new { role = "user", content = suggestPrompt } }
            };

            var resp = await client.PostAsJsonAsync("v1/messages", body, ct);
            var json = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
            var suggestion = json.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
            
            return Ok(new { suggestion });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Suggest answer failed");
            return StatusCode(500, new { error = "Failed to generate suggestion." });
        }
    }

    private static string BuildWelcomePrompt(string? projectContext)
    {
        var ctx = string.IsNullOrEmpty(projectContext) ? "" : $" The project context is: {projectContext}";
        return $"Please introduce yourself and begin the stakeholder interview to gather requirements for a PRD.{ctx} Start with the Discovery phase - ask about the problem being solved and who the target users are.";
    }
}

public record ConversationDetailResponse(ConversationInfo Info, List<MessageDto> Messages, PrdPreview? Preview, List<string> ExtractedKeys);
public record MessageDto(string Role, string Content, DateTime Timestamp);
public record GeneratePrdRequest(string SessionId);
public record SuggestAnswerRequest(string SessionId, string FeatureRequestContext);
