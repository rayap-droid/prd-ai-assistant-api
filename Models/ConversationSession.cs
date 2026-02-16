namespace PrdAiAssistant.Api.Models;

using PrdAiAssistant.Api.Models.Enums;

public class ConversationSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;
    public InterviewPhase CurrentPhase { get; set; } = InterviewPhase.Discovery;
    public List<ChatMessage> Messages { get; set; } = [];
    public Dictionary<string, string> ExtractedData { get; set; } = [];
    public string TemplateName { get; set; } = "default-prd-template.json";
    public string? ProjectContext { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    public void AddMessage(string role, string content)
    {
        Messages.Add(new ChatMessage(role, content, DateTime.UtcNow));
        LastActivity = DateTime.UtcNow;
    }

    public int TurnCount => Messages.Count(m => m.Role == "user");
}

public record ChatMessage(
    string Role,
    string Content,
    DateTime Timestamp
);
