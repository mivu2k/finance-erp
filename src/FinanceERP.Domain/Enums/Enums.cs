namespace FinanceERP.Domain.Enums;

public enum AccountType
{
    Asset = 1,
    Liability = 2,
    Equity = 3,
    Income = 4,
    Expense = 5
}

public enum VoucherType
{
    CashPayment = 1,
    CashReceipt = 2,
    BankPayment = 3,
    BankReceipt = 4,
    Journal = 5,
    Adjustment = 6
}

public enum VoucherStatus
{
    Draft = 0,
    Posted = 1,
    Void = 2
}

public enum RequestStatus
{
    Draft = 0,
    PendingManager = 1,
    PendingAdmin = 2,
    PendingAccountant = 3,
    Paid = 4,
    Rejected = 5,
    Cancelled = 6,
    // Advance-kind requests continue past disbursement:
    /// <summary>Money handed over; waiting for the requester's expense justification.</summary>
    Disbursed = 7,
    /// <summary>Justification submitted; pending admin approval.</summary>
    JustificationPending = 8,
    /// <summary>Justification approved; accountant classifies and settles.</summary>
    SettlementReady = 9,
    /// <summary>Actuals posted; advance cleared (difference returned/reimbursed).</summary>
    Settled = 10
}

public enum RequestKind
{
    /// <summary>Itemized claim paid after approval (original flow).</summary>
    Reimbursement = 0,
    /// <summary>Lump-sum advance first, expense justification and settlement after.</summary>
    Advance = 1
}

public enum ApprovalAction
{
    Submitted = 0,
    Approved = 1,
    Rejected = 2,
    Paid = 3,
    Cancelled = 4
}

public enum AdvanceStatus
{
    Draft = 0,
    PendingApproval = 1,
    Approved = 2,
    Disbursed = 3,
    Repaying = 4,
    Settled = 5,
    Rejected = 6,
    Cancelled = 7
}

public enum InstallmentStatus
{
    Pending = 0,
    PartiallyPaid = 1,
    Paid = 2,
    Overdue = 3,
    /// <summary>Employee claims this installment is paid; awaiting accountant confirmation.</summary>
    PendingConfirmation = 4
}

public enum LoanDirection
{
    Taken = 1,
    Given = 2
}

public enum LoanStatus
{
    Active = 0,
    Settled = 1,
    Defaulted = 2,
    Cancelled = 3
}

public enum InvestmentStatus
{
    Active = 0,
    PartiallyWithdrawn = 1,
    Closed = 2
}

public enum InvestmentTxnType
{
    Deposit = 1,
    Profit = 2,
    Loss = 3,
    Withdrawal = 4
}

public enum ThirdPartyType
{
    Customer = 1,
    Supplier = 2,
    Vendor = 3,
    Contractor = 4,
    Investor = 5,
    Lender = 6,
    Borrower = 7,
    Other = 99
}

public enum NotificationType
{
    Info = 0,
    ApprovalRequest = 1,
    Approved = 2,
    Rejected = 3,
    PaymentDue = 4,
    AdvanceDue = 5,
    LoanDue = 6,
    LowCash = 7,
    System = 8
}
