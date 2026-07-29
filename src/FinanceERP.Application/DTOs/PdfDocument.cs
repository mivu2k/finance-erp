using ErpPlatform.Shared.Kernel;

namespace FinanceERP.Application.DTOs;

/// <summary>
/// A single record rendered as a printable document — the shape every "print this form"
/// endpoint fills in. Sections render only when they hold data, so the same model covers
/// a two-line petty cash slip and a full payment request with an approval trail.
/// </summary>
public class PdfDocument
{
    /// <summary>Letterhead — logo, address, footer note. Blank until an admin sets it.</summary>
    public CompanyBranding Company { get; set; } = CompanyBranding.Empty;
    /// <summary>Document class, e.g. "Payment Voucher", "Payslip".</summary>
    public string Title { get; set; } = "";
    /// <summary>The record's own reference, e.g. "PV-2026-00042".</summary>
    public string? DocumentNo { get; set; }
    public string? Subtitle { get; set; }
    /// <summary>Diagonal overlay for non-final states — "DRAFT", "VOID", "UNPAID".</summary>
    public string? Watermark { get; set; }

    /// <summary>Header key/value pairs, laid out in two columns.</summary>
    public List<PdfField> Fields { get; set; } = [];

    /// <summary>Optional line-item table.</summary>
    public string[]? TableHeaders { get; set; }
    public List<string[]> TableRows { get; set; } = [];
    /// <summary>Column indexes rendered right-aligned (amounts).</summary>
    public int[] RightAlignedColumns { get; set; } = [];
    /// <summary>Emphasised final row, e.g. totals.</summary>
    public string[]? TableFooter { get; set; }

    /// <summary>Right-hand summary block below the table.</summary>
    public List<PdfField> Totals { get; set; } = [];

    /// <summary>Who did what, in order — rendered as an audit trail table when present.</summary>
    public List<PdfApprovalRow> Approvals { get; set; } = [];

    public string? Notes { get; set; }
    /// <summary>Signature captions, e.g. "Prepared by", "Approved by", "Received by".</summary>
    public string[] Signatures { get; set; } = [];
    public string? FooterNote { get; set; }
}

public record PdfField(string Label, string? Value, bool Emphasise = false);

public record PdfApprovalRow(string Level, string Actor, string Action, string? Comment, DateTime? When);
