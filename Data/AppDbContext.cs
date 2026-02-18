using Microsoft.EntityFrameworkCore;
using PrdAiAssistant.Api.Models;

namespace PrdAiAssistant.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<SavedPrd> SavedPrds => Set<SavedPrd>();
}