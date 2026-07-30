namespace ErpPlatform.Shared.Kernel;

/// <summary>
/// Lets a business module push a movement into the platform's formal double-entry
/// books without depending on the accounting module.
/// </summary>
/// <remarks>
/// Same reasoning as <c>IPlatformUserDirectory</c> for users: a module that needs
/// something owned by another domain asks through an interface rather than
/// referencing that domain's projects. Finance supplies the implementation and the
/// host registers it, so <c>modules/*</c> stay independent of <c>src/FinanceERP.*</c>
/// and a module still builds and runs with accounting absent.
/// <para>
/// Deliberately dependency-free: account ids are plain ints and lines are plain
/// tuples, so this type can live in the Kernel without dragging EF Core or the
/// Finance domain along behind it.
/// </para>
/// </remarks>
public interface IBookkeepingPoster
{
    /// <summary>
    /// False when no accounting module is wired in. Callers check this rather than
    /// catching, so an unmapped install degrades quietly instead of failing writes.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Posts a balanced journal. Debits must equal credits — the implementation
    /// rejects anything else, because a module must not be able to unbalance the books.
    /// </summary>
    /// <param name="source">Module key that raised it, e.g. <c>ledger</c>.</param>
    /// <param name="sourceId">The originating record's id, for tracing back.</param>
    /// <returns>The posted voucher's id, or null when posting isn't available.</returns>
    Task<int?> PostAsync(
        DateOnly date,
        string narration,
        string source,
        int? sourceId,
        IEnumerable<BookkeepingLine> lines,
        CancellationToken ct = default);

    /// <summary>
    /// The accounts a module may post against, for a picker. Empty when unavailable.
    /// </summary>
    Task<IReadOnlyList<BookkeepingAccount>> ListAccountsAsync(CancellationToken ct = default);
}

/// <param name="AccountId">Account in the platform chart of accounts.</param>
public record BookkeepingLine(int AccountId, decimal Debit, decimal Credit, string? Description = null);

public record BookkeepingAccount(int Id, string Code, string Name, string? Group = null);

/// <summary>
/// Stand-in used when no accounting module is registered. Reports itself
/// unavailable and posts nothing, so a module can call unconditionally.
/// </summary>
public sealed class NullBookkeepingPoster : IBookkeepingPoster
{
    public bool IsAvailable => false;

    public Task<int?> PostAsync(DateOnly date, string narration, string source, int? sourceId,
        IEnumerable<BookkeepingLine> lines, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    public Task<IReadOnlyList<BookkeepingAccount>> ListAccountsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BookkeepingAccount>>([]);
}
