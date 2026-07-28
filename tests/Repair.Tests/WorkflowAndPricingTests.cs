using Repair.Domain;
using Repair.Infrastructure;
using Xunit;

namespace Repair.Tests;

/// <summary>The repair pipeline as a state machine — no database needed.</summary>
public class JobWorkflowTests
{
    [Theory]
    [InlineData(JobStatus.Received, JobStatus.Diagnosing)]
    [InlineData(JobStatus.Diagnosing, JobStatus.WaitingApproval)]
    [InlineData(JobStatus.WaitingApproval, JobStatus.InProgress)]
    [InlineData(JobStatus.InProgress, JobStatus.Completed)]
    [InlineData(JobStatus.Completed, JobStatus.Delivered)]
    public void The_normal_path_is_allowed(JobStatus from, JobStatus to)
    {
        Assert.True(JobWorkflow.CanMove(from, to));
    }

    [Fact]
    public void A_delivered_job_is_terminal()
    {
        // The single most important rule: history doesn't get rewritten.
        Assert.Empty(JobWorkflow.NextFrom(JobStatus.Delivered));
        Assert.False(JobWorkflow.CanMove(JobStatus.Delivered, JobStatus.Diagnosing));
        Assert.False(JobWorkflow.CanMove(JobStatus.Delivered, JobStatus.InProgress));
    }

    [Fact]
    public void A_cancelled_job_is_terminal()
    {
        Assert.Empty(JobWorkflow.NextFrom(JobStatus.Cancelled));
    }

    [Fact]
    public void A_job_cannot_skip_straight_from_received_to_delivered()
    {
        Assert.False(JobWorkflow.CanMove(JobStatus.Received, JobStatus.Delivered));
        Assert.Throws<InvalidOperationException>(
            () => JobWorkflow.EnsureCanMove(JobStatus.Received, JobStatus.Delivered));
    }

    [Fact]
    public void Work_can_go_back_for_re_diagnosis()
    {
        // Real workshops do send a job back; the machine must allow it.
        Assert.True(JobWorkflow.CanMove(JobStatus.InProgress, JobStatus.Diagnosing));
        Assert.True(JobWorkflow.CanMove(JobStatus.Completed, JobStatus.InProgress));
    }

    [Fact]
    public void Cancelling_is_available_until_the_job_is_completed()
    {
        foreach (var status in new[] { JobStatus.Received, JobStatus.Diagnosing,
                                       JobStatus.WaitingApproval, JobStatus.InProgress })
            Assert.True(JobWorkflow.CanMove(status, JobStatus.Cancelled));

        Assert.False(JobWorkflow.CanMove(JobStatus.Completed, JobStatus.Cancelled));
    }

    [Fact]
    public void Moving_to_the_status_it_already_holds_is_refused_clearly()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => JobWorkflow.EnsureCanMove(JobStatus.InProgress, JobStatus.InProgress));

        Assert.Contains("already", error.Message);
    }

    [Fact]
    public void Open_covers_everything_still_in_the_workshop()
    {
        Assert.Contains(JobStatus.Received, JobWorkflow.Open);
        Assert.Contains(JobStatus.InProgress, JobWorkflow.Open);
        Assert.DoesNotContain(JobStatus.Delivered, JobWorkflow.Open);
        Assert.DoesNotContain(JobStatus.Cancelled, JobWorkflow.Open);
    }
}

/// <summary>The quotation arithmetic — what the customer is asked to pay.</summary>
public class QuotationPricingTests
{
    private static Quotation Quote(params (QuotationItemType Type, decimal Qty, decimal Price, decimal Discount)[] lines) =>
        new()
        {
            Items = lines.Select(l => new QuotationItem
            {
                ItemType = l.Type,
                Description = "line",
                Quantity = l.Qty,
                UnitPrice = l.Price,
                Discount = l.Discount
            }).ToList()
        };

    [Fact]
    public void Line_totals_are_quantity_times_price_less_the_line_discount()
    {
        var q = Quote((QuotationItemType.Part, 2, 16000, 1000));

        IQuotationService.Recalculate(q);

        Assert.Equal(31000m, q.Items[0].LineTotal);
    }

    [Fact]
    public void Parts_and_labour_are_totalled_separately()
    {
        var q = Quote(
            (QuotationItemType.Part, 1, 52000, 0),
            (QuotationItemType.Part, 1, 16000, 0),
            (QuotationItemType.Labor, 1, 25000, 0));

        IQuotationService.Recalculate(q);

        Assert.Equal(68000m, q.PartsAmount);
        Assert.Equal(25000m, q.LaborAmount);
        Assert.Equal(93000m, q.Subtotal);
    }

    [Fact]
    public void Tax_is_charged_after_the_header_discount()
    {
        var q = Quote((QuotationItemType.Part, 1, 100000, 0));
        q.DiscountAmount = 10000;
        q.TaxPercent = 17;

        IQuotationService.Recalculate(q);

        // 17% of 90,000, not of 100,000 — the customer isn't taxed on a discount
        // they never received.
        Assert.Equal(15300m, q.TaxAmount);
        Assert.Equal(105300m, q.TotalAmount);
    }

    [Fact]
    public void A_header_labour_charge_survives_when_there_are_no_labour_lines()
    {
        var q = Quote((QuotationItemType.Part, 1, 68000, 0));
        q.LaborAmount = 25000;

        IQuotationService.Recalculate(q);

        Assert.Equal(25000m, q.LaborAmount);
        Assert.Equal(93000m, q.Subtotal);
    }

    [Fact]
    public void Labour_lines_override_a_header_labour_charge()
    {
        // Otherwise the same labour would be billed twice.
        var q = Quote((QuotationItemType.Labor, 1, 30000, 0));
        q.LaborAmount = 25000;

        IQuotationService.Recalculate(q);

        Assert.Equal(30000m, q.LaborAmount);
        Assert.Equal(30000m, q.Subtotal);
    }

    [Fact]
    public void An_empty_quotation_totals_to_nothing_rather_than_throwing()
    {
        var q = Quote();

        IQuotationService.Recalculate(q);

        Assert.Equal(0m, q.TotalAmount);
    }

    [Fact]
    public void Rounding_lands_on_two_decimals()
    {
        var q = Quote((QuotationItemType.Part, 3, 33.333m, 0));
        q.TaxPercent = 17;

        IQuotationService.Recalculate(q);

        Assert.Equal(100.00m, q.Items[0].LineTotal);
        Assert.Equal(17.00m, q.TaxAmount);
        Assert.Equal(117.00m, q.TotalAmount);
    }
}

/// <summary>The purchase total arithmetic, which is pure and worth pinning.</summary>
public class PurchaseTotalTests
{
    [Fact]
    public void Total_is_subtotal_less_discount_plus_tax_and_freight()
    {
        var purchase = new PartPurchase
        {
            Items =
            [
                new PartPurchaseItem { Quantity = 2, UnitCost = 34000 },
                new PartPurchaseItem { Quantity = 1, UnitCost = 12000 }
            ],
            DiscountAmount = 5000,
            TaxAmount = 3000,
            OtherCharges = 1500
        };

        IPurchaseService.Recalculate(purchase);

        Assert.Equal(80000m, purchase.Subtotal);
        Assert.Equal(79500m, purchase.TotalAmount);
    }

    [Fact]
    public void Fractional_quantities_are_handled()
    {
        // Cable and fluids are bought by the metre and the litre.
        var purchase = new PartPurchase
        {
            Items = [new PartPurchaseItem { Quantity = 2.5m, UnitCost = 340 }]
        };

        IPurchaseService.Recalculate(purchase);

        Assert.Equal(850m, purchase.Subtotal);
    }
}
