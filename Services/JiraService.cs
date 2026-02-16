using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrdAiAssistant.Api.Models.Configuration;
using PrdAiAssistant.Api.Models.DTOs;

namespace PrdAiAssistant.Api.Services;

public class JiraService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly JiraSettings _settings;
    private readonly ILogger<JiraService> _log;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public JiraService(IHttpClientFactory httpFactory, IOptions<JiraSettings> settings, ILogger<JiraService> log)
    {
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _log = log;
    }

    public async Task<JiraSubmitResponse> CreateIssueAsync(JiraSubmitRequest request, PrdDocument prd, CancellationToken ct = default)
    {
        var projectKey = string.IsNullOrEmpty(request.ProjectKey) ? _settings.DefaultProjectKey : request.ProjectKey;
        var issueType = request.IssueType ?? _settings.DefaultIssueType;
        var summary = request.Summary ?? prd.Title;
        if (summary.Length > 255) summary = summary[..252] + "...";

        var description = BuildAdfDescription(prd);
        var fields = new Dictionary<string, object>
        {
            ["project"] = new { key = projectKey },
            ["summary"] = summary,
            ["issuetype"] = new { name = issueType },
            ["description"] = description
        };

        if (!string.IsNullOrEmpty(request.EpicKey)) fields["parent"] = new { key = request.EpicKey };
        if (request.Labels is { Count: > 0 }) fields["labels"] = request.Labels;
        if (!string.IsNullOrEmpty(request.AssigneeAccountId)) fields["assignee"] = new { accountId = request.AssigneeAccountId };

        if (fields.TryGetValue("labels", out var el) && el is List<string> lbls)
        { if (!lbls.Contains("prd-generated")) lbls.Add("prd-generated"); }
        else { fields["labels"] = new List<string> { "prd-generated" }; }

        try
        {
            var client = _httpFactory.CreateClient("Jira");
            var json = JsonSerializer.Serialize(new { fields }, JsonOpts);
            var httpReq = new HttpRequestMessage(HttpMethod.Post, "rest/api/3/issue")
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            var httpRes = await client.SendAsync(httpReq, ct);
            var resBody = await httpRes.Content.ReadAsStringAsync(ct);

            if (!httpRes.IsSuccessStatusCode)
            {
                _log.LogError("Jira API error {Status}: {Body}", (int)httpRes.StatusCode, resBody);
                return new JiraSubmitResponse("", "", summary, false, $"Jira returned {(int)httpRes.StatusCode}: {ParseJiraError(resBody)}");
            }

            var created = JsonSerializer.Deserialize<JiraCreateResponse>(resBody, JsonOpts);
            var issueKey = created?.Key ?? "UNKNOWN";
            var issueUrl = $"{_settings.BaseUrl.TrimEnd('/')}/browse/{issueKey}";
            _log.LogInformation("Jira issue created: {Key}", issueKey);
            await AttachPrdFileAsync(issueKey, prd, ct);
            return new JiraSubmitResponse(issueKey, issueUrl, summary, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Failed to create Jira issue");
            return new JiraSubmitResponse("", "", summary, false, $"Exception: {ex.Message}");
        }
    }

    private async Task AttachPrdFileAsync(string issueKey, PrdDocument prd, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("Jira");
            var fileName = string.Concat($"PRD-{prd.Title.Replace(' ', '-')}-{prd.GeneratedAt:yyyyMMdd}.md".Split(Path.GetInvalidFileNameChars()));
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(prd.Markdown));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
            content.Add(fileContent, "file", fileName);
            var httpReq = new HttpRequestMessage(HttpMethod.Post, $"rest/api/3/issue/{issueKey}/attachments") { Content = content };
            httpReq.Headers.Add("X-Atlassian-Token", "no-check");
            await client.SendAsync(httpReq, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to attach PRD to {Key}", issueKey); }
    }

    public async Task<List<JiraProjectInfo>> GetProjectsAsync(CancellationToken ct = default)
    {
        try
        {
            var client = _httpFactory.CreateClient("Jira");
            var httpRes = await client.GetAsync("rest/api/3/project/search?maxResults=50", ct);
            var resBody = await httpRes.Content.ReadAsStringAsync(ct);
            if (!httpRes.IsSuccessStatusCode) return [];
            var result = JsonSerializer.Deserialize<JiraProjectSearchResponse>(resBody, JsonOpts);
            if (result?.Values is null) return [];
            return result.Values.Select(p => new JiraProjectInfo(
                p.Key ?? "", p.Name ?? "",
                p.IssueTypes?.Select(t => t.Name ?? "").Where(n => !string.IsNullOrEmpty(n)).ToList() ?? []
            )).ToList();
        }
        catch (Exception ex) { _log.LogError(ex, "Failed to fetch Jira projects"); return []; }
    }

    public async Task<(bool valid, string? error)> ValidateProjectAsync(string projectKey, string issueType, CancellationToken ct = default)
    {
        var projects = await GetProjectsAsync(ct);
        var project = projects.FirstOrDefault(p => p.Key.Equals(projectKey, StringComparison.OrdinalIgnoreCase));
        if (project is null) return (false, $"Project '{projectKey}' not found.");
        if (!project.IssueTypes.Any(t => t.Equals(issueType, StringComparison.OrdinalIgnoreCase)))
            return (false, $"Issue type '{issueType}' not found in {projectKey}.");
        return (true, null);
    }

    private static object BuildAdfDescription(PrdDocument prd)
    {
        var nodes = new List<object>();
        nodes.Add(AdfHeading(1, prd.Title));
        nodes.Add(AdfParagraph($"Generated: {prd.GeneratedAt:yyyy-MM-dd HH:mm} UTC | Completeness: {prd.CompletenessScore:F0}%"));
        nodes.Add(AdfRule());
        foreach (var kv in prd.Sections) { nodes.Add(AdfHeading(2, kv.Key)); nodes.Add(AdfParagraph(kv.Value)); }
        if (prd.Gaps.Count > 0) { nodes.Add(AdfRule()); nodes.Add(AdfHeading(2, "Gaps Requiring Follow-Up")); foreach (var g in prd.Gaps) nodes.Add(AdfParagraph($"- {g}")); }
        return new { version = 1, type = "doc", content = nodes };
    }

    private static object AdfHeading(int level, string text) => new { type = "heading", attrs = new { level }, content = new[] { new { type = "text", text } } };
    private static object AdfParagraph(string text) => new { type = "paragraph", content = new[] { new { type = "text", text } } };
    private static object AdfRule() => new { type = "rule" };

    private static string ParseJiraError(string responseBody)
    {
        try
        {
            var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("errorMessages", out var msgs))
            { var e = string.Join("; ", msgs.EnumerateArray().Select(m => m.GetString()).Where(s => !string.IsNullOrEmpty(s))); if (!string.IsNullOrEmpty(e)) return e; }
            if (doc.RootElement.TryGetProperty("errors", out var errs))
                return string.Join("; ", errs.EnumerateObject().Select(p => $"{p.Name}: {p.Value.GetString()}"));
            return responseBody.Length > 200 ? responseBody[..200] : responseBody;
        }
        catch { return responseBody.Length > 200 ? responseBody[..200] : responseBody; }
    }
}

internal class JiraCreateResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("self")] public string? Self { get; set; }
}

internal class JiraProjectSearchResponse
{
    [JsonPropertyName("values")] public List<JiraProjectValue>? Values { get; set; }
}

internal class JiraProjectValue
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("issueTypes")] public List<JiraIssueTypeValue>? IssueTypes { get; set; }
}

internal class JiraIssueTypeValue
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("subtask")] public bool Subtask { get; set; }
}
