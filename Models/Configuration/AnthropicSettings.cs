namespace PrdAiAssistant.Api.Models.Configuration;

public class AnthropicSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-sonnet-4-20250514";
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 0.7;
    public int MaxConversationTurns { get; set; } = 50;
}
