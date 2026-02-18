using System.ComponentModel.DataAnnotations;

namespace PrdAiAssistant.Api.Models;

public class SavedPrd
{
    [Key]
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string Status { get; set; } = "draft";
    public string Content { get; set; } = "";
    public string? JiraEpicKey { get; set; }
    public string? DesignNotes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}