using FinanceERP.Domain.Enums;

namespace FinanceERP.Application.DTOs;

public class VoucherEditDto
{
    public int Id { get; set; }
    public VoucherType Type { get; set; } = VoucherType.Journal;
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string? Narration { get; set; }
    public List<VoucherLineEditDto> Lines { get; set; } = [];
}

public class VoucherLineEditDto
{
    public int Id { get; set; }
    public int? AccountId { get; set; }
    public string? Description { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public int? CostCenterId { get; set; }
    public int? DepartmentId { get; set; }
    public int? ProjectId { get; set; }
    public int? ThirdPartyId { get; set; }
}

public record VoucherListItemDto(int Id, string VoucherNo, VoucherType Type, VoucherStatus Status,
    DateOnly Date, string? Narration, decimal Amount, string Source, string? CreatedBy);
