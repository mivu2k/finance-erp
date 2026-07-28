using System.Text.Json;
using ErpPlatform.Shared.Kernel;
using Microsoft.EntityFrameworkCore;

namespace ErpPlatform.Shared.Persistence;

/// <summary>
/// Base DbContext for a business module. Gives every module the same three
/// guarantees the accounts module already had: audit stamps on save, soft delete
/// instead of hard delete, and a written audit trail row per change.
/// </summary>
/// <remarks>
/// Each module points this at its own database. There are no cross-database
/// foreign keys anywhere in the platform — a module that needs a user stores the
/// Identity user id as a string and snapshots the display name.
/// </remarks>
public abstract class ModuleDbContext(DbContextOptions options, ICurrentUserService currentUser)
    : DbContext(options)
{
    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();

    /// <summary>Entity types that must never generate audit rows (avoids recursion).</summary>
    protected virtual bool IsAuditExempt(object entity) => entity is AuditLogEntry;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLogs");
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.EntityName, x.EntityId });
            e.Property(x => x.Action).HasMaxLength(32);
            e.Property(x => x.EntityName).HasMaxLength(128);
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.Browser).HasMaxLength(512);
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var user = currentUser.UserName;
        var audits = new List<AuditLogEntry>();

        var entries = ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in entries)
        {
            if (entry.Entity is BaseEntity be)
            {
                if (entry.State == EntityState.Added) { be.CreatedAtUtc = now; be.CreatedBy = user; }
                else if (entry.State == EntityState.Modified) { be.ModifiedAtUtc = now; be.ModifiedBy = user; }
            }

            // Soft delete: convert hard deletes into flag updates.
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDelete sd)
            {
                entry.State = EntityState.Modified;
                sd.IsDeleted = true;
                sd.DeletedAtUtc = now;
                sd.DeletedBy = user;
            }

            if (IsAuditExempt(entry.Entity)) continue;

            var action = entry.State switch
            {
                EntityState.Added => "Created",
                EntityState.Deleted => "Deleted",
                _ => entry.Entity is ISoftDelete { IsDeleted: true } ? "SoftDeleted" : "Modified"
            };

            var oldVals = entry.State == EntityState.Added ? null :
                JsonSerializer.Serialize(entry.Properties
                    .Where(p => p.IsModified || entry.State == EntityState.Deleted)
                    .ToDictionary(p => p.Metadata.Name, p => p.OriginalValue?.ToString()));
            var newVals = entry.State == EntityState.Deleted ? null :
                JsonSerializer.Serialize(entry.Properties
                    .Where(p => p.IsModified || entry.State == EntityState.Added)
                    .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue?.ToString()));

            audits.Add(new AuditLogEntry
            {
                UserId = currentUser.UserId,
                UserName = user,
                IpAddress = currentUser.IpAddress,
                Browser = currentUser.Browser,
                Action = action,
                EntityName = entry.Metadata.ClrType.Name,
                EntityId = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey())?.CurrentValue?.ToString(),
                OldValues = oldVals,
                NewValues = newVals,
                TimestampUtc = now
            });
        }

        var result = await base.SaveChangesAsync(ct);

        if (audits.Count > 0)
        {
            AuditLogs.AddRange(audits);
            await base.SaveChangesAsync(ct);
        }

        return result;
    }
}
