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

    private static string BuildWelcomePrompt(string? projectContext)
    {
        var ctx = string.IsNullOrEmpty(projectContext) ? "" : $" The project context is: {projectContext}";
        return $"Please introduce yourself and begin the stakeholder interview to gather requirements for a PRD.{ctx} Start with the Discovery phase - ask about the problem being solved and who the target users are.";
    }
}

public record ConversationDetailResponse(ConversationInfo Info, List<MessageDto> Messages, PrdPreview? Preview, List<string> ExtractedKeys);
public record MessageDto(string Role, string Content, DateTime Timestamp);
