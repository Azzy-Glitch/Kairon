using AIDIP.Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace AIDIP.Backend.Infrastructure;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Incident> Incidents { get; set; }
    public DbSet<Metric> Metrics { get; set; }
    public DbSet<Analysis> Analyses { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Incident>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Timestamp });
            entity.HasIndex(e => e.Endpoint);
            entity.HasIndex(e => e.StatusCode);
            entity.HasIndex(e => e.Environment);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);
            entity.Property(e => e.StackTrace).HasMaxLength(8000);
        });

        modelBuilder.Entity<Metric>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Timestamp });
            entity.HasIndex(e => e.Environment);
        });

        modelBuilder.Entity<Analysis>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Type });
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.OutputJson).HasMaxLength(16000);
        });
    }
}