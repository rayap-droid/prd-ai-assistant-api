namespace PrdAiAssistant.Api.Models.DTOs;

public record JiraSubmitRequest(
    string SessionId,
    string ProjectKey,
    string? IssueType = "Story",
    string? Summary = null,
    string? EpicKey = null,
    List<string>? Labels = null,
    string? AssigneeAccountId = null
);

public record JiraSubmitResponse(
    string IssueKey,
    string IssueUrl,
    string Summary,
    bool Success,
    string? Error = null
);

public record JiraProjectInfo(
    string Key,
    string Name,
    List<string> IssueTypes
);
