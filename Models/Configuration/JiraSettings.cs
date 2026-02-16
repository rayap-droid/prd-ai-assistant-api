namespace PrdAiAssistant.Api.Models.Configuration;

public class JiraSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string DefaultProjectKey { get; set; } = string.Empty;
    public string DefaultIssueType { get; set; } = "Story";
}
