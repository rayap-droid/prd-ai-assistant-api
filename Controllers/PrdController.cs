using Microsoft.AspNetCore.Mvc;
using PrdAiAssistant.Api.Models.DTOs;
using PrdAiAssistant.Api.Services;

namespace PrdAiAssistant.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class PrdController : ControllerBase
{
    private readonly ConversationManager _conversations;
    private readonly ClaudeService _claude;
    private readonly PrdTemplateEngine _templates;
    private readonly JiraService _jira;
    private readonly ILogger<PrdController> _log;

    public PrdController(ConversationManager conversations, ClaudeService claude, PrdTemplateEngine templates, JiraService jira, ILogger<PrdController> log)
    {
        _conversations = conversations;
        _claude = claude;
        _templates = templates;
        _jira = jira;
        _log = log;
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GeneratePrdRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId)) return BadRequest(new { error = "SessionId is required." });
        if (!_conversations.TryGetSession(request.SessionId, out var session))
            return NotFound(new { error = $"Session '{request.SessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        if (session.ExtractedData.Count == 0) return BadRequest(new { error = "No interview data collected yet." });
        try
        {
            var aiMarkdown = await _claude.GeneratePrdAsync(session.ExtractedData, template, ct);
            var prd = _templates.BuildPrdFromData(session.Id, session.ExtractedData, template, aiMarkdown);
            _conversations.MarkPrdGenerated(session.Id);
            return Ok(prd);
        }
        catch (ClaudeApiException)
        {
            var fallback = _templates.BuildPrdFromData(session.Id, session.ExtractedData, template);
            _conversations.MarkPrdGenerated(session.Id);
            return Ok(fallback);
        }
    }

    [HttpPost("generate/quick")]
    public IActionResult GenerateQuick([FromBody] GeneratePrdRequest request)
    {
        if (!_conversations.TryGetSession(request.SessionId, out var session))
            return NotFound(new { error = $"Session '{request.SessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        return Ok(_templates.BuildPrdFromData(session.Id, session.ExtractedData, template));
    }

    [HttpPut("section")]
    public IActionResult UpdateSection([FromBody] UpdatePrdSectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId)) return BadRequest(new { error = "SessionId is required." });
        if (string.IsNullOrWhiteSpace(request.SectionKey)) return BadRequest(new { error = "SectionKey is required." });
        if (!_conversations.TryGetSession(request.SessionId, out var session))
            return NotFound(new { error = $"Session '{request.SessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        var validKeys = template.Sections.Select(s => s.Key).ToHashSet();
        if (!validKeys.Contains(request.SectionKey))
            return BadRequest(new { error = $"Unknown section key '{request.SectionKey}'.", validKeys = validKeys.ToList() });
        return Ok(_templates.UpdateSection(session.Id, session.ExtractedData, template, request.SectionKey, request.Content));
    }

    [HttpGet("{sessionId}/export/markdown")]
    public IActionResult ExportMarkdown(string sessionId)
    {
        if (!_conversations.TryGetSession(sessionId, out var session))
            return NotFound(new { error = $"Session '{sessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        var prd = _templates.BuildPrdFromData(session.Id, session.ExtractedData, template);
        return File(System.Text.Encoding.UTF8.GetBytes(prd.Markdown), "text/markdown", SanitizeFileName($"PRD-{prd.Title}.md"));
    }

    [HttpGet("{sessionId}/export/html")]
    public IActionResult ExportHtml(string sessionId)
    {
        if (!_conversations.TryGetSession(sessionId, out var session))
            return NotFound(new { error = $"Session '{sessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        var prd = _templates.BuildPrdFromData(session.Id, session.ExtractedData, template);
        return File(System.Text.Encoding.UTF8.GetBytes(prd.Html), "text/html", SanitizeFileName($"PRD-{prd.Title}.html"));
    }

    [HttpPost("submit/jira")]
    public async Task<IActionResult> SubmitToJira([FromBody] JiraSubmitRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId)) return BadRequest(new { error = "SessionId is required." });
        if (string.IsNullOrWhiteSpace(request.ProjectKey)) return BadRequest(new { error = "ProjectKey is required." });
        if (!_conversations.TryGetSession(request.SessionId, out var session))
            return NotFound(new { error = $"Session '{request.SessionId}' not found." });
        var template = _templates.LoadTemplate(session!.TemplateName);
        var issueType = request.IssueType ?? "Story";
        var (valid, validationError) = await _jira.ValidateProjectAsync(request.ProjectKey, issueType, ct);
        if (!valid) return BadRequest(new { error = validationError });
        var prd = _templates.BuildPrdFromData(session.Id, session.ExtractedData, template);
        var result = await _jira.CreateIssueAsync(request, prd, ct);
        if (result.Success) _conversations.MarkSubmittedToJira(session.Id);
        return Ok(result);
    }

    [HttpGet("jira/projects")]
    public async Task<IActionResult> GetJiraProjects(CancellationToken ct) => Ok(await _jira.GetProjectsAsync(ct));

    [HttpGet("templates")]
    public IActionResult ListTemplates() => Ok(_templates.ListTemplates());

    [HttpPost("templates/reload")]
    public IActionResult ReloadTemplates() { _templates.ClearCache(); return NoContent(); }

    private static string SanitizeFileName(string name) => string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '-');
}
