using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CarbonAware.Api.Data;

public sealed class LoggingDbContext : DbContext
{
    public LoggingDbContext(DbContextOptions<LoggingDbContext> options) : base(options) { }

    public DbSet<WattTimeCallLog> WattTimeCalls => Set<WattTimeCallLog>();
    public DbSet<AdviceExecutionLog> AdviceExecutions => Set<AdviceExecutionLog>();
    public DbSet<AdviceCandidateLog> AdviceCandidates => Set<AdviceCandidateLog>();
    public DbSet<CycleResultLog> CycleResults => Set<CycleResultLog>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<WattTimeCallLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RequestUrl).HasMaxLength(800);
            e.Property(x => x.Region).HasMaxLength(64);
            e.Property(x => x.SignalType).HasMaxLength(32);
            e.Property(x => x.Method).HasMaxLength(8);
            e.Property(x => x.SourceFile).HasMaxLength(128);
        });

        mb.Entity<AdviceExecutionLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Mode).HasMaxLength(32);
            e.Property(x => x.SelectedCloud).HasMaxLength(32);
            e.Property(x => x.SelectedRegion).HasMaxLength(64);
            e.Property(x => x.Rationale).HasMaxLength(1024);
        });

        mb.Entity<AdviceCandidateLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Cloud).HasMaxLength(32);
            e.Property(x => x.Region).HasMaxLength(64);
            e.HasOne(x => x.Execution)
             .WithMany(x => x.Candidates)
             .HasForeignKey(x => x.ExecutionId);
        });

        mb.Entity<CycleResultLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CycleId).HasMaxLength(32).IsRequired();
            e.Property(x => x.ObjectiveType).HasMaxLength(16);
            e.Property(x => x.WeightConfig).HasMaxLength(32);
            e.Property(x => x.CloudProvider).HasMaxLength(32);
            e.Property(x => x.Region).HasMaxLength(64);
            e.Property(x => x.ErrorNotes).HasMaxLength(1024);
            // one row per cycle+cloud+region -- lets the same cycle id appear once per
            // region if you ever run more than one region per cycle
            e.HasIndex(x => new { x.CycleId, x.CloudProvider, x.Region }).IsUnique();
        });
    }
}

public sealed class WattTimeCallLog
{
    public long Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    // request
    public string Method { get; set; } = "GET";
    public string RequestUrl { get; set; } = default!;
    public string? Region { get; set; }
    public string? SignalType { get; set; }
    public int? HorizonHours { get; set; }

    // result
    public int StatusCode { get; set; }
    public bool Success { get; set; }
    public int DurationMs { get; set; }

    // payloads (raw strings for audit)
    public string? ResponseBody { get; set; }
    public string? Error { get; set; }

    // optional: where in code we logged from (helps debugging)
    public string? SourceFile { get; set; }
    public int? SourceLine { get; set; }
    public Guid? RequestId { get; set; }
}

public sealed class AdviceExecutionLog
{
    public long Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    // input context
    public string Mode { get; set; } = default!;                 // run_now | schedule_at
    public DateTimeOffset TargetWhen { get; set; }               // now or chosen deadline
    public string? PreferredCloudsCsv { get; set; }              // e.g., "gcp,azure,aws"
    public string? PreferredRegionsCsv { get; set; }             // raw list user picked

    // decision
    public string SelectedCloud { get; set; } = default!;
    public string SelectedRegion { get; set; } = default!;
    public DateTimeOffset? SelectedWhen { get; set; }
    public double? SelectedMoerGPerKwh { get; set; }
    public string Rationale { get; set; } = default!;

    // metrics at decision time
    public string? HighestEmissionCloud { get; set; }
    public string? HighestEmissionRegion { get; set; }
    public double? HighestEmissionGPerKwh { get; set; }
    public double? EstimatedSavingGPerKwh { get; set; }
    public double? EstimatedSavingPercent { get; set; }
    public double? AverageEmissionGPerKwh { get; set; }
    public double? AverageEstimatedSavingPercent { get; set; }

    // best window (if schedule_at)
    public string? BestWindowCloud { get; set; }
    public string? BestWindowRegion { get; set; }
    public double? BestWindowMoerGPerKwh { get; set; }
    public DateTimeOffset? BestWindowWhen { get; set; }
    public ICollection<AdviceCandidateLog> Candidates { get; set; } = new List<AdviceCandidateLog>();
    public Guid? RequestId { get; init; }
}

public sealed class AdviceCandidateLog
{
    public long Id { get; set; }
    public long ExecutionId { get; set; }
    public AdviceExecutionLog Execution { get; set; } = default!;

    public string Cloud { get; set; } = default!;
    public string Region { get; set; } = default!;

    public double? MoerAtTarget { get; set; }
    public double? BestMoerUntilTarget { get; set; }
    public DateTimeOffset? BestMoerAt { get; set; }
    public Guid? RequestId { get; init; }
}
