using Microsoft.EntityFrameworkCore;

namespace Studio.Data;

public sealed class StudioDb(DbContextOptions<StudioDb> options) : DbContext(options)
{
    public DbSet<ProjectRow> Projects => Set<ProjectRow>();
    public DbSet<JobRow> Jobs => Set<JobRow>();
    public DbSet<ChargeRow> Charges => Set<ChargeRow>();
    public DbSet<AssetRow> Assets => Set<AssetRow>();
    public DbSet<RevisionRow> Revisions => Set<RevisionRow>();
    public DbSet<SettingRow> Settings => Set<SettingRow>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SettingRow>().HasKey(x => x.Key);
        b.Entity<JobRow>().HasIndex(x => new { x.ProjectId, x.Status });
        b.Entity<AssetRow>().HasIndex(x => x.ProjectId);
        b.Entity<RevisionRow>().HasIndex(x => new { x.ProjectId, x.Version });
        b.Entity<ChargeRow>().HasIndex(x => x.JobId).IsUnique();
    }
}
public class ProjectRow { public Guid Id { get; set; } public string Json { get; set; } = ""; }
public class JobRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Stage { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public string RequestJson { get; set; } = "";
    public string ConfigJson { get; set; } = "";
    public string? Checkpoint { get; set; }
    public string? Error { get; set; }
    public bool CancelRequested { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long? FinishedAt { get; set; }
}
public class ChargeRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid JobId { get; set; }
    public string ProviderId { get; set; } = "";
    public string Stage { get; set; } = "";
    public decimal AmountUsd { get; set; }
    public string Status { get; set; } = "Reserved";
    public string PriceVersion { get; set; } = "";
    public string Note { get; set; } = "";
}
public class AssetRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Guid ProjectId { get; set; }
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Bytes { get; set; }
    public string? Attribution { get; set; }
}
public class RevisionRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public int Version { get; set; }
    public string Reason { get; set; } = "";
    public string Json { get; set; } = "";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
public class SettingRow { public string Key { get; set; } = ""; public string Json { get; set; } = ""; public string? ProtectedSecret { get; set; } }
