namespace TopupLedger.Domain.Ledger;

public static class LedgerEntryTypes
{
    public const string TopupDebit = "TOPUP_DEBIT";
    public const string TopupDebitContra = "TOPUP_DEBIT_CONTRA";
    public const string TopupRefund = "TOPUP_REFUND";
    public const string TopupRefundContra = "TOPUP_REFUND_CONTRA";
    public const string DistributorShare = "DISTRIBUTOR_SHARE";
    public const string DistributorShareContra = "DISTRIBUTOR_SHARE_CONTRA";
    public const string ManualCredit = "MANUAL_CREDIT";
    public const string ManualCreditContra = "MANUAL_CREDIT_CONTRA";
    public const string OpeningBalance = "OPENING_BALANCE";
    public const string OpeningBalanceContra = "OPENING_BALANCE_CONTRA";
}

/// <summary>
/// A movement is two rows with opposite signs. Credits are positive, debits are negative.
/// The pair sums to zero. Historical rows are never edited; a refund is a new pair.
/// </summary>
public static class LedgerPair
{
    public static (long DebitAccountAmount, long CreditAccountAmount) Split(long amountFils)
    {
        if (amountFils <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountFils), "A ledger movement amount must be a positive number of fils.");
        }

        return (-amountFils, amountFils);
    }
}
