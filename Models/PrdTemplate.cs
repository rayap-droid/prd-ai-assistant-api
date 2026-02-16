namespace PrdAiAssistant.Api.Models;

public class PrdTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public List<PrdSection> Sections { get; set; } = [];
}

public class PrdSection
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
    public int Order { get; set; }
    public string MappedPhase { get; set; } = "Discovery";
    public List<string> PromptHints { get; set; } = [];
}
