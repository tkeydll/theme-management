using Microsoft.EntityFrameworkCore;
using ThemeManagement.Domain.Entities;

namespace ThemeManagement.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Grade> Grades => Set<Grade>();
    public DbSet<Engineer> Engineers => Set<Engineer>();
    public DbSet<MonthlyWorkDays> MonthlyWorkDays => Set<MonthlyWorkDays>();
    public DbSet<EngineerMonthlyAdjustment> EngineerMonthlyAdjustments => Set<EngineerMonthlyAdjustment>();
    public DbSet<Theme> Themes => Set<Theme>();
    public DbSet<EngineerThemeAllocation> EngineerThemeAllocations => Set<EngineerThemeAllocation>();
    public DbSet<ThemeCarryOver> ThemeCarryOvers => Set<ThemeCarryOver>();
    public DbSet<GradePriceHistory> GradePriceHistories => Set<GradePriceHistory>();
    public DbSet<NotificationSetting> NotificationSettings => Set<NotificationSetting>();
    public DbSet<ThemeMonthlyTarget> ThemeMonthlyTargets => Set<ThemeMonthlyTarget>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<EngineerSkill> EngineerSkills => Set<EngineerSkill>();
    public DbSet<ThemeRequiredSkill> ThemeRequiredSkills => Set<ThemeRequiredSkill>();
    public DbSet<MonthlyClosure> MonthlyClosures => Set<MonthlyClosure>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MonthlyWorkDays>()
            .HasIndex(e => new { e.Year, e.Month }).IsUnique();

        modelBuilder.Entity<EngineerMonthlyAdjustment>()
            .HasIndex(e => new { e.EngineerId, e.Year, e.Month }).IsUnique();

        modelBuilder.Entity<EngineerThemeAllocation>()
            .HasIndex(e => new { e.EngineerId, e.ThemeId, e.Year, e.Month }).IsUnique();

        modelBuilder.Entity<ThemeCarryOver>()
            .HasIndex(e => new { e.ThemeId, e.FiscalYear, e.IsFirstHalf }).IsUnique();

        modelBuilder.Entity<ThemeMonthlyTarget>()
            .HasIndex(e => new { e.ThemeId, e.Year, e.Month }).IsUnique();

        modelBuilder.Entity<EngineerSkill>()
            .HasIndex(e => new { e.EngineerId, e.SkillId }).IsUnique();

        modelBuilder.Entity<ThemeRequiredSkill>()
            .HasIndex(e => new { e.ThemeId, e.SkillId }).IsUnique();

        modelBuilder.Entity<MonthlyClosure>()
            .HasIndex(e => new { e.Year, e.Month }).IsUnique();

        modelBuilder.Entity<EngineerSkill>()
            .Property(e => e.Level)
            .HasColumnType("INTEGER");

        modelBuilder.Entity<ThemeRequiredSkill>()
            .Property(e => e.RequiredLevel)
            .HasColumnType("INTEGER");
    }

    public override int SaveChanges()
    {
        SetTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SetTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void SetTimestamps()
    {
        var now = DateTime.Now;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added)
            {
                var createdAt = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "CreatedAt");
                if (createdAt != null) createdAt.CurrentValue = now;
                var updatedAt = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "UpdatedAt");
                if (updatedAt != null) updatedAt.CurrentValue = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                var updatedAt = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "UpdatedAt");
                if (updatedAt != null) updatedAt.CurrentValue = now;
            }
        }
    }
}
