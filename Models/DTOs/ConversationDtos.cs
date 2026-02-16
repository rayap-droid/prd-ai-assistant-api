namespace PrdAiAssistant.Api.Models.DTOs;

using PrdAiAssistant.Api.Models.Enums;

public record StartConversationRequest(
    string? TemplateName = null,
    string? ProjectContext = null
);

public record ChatMessageRequest(
    string SessionId,
    string Message
);

public record StartConversationResponse(
    string SessionId,
    string WelcomeMessage,
    InterviewPhase CurrentPhase
);

public record ChatMessageResponse(
    string SessionId,
    string Reply,
    InterviewPhase CurrentPhase,
    double CompletionPercent,
    bool IsInterviewComplete,
    PrdPreview? PrdPreview = null
);

public record PrdPreview(
    Dictionary<string, string> Sections,
    List<string> MissingSections,
    double CompletenessScore
);

public record ConversationInfo(
    string SessionId,
    ConversationStatus Status,
    InterviewPhase CurrentPhase,
    int MessageCount,
    DateTime CreatedAt,
    DateTime LastActivity
);
