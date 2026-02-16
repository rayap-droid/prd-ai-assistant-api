using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Markdig;
using Microsoft.Extensions.Options;
using PrdAiAssistant.Api.Models;
using PrdAiAssistant.Api.Models.Configuration;
using PrdAiAssistant.Api.Models.DTOs;

namespace PrdAiAssistant.Api.Services;

public class PrdTemplateEngine
{
    private readonly PrdTemplateSettings _settings;
    private readonly ILogger<PrdTemplateEngine> _log;
    private readonly MarkdownPipeline _mdPipeline;
    private static readonly ConcurrentDictionary<string, PrdTemplate> TemplateCache = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public PrdTemplateEngine(IOptions<PrdTemplateSettings> settings, ILogger<PrdTemplateEngine> log)
    {
        _settings = settings.Value;
        _log = log;
        _mdPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    }

    public PrdTemplate LoadTemplate(string? templateName = null)
    {
        var name = templateName ?? _settings.DefaultTemplate;
        if (TemplateCache.TryGetValue(name, out var cached)) return cached;
        var path = Path.Combine(_settings.TemplateDirectory, name);
        if (!File.Exists(path))
        {
            _log.LogWarning("Template '{Name}' not found, using built-in default", name);
            var fallback = GetBuiltInTemplate();
            TemplateCache[name] = fallback;
            return fallback;
        }
        try
        {
            var json = File.ReadAllText(path);
            var template = JsonSerializer.Deserialize<PrdTemplate>(json, JsonOpts)
                ?? throw new InvalidOperationException($"Template '{name}' deserialized to null");
            for (var i = 0; i < template.Sections.Count; i++)
                if (template.Sections[i].Order == 0) template.Sections[i].Order = i + 1;
            TemplateCache[name] = template;
            return template;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load template '{Name}'", name);
            var fallback = GetBuiltInTemplate();
            TemplateCache[name] = fallback;
            return fallback;
        }
    }

    public List<string> ListTemplates()
    {
        if (!Directory.Exists(_settings.TemplateDirectory))
            return ["default-prd-template.json (built-in)"];
        return Directory.GetFiles(_settings.TemplateDirectory, "*.json")
            .Select(Path.GetFileName).Where(f => f is not null).Cast<string>().ToList();
    }

    public void ClearCache()
    {
        TemplateCache.Clear();
        _log.LogInformation("Template cache cleared");
    }

    public PrdDocument BuildPrdFromData(string sessionId, Dictionary<string, string> extractedData, PrdTemplate template, string? aiGeneratedMarkdown = null)
    {
        string markdown;
        Dictionary<string, string> sections;
        if (!string.IsNullOrEmpty(aiGeneratedMarkdown))
        {
            markdown = aiGeneratedMarkdown;
            sections = MapMarkdownToSections(aiGeneratedMarkdown, template);
        }
        else
        {
            (markdown, sections) = BuildMarkdownFromTemplate(extractedData, template);
        }
        var html = ConvertToHtml(markdown);
        var gaps = GetGaps(extractedData, template);
        var score = CalculateCompleteness(extractedData, template);
        var title = extractedData.TryGetValue("title", out var t) ? t
            : extractedData.TryGetValue("problem_statement", out var p) ? TruncateTitle(p)
            : "Untitled PRD";
        return new PrdDocument(sessionId, title, markdown, html, sections, score, gaps, DateTime.UtcNow);
    }

    public PrdDocument UpdateSection(string sessionId, Dictionary<string, string> extractedData, PrdTemplate template, string sectionKey, string newContent)
    {
        extractedData[sectionKey] = newContent;
        return BuildPrdFromData(sessionId, extractedData, template);
    }

    private (string markdown, Dictionary<string, string> sections) BuildMarkdownFromTemplate(Dictionary<string, string> data, PrdTemplate template)
    {
        var sb = new StringBuilder();
        var sections = new Dictionary<string, string>();
        var title = data.TryGetValue("title", out var t) ? t : "Product Requirements Document";
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"**Template:** {template.Name}");
        sb.AppendLine($"**Completeness:** {CalculateCompleteness(data, template):F0}%");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Table of Contents");
        foreach (var sec in template.Sections.OrderBy(s => s.Order))
        {
            var status = data.ContainsKey(sec.Key) ? "+" : " ";
            sb.AppendLine($"- [{status}] {sec.Title}");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        foreach (var sec in template.Sections.OrderBy(s => s.Order))
        {
            sb.AppendLine($"## {sec.Title}");
            sb.AppendLine();
            if (data.TryGetValue(sec.Key, out var content) && !string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine(content);
                sections[sec.Title] = content;
            }
            else
            {
                var placeholder = sec.Required
                    ? $"**[REQUIRED - TO BE COMPLETED]**\n\n_{sec.Description}_"
                    : $"_[Optional] {sec.Description}_";
                sb.AppendLine(placeholder);
                sections[sec.Title] = placeholder;
            }
            sb.AppendLine();
        }
        return (sb.ToString(), sections);
    }

    private Dictionary<string, string> MapMarkdownToSections(string markdown, PrdTemplate template)
    {
        var sections = new Dictionary<string, string>();
        var lines = markdown.Split('\n');
        string? currentSection = null;
        var currentContent = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                if (currentSection is not null)
                {
                    var key = FindSectionKey(currentSection, template);
                    if (key is not null) sections[currentSection] = currentContent.ToString().Trim();
                }
                currentSection = line[3..].Trim();
                currentContent.Clear();
            }
            else if (currentSection is not null) currentContent.AppendLine(line);
        }
        if (currentSection is not null)
        {
            var key = FindSectionKey(currentSection, template);
            if (key is not null) sections[currentSection] = currentContent.ToString().Trim();
        }
        return sections;
    }

    public string ConvertToHtml(string markdown)
    {
        var body = Markdown.ToHtml(markdown, _mdPipeline);
        return "<!DOCTYPE html><html><head><meta charset=\"UTF-8\"><style>body{font-family:sans-serif;max-width:900px;margin:0 auto;padding:2rem;line-height:1.6}h1{border-bottom:3px solid #2563eb;padding-bottom:.5rem}h2{color:#1e40af;margin-top:2rem;border-bottom:1px solid #e5e7eb}table{border-collapse:collapse;width:100%}th,td{border:1px solid #d1d5db;padding:.75rem}th{background:#f3f4f6}</style></head><body>" + body + "</body></html>";
    }

    public double CalculateCompleteness(Dictionary<string, string> data, PrdTemplate template)
    {
        if (template.Sections.Count == 0) return 0;
        var totalWeight = template.Sections.Sum(s => s.Required ? 2.0 : 1.0);
        var filledWeight = template.Sections
            .Where(s => data.ContainsKey(s.Key) && !string.IsNullOrWhiteSpace(data[s.Key]))
            .Sum(s => s.Required ? 2.0 : 1.0);
        return Math.Round((filledWeight / totalWeight) * 100.0, 1);
    }

    public List<string> GetGaps(Dictionary<string, string> data, PrdTemplate template)
    {
        return template.Sections
            .Where(s => s.Required && (!data.ContainsKey(s.Key) || string.IsNullOrWhiteSpace(data[s.Key])))
            .OrderBy(s => s.Order).Select(s => $"{s.Title}: {s.Description}").ToList();
    }

    private static string? FindSectionKey(string title, PrdTemplate template)
    {
        return template.Sections.FirstOrDefault(s => s.Title.Equals(title, StringComparison.OrdinalIgnoreCase))?.Key;
    }

    private static string TruncateTitle(string text)
    {
        var firstLine = text.Split('\n')[0].Trim();
        return firstLine.Length > 80 ? firstLine[..77] + "..." : firstLine;
    }

    public static PrdTemplate GetBuiltInTemplate() => new()
    {
        Name = "Default PRD Template",
        Description = "Standard product requirements document template",
        Version = "1.0",
        Sections =
        [
            new() { Key = "title", Title = "Product Title", Order = 1, Description = "Name of the product or feature", MappedPhase = "Discovery", Required = true, PromptHints = ["What is the name?"] },
            new() { Key = "problem_statement", Title = "Problem Statement", Order = 2, Description = "The problem this product solves", MappedPhase = "Discovery", Required = true, PromptHints = ["What problem are you solving?"] },
            new() { Key = "target_users", Title = "Target Users", Order = 3, Description = "Primary and secondary users", MappedPhase = "Discovery", Required = true, PromptHints = ["Who will use this?"] },
            new() { Key = "goals_objectives", Title = "Goals & Objectives", Order = 4, Description = "Business goals and objectives", MappedPhase = "Discovery", Required = true, PromptHints = ["What does success look like?"] },
            new() { Key = "user_stories", Title = "User Stories", Order = 5, Description = "Key user stories", MappedPhase = "Requirements", Required = true, PromptHints = ["Describe user workflows"] },
            new() { Key = "functional_requirements", Title = "Functional Requirements", Order = 6, Description = "Features and capabilities", MappedPhase = "Requirements", Required = true, PromptHints = ["Must-have features?"] },
            new() { Key = "non_functional_requirements", Title = "Non-Functional Requirements", Order = 7, Description = "Performance and security", MappedPhase = "Technical", Required = true, PromptHints = ["Performance needs?"] },
            new() { Key = "technical_constraints", Title = "Technical Constraints", Order = 8, Description = "Technical limitations", MappedPhase = "Technical", Required = false, PromptHints = ["Integration constraints?"] },
            new() { Key = "scope_boundaries", Title = "Scope & Boundaries", Order = 9, Description = "In and out of scope", MappedPhase = "Requirements", Required = true, PromptHints = ["What is out of scope?"] },
            new() { Key = "acceptance_criteria", Title = "Acceptance Criteria", Order = 10, Description = "Completion conditions", MappedPhase = "AcceptanceCriteria", Required = true, PromptHints = ["How to verify?"] },
            new() { Key = "success_metrics", Title = "Success Metrics", Order = 11, Description = "KPIs to measure success", MappedPhase = "AcceptanceCriteria", Required = true, PromptHints = ["What KPIs?"] },
            new() { Key = "timeline_milestones", Title = "Timeline & Milestones", Order = 12, Description = "Key dates and phases", MappedPhase = "Requirements", Required = false, PromptHints = ["Any deadlines?"] },
            new() { Key = "risks_dependencies", Title = "Risks & Dependencies", Order = 13, Description = "Risks and mitigation", MappedPhase = "Technical", Required = false, PromptHints = ["What could go wrong?"] },
            new() { Key = "open_questions", Title = "Open Questions", Order = 14, Description = "Unresolved questions", MappedPhase = "Review", Required = false, PromptHints = ["Anything unclear?"] }
        ]
    };
}
