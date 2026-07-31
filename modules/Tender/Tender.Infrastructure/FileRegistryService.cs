using ErpPlatform.Shared.Persistence;
using ErpPlatform.Shared.Kernel;
using Microsoft.EntityFrameworkCore;
using Tender.Domain;

namespace Tender.Infrastructure;

public record FileFilter(
    string? Search = null,
    FileStatus? Status = null,
    FileOwnerType? OwnerType = null,
    bool OverdueOnly = false);

public interface IFileRegistryService
{
    /// <summary>
    /// The file for a tender or project, creating it on first use. Idempotent — every
    /// owner record has exactly one file, and calling this twice returns the same one.
    /// </summary>
    Task<PhysicalFile> EnsureForAsync(FileOwnerType ownerType, int ownerId,
        string ownerReference, string ownerTitle, CancellationToken ct = default);

    Task<PhysicalFile?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>What a scanned sticker resolves to.</summary>
    Task<PhysicalFile?> GetByNumberAsync(string fileNumber, CancellationToken ct = default);

    Task<PhysicalFile?> FindForOwnerAsync(FileOwnerType ownerType, int ownerId,
        CancellationToken ct = default);

    Task<List<PhysicalFile>> ListAsync(FileFilter? filter = null, CancellationToken ct = default);

    /// <summary>Hands the file to someone. Refuses if it is already out — two holders is how files vanish.</summary>
    Task<PhysicalFile> IssueAsync(int fileId, string? holderUserId, string holderName,
        string? purpose, DateOnly? dueBack, string recordedById, string recordedByName,
        string? remarks = null, CancellationToken ct = default);

    Task<PhysicalFile> ReturnAsync(int fileId, string? location,
        string recordedById, string recordedByName, string? remarks = null,
        CancellationToken ct = default);

    /// <summary>Straight from one holder to the next, without a trip back to the registry.</summary>
    Task<PhysicalFile> TransferAsync(int fileId, string? holderUserId, string holderName,
        string? purpose, DateOnly? dueBack, string recordedById, string recordedByName,
        string? remarks = null, CancellationToken ct = default);

    Task<PhysicalFile> ArchiveAsync(int fileId, string? location,
        string recordedById, string recordedByName, string? remarks = null,
        CancellationToken ct = default);

    Task<PhysicalFile> ReopenAsync(int fileId, string recordedById, string recordedByName,
        string? remarks = null, CancellationToken ct = default);

    Task<PhysicalFile> MarkLostAsync(int fileId, string recordedById, string recordedByName,
        string? remarks = null, CancellationToken ct = default);

    Task<PhysicalFile> MarkFoundAsync(int fileId, string? location,
        string recordedById, string recordedByName, string? remarks = null,
        CancellationToken ct = default);

    /// <summary>Updates the shelf details — not a movement, so it writes no history row.</summary>
    Task<PhysicalFile> UpdateDetailsAsync(int fileId, string? location, string? volumeNumber,
        string? remarks, CancellationToken ct = default);

    /// <summary>Files out past the date they were promised back.</summary>
    Task<List<PhysicalFile>> ListOverdueAsync(CancellationToken ct = default);
}

public class FileRegistryService(TenderDbContext db, IBusinessClock clock) : IFileRegistryService
{
    private const string SequenceType = "TenderFile";
    private const string SequencePrefix = "FILE";

    public async Task<PhysicalFile> EnsureForAsync(FileOwnerType ownerType, int ownerId,
        string ownerReference, string ownerTitle, CancellationToken ct = default)
    {
        var existing = await db.Files
            .FirstOrDefaultAsync(f => f.OwnerType == ownerType && f.OwnerId == ownerId, ct);

        if (existing is not null)
        {
            // Keep the snapshots honest if the tender/project was renamed.
            existing.OwnerReference = ownerReference;
            existing.OwnerTitle = ownerTitle;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var today = clock.Today;

        // Allocating a number and writing the file must be one unit, or a crash
        // between them burns a number and leaves a file without one.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var number = await new DocumentNumberService(db).NextAsync(SequenceType, SequencePrefix, ct);

            var file = new PhysicalFile
            {
                FileNumber = number,
                OwnerType = ownerType,
                OwnerId = ownerId,
                OwnerReference = ownerReference,
                OwnerTitle = ownerTitle,
                Status = FileStatus.InRegistry,
                OpenedOn = today
            };

            db.Files.Add(file);
            await db.SaveChangesAsync(ct);

            db.FileMovements.Add(new FileMovement
            {
                PhysicalFileId = file.Id,
                Action = FileMovementAction.Opened,
                MovedOn = today,
                RecordedById = string.Empty,
                RecordedByName = "System",
                Remarks = "File opened."
            });
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return file;
        });
    }

    public Task<PhysicalFile?> GetAsync(int id, CancellationToken ct = default) =>
        db.Files
            .Include(f => f.Movements.OrderByDescending(m => m.MovedOn).ThenByDescending(m => m.Id))
            .FirstOrDefaultAsync(f => f.Id == id, ct);

    public Task<PhysicalFile?> GetByNumberAsync(string fileNumber, CancellationToken ct = default)
    {
        var n = (fileNumber ?? string.Empty).Trim();
        return db.Files
            .Include(f => f.Movements.OrderByDescending(m => m.MovedOn).ThenByDescending(m => m.Id))
            .FirstOrDefaultAsync(f => f.FileNumber == n, ct);
    }

    public Task<PhysicalFile?> FindForOwnerAsync(
        FileOwnerType ownerType, int ownerId, CancellationToken ct = default) =>
        db.Files.Include(f => f.Movements)
            .FirstOrDefaultAsync(f => f.OwnerType == ownerType && f.OwnerId == ownerId, ct);

    public async Task<List<PhysicalFile>> ListAsync(
        FileFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new FileFilter();
        var q = db.Files.Include(f => f.Movements).AsNoTracking().AsSplitQuery().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(f => f.FileNumber.Contains(s) || f.OwnerReference.Contains(s)
                          || f.OwnerTitle.Contains(s)
                          || (f.HolderName != null && f.HolderName.Contains(s))
                          || (f.Location != null && f.Location.Contains(s)));
        }

        if (filter.Status is { } st) q = q.Where(f => f.Status == st);
        if (filter.OwnerType is { } ot) q = q.Where(f => f.OwnerType == ot);

        var files = await q.OrderByDescending(f => f.Id).ToListAsync(ct);

        // Overdue depends on the newest issue movement, which is awkward to express in
        // SQL and cheap in memory at registry scale.
        return filter.OverdueOnly ? files.Where(IsOverdue).ToList() : files;
    }

    public async Task<List<PhysicalFile>> ListOverdueAsync(CancellationToken ct = default)
    {
        var files = await db.Files.Include(f => f.Movements).AsNoTracking().AsSplitQuery()
            .Where(f => f.Status == FileStatus.Issued)
            .ToListAsync(ct);
        return files.Where(IsOverdue).OrderBy(f => f.FileNumber).ToList();
    }

    private bool IsOverdue(PhysicalFile file) =>
        file.Status == FileStatus.Issued
        && file.Movements
            .Where(m => m.Action is FileMovementAction.Issued or FileMovementAction.Transferred)
            .OrderByDescending(m => m.MovedOn).ThenByDescending(m => m.Id)
            .FirstOrDefault() is { } last
        && last.DueBack is { } due && due < clock.Today;

    public Task<PhysicalFile> IssueAsync(int fileId, string? holderUserId, string holderName,
        string? purpose, DateOnly? dueBack, string recordedById, string recordedByName,
        string? remarks = null, CancellationToken ct = default) =>
        MoveAsync(fileId, FileMovementAction.Issued, recordedById, recordedByName, remarks, ct,
            validate: file =>
            {
                if (file.Status == FileStatus.Issued)
                    throw new InvalidOperationException(
                        $"{file.FileNumber} is already out with {file.HolderName}. Record its return first.");
                if (file.Status == FileStatus.Lost)
                    throw new InvalidOperationException(
                        $"{file.FileNumber} is marked lost — mark it found before issuing it.");
                if (string.IsNullOrWhiteSpace(holderName))
                    throw new InvalidOperationException("Say who is taking the file.");
            },
            apply: (file, movement) =>
            {
                movement.ToHolderUserId = holderUserId;
                movement.ToHolderName = holderName;
                movement.Purpose = purpose;
                movement.DueBack = dueBack;

                file.Status = FileStatus.Issued;
                file.HolderUserId = holderUserId;
                file.HolderName = holderName;
            });

    public Task<PhysicalFile> TransferAsync(int fileId, string? holderUserId, string holderName,
        string? purpose, DateOnly? dueBack, string recordedById, string recordedByName,
        string? remarks = null, CancellationToken ct = default) =>
        MoveAsync(fileId, FileMovementAction.Transferred, recordedById, recordedByName, remarks, ct,
            validate: file =>
            {
                if (file.Status != FileStatus.Issued)
                    throw new InvalidOperationException(
                        "Only a file that is currently out can be handed on. Issue it instead.");
                if (string.IsNullOrWhiteSpace(holderName))
                    throw new InvalidOperationException("Say who is taking the file.");
            },
            apply: (file, movement) =>
            {
                movement.ToHolderUserId = holderUserId;
                movement.ToHolderName = holderName;
                movement.Purpose = purpose;
                movement.DueBack = dueBack;

                file.HolderUserId = holderUserId;
                file.HolderName = holderName;
            });

    public Task<PhysicalFile> ReturnAsync(int fileId, string? location,
        string recordedById, string recordedByName, string? remarks = null,
        CancellationToken ct = default) =>
        MoveAsync(fileId, FileMovementAction.Returned, recordedById, recordedByName, remarks, ct,
            validate: file =>
            {
                if (file.Status != FileStatus.Issued)
                    throw new InvalidOperationException($"{file.FileNumber} is not currently out.");
            },
            apply: (file, movement) =>
            {
                movement.ToLocation = location ?? file.Location;

                file.Status = FileStatus.InRegistry;
                file.HolderUserId = null;
                file.HolderName = null;
                if (!string.IsNullOrWhiteSpace(location)) file.Location = location;
            });

    public Task<PhysicalFile> ArchiveAsync(int fileId, string? location,
        string recordedById, string recordedByName, string? remarks = null,
        CancellationToken ct = default) =>
        MoveAsync(fileId, FileMovementAction.Archived, recordedById, recordedByName, remarks, ct,
            validate: file =>
            {
                if (file.Status == FileStatus.Issued)
                    throw new InvalidOperationException(
                        $"{file.FileNumber} is still out with {file.HolderName}. Record its return first.");
            },
            apply: (file, movement) =>
            {
                movement.ToLocation = location ?? file.Location;

                file.Status = FileStatus.Archived;
                file.HolderUserId = null;
                file.HolderName = null;
                file.ClosedOn = clock.Today;
                if (!string.IsNullOrWhiteSpace(location)) file.Location = location;
            });

    public Task<PhysicalFile> ReopenAsync(int fileId, string recordedById, string recordedByName,
        string? remarks = null, CancellationToken ct = default) =>
        MoveAsync(fileId, FileMovementAction.Reopened, recordedById, recordedByName, remarks, ct,
            validate: file =>
            {
                if (file.Status != FileStatus.Archived)
                    throw new InvalidOperationException("Only an archived file can be reopened.");
            },
            apply: (file, _) =>
            {
                file.Status = FileStatus.InRegistry;
                file.ClosedOn = null;
            });

    public Task<PhysicalFile> MarkLostAsync(int fileId, string recordedById, string recordedByName,
        string? remarks = null, CancellationToken ct = default) =>
        MoveAsync(fileId, FileMovementAction.MarkedLost, recordedById, recordedByName, remarks, ct,
            apply: (file, _) =>
            {
                // The holder is kept: who had it when it went missing is the single
                // most useful thing on the record.
                file.Status = FileStatus.Lost;
            });

    public Task<PhysicalFile> MarkFoundAsync(int fileId, string? location,
        string recordedById, string recordedByName, string? remarks = null,
        CancellationToken ct = default) =>
        MoveAsync(fileId, FileMovementAction.Found, recordedById, recordedByName, remarks, ct,
            validate: file =>
            {
                if (file.Status != FileStatus.Lost)
                    throw new InvalidOperationException("This file is not marked lost.");
            },
            apply: (file, movement) =>
            {
                movement.ToLocation = location ?? file.Location;

                file.Status = FileStatus.InRegistry;
                file.HolderUserId = null;
                file.HolderName = null;
                if (!string.IsNullOrWhiteSpace(location)) file.Location = location;
            });

    public async Task<PhysicalFile> UpdateDetailsAsync(int fileId, string? location,
        string? volumeNumber, string? remarks, CancellationToken ct = default)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new InvalidOperationException("File not found.");

        file.Location = location;
        file.VolumeNumber = volumeNumber;
        file.Remarks = remarks;

        await db.SaveChangesAsync(ct);
        return file;
    }

    /// <summary>
    /// Every state change goes through here so the movement row and the file's own
    /// summary fields are written together — a status that moved without leaving a
    /// history row is exactly the bug a tracking register exists to prevent.
    /// </summary>
    private async Task<PhysicalFile> MoveAsync(
        int fileId, FileMovementAction action, string recordedById, string recordedByName,
        string? remarks, CancellationToken ct,
        Action<PhysicalFile>? validate = null,
        Action<PhysicalFile, FileMovement>? apply = null)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new InvalidOperationException("File not found.");

        validate?.Invoke(file);

        var movement = new FileMovement
        {
            PhysicalFileId = file.Id,
            Action = action,
            MovedOn = clock.Today,
            FromHolderName = file.HolderName,
            FromLocation = file.Location,
            RecordedById = recordedById,
            RecordedByName = recordedByName,
            Remarks = remarks
        };

        apply?.Invoke(file, movement);

        db.FileMovements.Add(movement);
        await db.SaveChangesAsync(ct);
        return file;
    }
}
