namespace PrdAiAssistant.Api.Models.Configuration;

public class PrdTemplateSettings
{
    public string TemplateDirectory { get; set; } = "Templates";
    public string DefaultTemplate { get; set; } = "default-prd-template.json";
}
