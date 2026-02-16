using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrdAiAssistant.Api.Models;
using PrdAiAssistant.Api.Models.Configuration;
using PrdAiAssistant.Api.Models.Enums;

namespace PrdAiAssistant.Api.Services;

public class ClaudeService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly AnthropicSettings _settings;
    private readonly ILogger<ClaudeService> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ClaudeService(
        IHttpClientFactory httpFactory,
        IOptions<AnthropicSettings> settings,
        ILogger<ClaudeService> log)
    {
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _log = log;
    }

    public async Task<ClaudeResponse> ChatAsync(
        ConversationSession session,
        PrdTemplate template,
        CancellationToken ct = default)
    {
        var systemPrompt = BuildSystemPrompt(session, template);
        var messages = BuildMessages(session);

        var requestBody = new
        {
            model = _settings.Model,
            max_tokens = _settings.MaxTokens,
            temperature = _settings.Temperature,
            system = systemPrompt,
            messages
        };

        var client = _httpFactory.CreateClient("Anthropic");
        var json = JsonSerializer.Serialize(requestBody, JsonOpts);

        var httpReq = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var httpRes = await client.SendAsync(httpReq, ct);
        var resBody = await httpRes.Content.ReadAsStringAsync(ct);

        if (!httpRes.IsSuccessStatusCode)
        {
            _log.LogError("Claude API error {Status}: {Body}", (int)httpRes.StatusCode, resBody);
            throw new ClaudeApiException($"Anthropic API returned {(int)httpRes.StatusCode}", resBody);
        }

        var apiResponse = JsonSerializer.Deserialize<AnthropicApiResponse>(resBody, JsonOpts)
            ?? throw new ClaudeApiException("Failed to deserialize Claude response", resBody);

        var reply = ExtractTextContent(apiResponse);
        var extracted = ParseStructuredData(reply);

        return new ClaudeResponse(
            Reply: CleanReplyText(reply),
            ExtractedData: extracted,
            SuggestedPhase: null,
            InputTokens: apiResponse.Usage?.InputTokens ?? 0,
            OutputTokens: apiResponse.Usage?.OutputTokens ?? 0
        );
    }

    public async Task<string> GeneratePrdAsync(
        Dictionary<string, string> extractedData,
        PrdTemplate template,
        CancellationToken ct = default)
    {
        var sectionList = string.Join("\n", template.Sections
            .OrderBy(s => s.Order)
            .Select(s => $"- **{s.Title}** (key: {s.Key}): {s.Description}"));

        var dataBlock = string.Join("\n", extractedData
            .Select(kv => $"[{kv.Key}]\n{kv.Value}"));

        var systemPrompt = "You are a senior product manager writing a PRD.\n\nGenerate a complete, professional PRD in Markdown format using the template sections below.\nFill every section using the extracted interview data provided.\nIf data for a section is missing, write \"[TO BE COMPLETED]\".\n\n## Template Sections:\n" + sectionList;

        var messages = new List<object>
        {
            new { role = "user", content = "Here is the extracted interview data:\n\n" + dataBlock + "\n\nPlease generate the complete PRD document in Markdown." }
        };

        var requestBody = new
        {
            model = _settings.Model,
            max_tokens = _settings.MaxTokens,
            temperature = 0.3,
            system = systemPrompt,
            messages
        };

        var client = _httpFactory.CreateClient("Anthropic");
        var json = JsonSerializer.Serialize(requestBody, JsonOpts);
        var httpReq = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var httpRes = await client.SendAsync(httpReq, ct);
        var resBody = await httpRes.Content.ReadAsStringAsync(ct);

        if (!httpRes.IsSuccessStatusCode)
            throw new ClaudeApiException("PRD generation failed", resBody);

        var apiResponse = JsonSerializer.Deserialize<AnthropicApiResponse>(resBody, JsonOpts)
            ?? throw new ClaudeApiException("Failed to deserialize PRD response", resBody);

        return ExtractTextContent(apiResponse);
    }

    private string BuildSystemPrompt(ConversationSession session, PrdTemplate template)
    {
        var phase = session.CurrentPhase;
        var phaseSection = template.Sections
            .Where(s => s.MappedPhase.Equals(phase.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sectionHints = phaseSection.Count > 0
            ? string.Join("\n", phaseSection.Select(s =>
                $"- {s.Title}: {s.Description}\n  Hints: {string.Join(", ", s.PromptHints)}"))
            : "No specific sections mapped to this phase.";

        var existingKeys = session.ExtractedData.Keys.ToHashSet();
        var missingRequired = template.Sections
            .Where(s => s.Required && !existingKeys.Contains(s.Key))
            .Select(s => s.Title)
            .ToList();

        var contextBlock = string.IsNullOrEmpty(session.ProjectContext)
            ? ""
            : "\n## Project Context:\n" + session.ProjectContext + "\n";

        var sectionKeys = string.Join(", ", template.Sections.Select(s => s.Key));
        var missingText = missingRequired.Count > 0 ? string.Join(", ", missingRequired) : "None";

        return "You are an expert product manager conducting a stakeholder interview to gather requirements for a PRD.\n\n## Your Behavior:\n- Ask focused, open-ended questions one or two at a time\n- Listen carefully and ask smart follow-up questions\n- Acknowledge the stakeholder input before moving on\n- Naturally transition between topics\n- Be conversational but professional\n- If answers are vague, probe deeper with specific examples\n- Do NOT generate the PRD yourself - just gather information\n\n## Current Interview Phase: " + phase + "\n## Sections to Cover in This Phase:\n" + sectionHints + "\n\n## Still Missing (required sections not yet covered):\n" + missingText + "\n" + contextBlock + "\n## Extracted Data So Far:\n" + FormatExtractedData(session.ExtractedData) + "\n\n## IMPORTANT - Structured Data Extraction:\nAfter your conversational reply, you MUST append a data block in this exact format:\n\n---EXTRACTED---\n[section_key]\nextracted content here\n[/section_key]\n---/EXTRACTED---\n\nOnly include sections where the user provided NEW information in their latest message.\nUse these section keys: " + sectionKeys + "\n\nIf the user message does not contain extractable PRD data, omit the ---EXTRACTED--- block entirely.\n\n## Phase Transition:\nIf you believe the current phase is sufficiently covered, append:\n---PHASE:NextPhaseName---\nValid phases: Discovery, Requirements, Technical, AcceptanceCriteria, Review";
    }

    private List<object> BuildMessages(ConversationSession session)
    {
        var maxMessages = _settings.MaxConversationTurns * 2;
        return session.Messages
            .Where(m => m.Role is "user" or "assistant")
            .TakeLast(maxMessages)
            .Select(m => (object)new { role = m.Role, content = m.Content })
            .ToList();
    }

    private static string ExtractTextContent(AnthropicApiResponse response)
    {
        return string.Join("", response.Content
            .Where(c => c.Type == "text")
            .Select(c => c.Text));
    }

    private static Dictionary<string, string> ParseStructuredData(string reply)
    {
        var data = new Dictionary<string, string>();
        const string startMarker = "---EXTRACTED---";
        const string endMarker = "---/EXTRACTED---";

        var startIdx = reply.IndexOf(startMarker, StringComparison.Ordinal);
        var endIdx = reply.IndexOf(endMarker, StringComparison.Ordinal);

        if (startIdx < 0 || endIdx < 0 || endIdx <= startIdx)
            return data;

        var block = reply[(startIdx + startMarker.Length)..endIdx].Trim();
        var pos = 0;
        while (pos < block.Length)
        {
            var keyStart = block.IndexOf('[', pos);
            if (keyStart < 0) break;
            var keyEnd = block.IndexOf(']', keyStart);
            if (keyEnd < 0) break;
            var key = block[(keyStart + 1)..keyEnd];
            if (key.StartsWith('/')) { pos = keyEnd + 1; continue; }
            var closeTag = $"[/{key}]";
            var closeIdx = block.IndexOf(closeTag, keyEnd, StringComparison.Ordinal);
            if (closeIdx < 0) { pos = keyEnd + 1; continue; }
            var value = block[(keyEnd + 1)..closeIdx].Trim();
            if (!string.IsNullOrEmpty(value)) data[key] = value;
            pos = closeIdx + closeTag.Length;
        }
        return data;
    }

    public static InterviewPhase? ParsePhaseTransition(string reply)
    {
        const string marker = "---PHASE:";
        var idx = reply.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var rest = reply[(idx + marker.Length)..];
        var endIdx = rest.IndexOf("---", StringComparison.Ordinal);
        if (endIdx < 0) return null;
        var phaseName = rest[..endIdx].Trim();
        return Enum.TryParse<InterviewPhase>(phaseName, ignoreCase: true, out var phase) ? phase : null;
    }

    private static string CleanReplyText(string reply)
    {
        var cleaned = reply;
        const string startMarker = "---EXTRACTED---";
        const string endMarker = "---/EXTRACTED---";
        var s = cleaned.IndexOf(startMarker, StringComparison.Ordinal);
        var e = cleaned.IndexOf(endMarker, StringComparison.Ordinal);
        if (s >= 0 && e > s)
            cleaned = cleaned[..s] + cleaned[(e + endMarker.Length)..];
        var phaseIdx = cleaned.IndexOf("---PHASE:", StringComparison.Ordinal);
        if (phaseIdx >= 0)
        {
            var phaseEnd = cleaned.IndexOf("---", phaseIdx + 9, StringComparison.Ordinal);
            if (phaseEnd >= 0)
                cleaned = cleaned[..phaseIdx] + cleaned[(phaseEnd + 3)..];
        }
        return cleaned.Trim();
    }

    private static string FormatExtractedData(Dictionary<string, string> data)
    {
        if (data.Count == 0) return "No data extracted yet.";
        return string.Join("\n\n", data.Select(kv => $"### {kv.Key}\n{kv.Value}"));
    }
}

public record ClaudeResponse(
    string Reply,
    Dictionary<string, string> ExtractedData,
    InterviewPhase? SuggestedPhase,
    int InputTokens,
    int OutputTokens
);

public class AnthropicApiResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("content")]
    public List<ContentBlock> Content { get; set; } = [];
    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; set; }
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }
}

public class ContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public class UsageInfo
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }
    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

public class ClaudeApiException : Exception
{
    public string ResponseBody { get; }
    public ClaudeApiException(string message, string responseBody) : base(message)
    {
        ResponseBody = responseBody;
    }
}
