namespace ErpPlatform.Shared.Kernel;

/// <summary>
/// Base for every persisted entity in every module. Audit stamps are filled in
/// automatically by each module's DbContext on save.
/// </summary>
public abstract class BaseEntity
{
    public int Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }
}

public interface ISoftDelete
{
    bool IsDeleted { get; set; }
    DateTime? DeletedAtUtc { get; set; }
    string? DeletedBy { get; set; }
}

/// <summary>Entity that is never hard-deleted — deletes become flag updates.</summary>
public abstract class AuditableEntity : BaseEntity, ISoftDelete
{
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
}

/// <summary>
/// Opt-in optimistic locking for records where a lost update costs money.
/// </summary>
/// <remarks>
/// Without this, two people editing the same voucher, delivery or stock item silently
/// last-write-wins: the second save overwrites the first with no warning to either.
/// With it, the second save throws <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
/// and the UI can say "somebody changed this while you were editing" instead of quietly
/// destroying their work.
/// <para>
/// Deliberately an interface rather than a field on <see cref="BaseEntity"/>: putting it
/// on everything would mean a migration on every table in all nine databases, and most
/// tables are never edited concurrently. Add it where contention is real.
/// </para>
/// <para>
/// MariaDB has no native <c>rowversion</c>, so the value is a GUID re-stamped on every
/// save by <c>ModuleDbContext</c>. EF still compares the <em>original</em> value in the
/// UPDATE's WHERE clause, which is what makes the check work.
/// </para>
/// </remarks>
public interface IConcurrencyChecked
{
    Guid ConcurrencyStamp { get; set; }
}
