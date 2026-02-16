using System.Collections.Concurrent;
using PrdAiAssistant.Api.Models;
using PrdAiAssistant.Api.Models.DTOs;
using PrdAiAssistant.Api.Models.Enums;

namespace PrdAiAssistant.Api.Services;

public class ConversationManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ConversationSession> _sessions = new();
    private readonly ILogger<ConversationManager> _log;
    private readonly Timer _cleanupTimer;

    private static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);

    private static readonly InterviewPhase[] PhaseOrder =
    [
        InterviewPhase.Discovery,
        InterviewPhase.Requirements,
        InterviewPhase.Technical,
        InterviewPhase.AcceptanceCriteria,
        InterviewPhase.Review
    ];

    public ConversationManager(ILogger<ConversationManager> log)
    {
        _log = log;
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, CleanupInterval, CleanupInterval);
    }

    public ConversationSession CreateSession(string? templateName = null, string? projectContext = null)
    {
        var session = new ConversationSession
        {
            TemplateName = templateName ?? "custom-prd-template.json",
            ProjectContext = projectContext
        };
        if (!_sessions.TryAdd(session.Id, session))
            throw new InvalidOperationException("Session ID collision");
        _log.LogInformation("Session created: {Id}", session.Id);
        return session;
    }

    public ConversationSession GetSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new SessionNotFoundException(sessionId);
        if (session.Status == ConversationStatus.Expired)
            throw new SessionExpiredException(sessionId);
        session.LastActivity = DateTime.UtcNow;
        return session;
    }

    public bool TryGetSession(string sessionId, out ConversationSession? session)
    {
        return _sessions.TryGetValue(sessionId, out session)
            && session!.Status != ConversationStatus.Expired;
    }

    public List<ConversationInfo> ListActiveSessions()
    {
        return _sessions.Values
            .Where(s => s.Status == ConversationStatus.Active)
            .OrderByDescending(s => s.LastActivity)
            .Select(ToInfo)
            .ToList();
    }

    public void CancelSession(string sessionId)
    {
        var session = GetSession(sessionId);
        session.Status = ConversationStatus.Cancelled;
    }

    public void AddUserMessage(string sessionId, string message)
    {
        var session = GetSession(sessionId);
        ValidateCanChat(session);
        session.AddMessage("user", message);
    }

    public void AddAssistantMessage(string sessionId, string reply)
    {
        var session = GetSession(sessionId);
        session.AddMessage("assistant", reply);
    }

    public ProcessResult ProcessClaudeResponse(string sessionId, ClaudeResponse response, PrdTemplate template)
    {
        var session = GetSession(sessionId);
        foreach (var kv in response.ExtractedData)
        {
            if (session.ExtractedData.TryGetValue(kv.Key, out var existing))
                session.ExtractedData[kv.Key] = MergeContent(existing, kv.Value);
            else
                session.ExtractedData[kv.Key] = kv.Value;
        }
        if (response.SuggestedPhase.HasValue && response.SuggestedPhase.Value != session.CurrentPhase)
            TransitionPhase(session, response.SuggestedPhase.Value);

        var completion = CalculateCompletion(session, template);
        var missing = GetMissingSections(session, template);
        var isComplete = completion >= 90.0 && session.CurrentPhase == InterviewPhase.Review;

        return new ProcessResult(completion, missing, isComplete, session.CurrentPhase);
    }

    public void ApplyPhaseTransition(string sessionId, InterviewPhase newPhase)
    {
        var session = GetSession(sessionId);
        if (newPhase != session.CurrentPhase) TransitionPhase(session, newPhase);
    }

    public void MarkPrdGenerated(string sessionId)
    {
        GetSession(sessionId).Status = ConversationStatus.PrdGenerated;
    }

    public void MarkSubmittedToJira(string sessionId)
    {
        GetSession(sessionId).Status = ConversationStatus.SubmittedToJira;
    }

    public double CalculateCompletion(ConversationSession session, PrdTemplate template)
    {
        if (template.Sections.Count == 0) return 0;
        var totalWeight = template.Sections.Sum(s => s.Required ? 2.0 : 1.0);
        var filledWeight = template.Sections
            .Where(s => session.ExtractedData.ContainsKey(s.Key))
            .Sum(s => s.Required ? 2.0 : 1.0);
        var phaseIdx = Array.IndexOf(PhaseOrder, session.CurrentPhase);
        var phaseBonus = phaseIdx >= 0 ? (phaseIdx / (double)(PhaseOrder.Length - 1)) * 10.0 : 0;
        return Math.Min(100.0, Math.Round((filledWeight / totalWeight) * 90.0 + phaseBonus, 1));
    }

    public List<string> GetMissingSections(ConversationSession session, PrdTemplate template)
    {
        return template.Sections
            .Where(s => s.Required && !session.ExtractedData.ContainsKey(s.Key))
            .OrderBy(s => s.Order)
            .Select(s => s.Title)
            .ToList();
    }

    public PrdPreview BuildPreview(ConversationSession session, PrdTemplate template)
    {
        var sections = new Dictionary<string, string>();
        foreach (var sec in template.Sections.OrderBy(s => s.Order))
        {
            sections[sec.Title] = session.ExtractedData.TryGetValue(sec.Key, out var val)
                ? val : "[Not yet covered]";
        }
        return new PrdPreview(sections, GetMissingSections(session, template), CalculateCompletion(session, template));
    }

    private void TransitionPhase(ConversationSession session, InterviewPhase newPhase)
    {
        _log.LogInformation("Session {Id} phase: {From} -> {To}", session.Id, session.CurrentPhase, newPhase);
        session.CurrentPhase = newPhase;
    }

    private static void ValidateCanChat(ConversationSession session)
    {
        if (session.Status != ConversationStatus.Active)
            throw new InvalidOperationException($"Session {session.Id} is {session.Status}");
    }

    private static string MergeContent(string existing, string newContent)
    {
        if (existing.Contains(newContent, StringComparison.OrdinalIgnoreCase)) return existing;
        if (newContent.Contains(existing, StringComparison.OrdinalIgnoreCase)) return newContent;
        return $"{existing}\n\n{newContent}";
    }

    private static ConversationInfo ToInfo(ConversationSession s) => new(
        s.Id, s.Status, s.CurrentPhase, s.Messages.Count, s.CreatedAt, s.LastActivity);

    private void CleanupExpiredSessions(object? state)
    {
        var cutoff = DateTime.UtcNow - SessionTimeout;
        foreach (var id in _sessions.Where(kv => kv.Value.LastActivity < cutoff && kv.Value.Status == ConversationStatus.Active).Select(kv => kv.Key).ToList())
        {
            if (_sessions.TryGetValue(id, out var session)) session.Status = ConversationStatus.Expired;
        }
        var removeCutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        foreach (var id in _sessions.Where(kv => kv.Value.Status is ConversationStatus.Expired or ConversationStatus.Cancelled && kv.Value.LastActivity < removeCutoff).Select(kv => kv.Key).ToList())
        {
            _sessions.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}

public record ProcessResult(double CompletionPercent, List<string> MissingSections, bool IsInterviewComplete, InterviewPhase CurrentPhase);

public class SessionNotFoundException : Exception
{
    public SessionNotFoundException(string sessionId) : base($"Session '{sessionId}' not found.") { }
}

public class SessionExpiredException : Exception
{
    public SessionExpiredException(string sessionId) : base($"Session '{sessionId}' has expired.") { }
}

