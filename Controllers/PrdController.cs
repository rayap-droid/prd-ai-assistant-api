using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrdAiAssistant.Api.Data;
using PrdAiAssistant.Api.Models;

namespace PrdAiAssistant.Api.Controllers;

[ApiController]
[Route("api/library")]

public class PrdController : ControllerBase
{
    private readonly AppDbContext _db;
    public PrdController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var prds = await _db.SavedPrds
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new {
                p.Id, p.Title, p.ProjectName,
                p.Status, p.CreatedAt, p.UpdatedAt,
                p.JiraEpicKey
            })
            .ToListAsync();
        return Ok(prds);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var prd = await _db.SavedPrds.FindAsync(id);
        if (prd == null) return NotFound();
        return Ok(prd);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SavedPrd prd)
    {
        prd.CreatedAt = DateTime.UtcNow;
        prd.UpdatedAt = DateTime.UtcNow;
        _db.SavedPrds.Add(prd);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = prd.Id }, prd);
    }

    [HttpPut("{id}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] StatusUpdate update)
    {
        var prd = await _db.SavedPrds.FindAsync(id);
        if (prd == null) return NotFound();
        prd.Status = update.Status;
        prd.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(prd);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var prd = await _db.SavedPrds.FindAsync(id);
        if (prd == null) return NotFound();
        _db.SavedPrds.Remove(prd);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record StatusUpdate(string Status);