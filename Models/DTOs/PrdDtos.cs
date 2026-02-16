namespace PrdAiAssistant.Api.Models.DTOs;

public record GeneratePrdRequest(
    string SessionId
);

public record PrdDocument(
    string SessionId,
    string Title,
    string Markdown,
    string Html,
    Dictionary<string, string> Sections,
    double CompletenessScore,
    List<string> Gaps,
    DateTime GeneratedAt
);

public record UpdatePrdSectionRequest(
    string SessionId,
    string SectionKey,
    string Content
);
