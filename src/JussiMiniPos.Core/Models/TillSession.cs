namespace JussiMiniPos.Models;

/// <summary>
/// One till's working period, from opening with a cash float to closing with
/// the cash counted. Sales and refunds made meanwhile point back at it, which
/// is what the end-of-day report adds up.
/// </summary>
/// <param name="Till">Which till: the computer's name, so tills sharing a database keep their own drawers.</param>
/// <param name="OpeningFloat">The change money in the drawer at opening.</param>
public sealed record TillSession(long Id, string Till, DateTimeOffset OpenedAt, string OpenedBy, decimal OpeningFloat)
{
    public DateTimeOffset? ClosedAt { get; init; }

    public string? ClosedBy { get; init; }

    /// <summary>What the drawer should have held at closing, worked out from the session's cash movements.</summary>
    public decimal? ExpectedCash { get; init; }

    /// <summary>What was counted in the drawer at closing.</summary>
    public decimal? CountedCash { get; init; }

    /// <summary>Anything said about the closing, such as why the cash was short.</summary>
    public string? Note { get; init; }

    public bool IsOpen => ClosedAt is null;

    /// <summary>Counted less expected: negative when cash is missing.</summary>
    public decimal? Difference => CountedCash - ExpectedCash;
}

/// <summary>One payment method's share of a session: what came in, what went back.</summary>
public sealed record PaymentTotal(PaymentMethod Method, int SaleCount, decimal Sales, int RefundCount, decimal Refunds)
{
    public decimal Net => Sales - Refunds;

    public string Name => PaymentMethodNames.Finnish(Method);
}

/// <summary>
/// The end-of-day (Z) report for one till session: takings per payment
/// method, refunds, voids, cash rounding, VAT per rate, and the cash
/// reconciliation. Voided sales are counted but left out of every total.
/// </summary>
public sealed record TillReport(
    TillSession Session,
    IReadOnlyList<PaymentTotal> Payments,
    IReadOnlyList<VatLine> Vat,
    int SaleCount,
    int ItemCount,
    int VoidCount,
    decimal Voided,
    decimal Rounding,
    DateTimeOffset GeneratedAt)
{
    public decimal GrossSales => Payments.Sum(payment => payment.Sales);

    public decimal RefundTotal => Payments.Sum(payment => payment.Refunds);

    public int RefundCount => Payments.Sum(payment => payment.RefundCount);

    public decimal NetTotal => GrossSales - RefundTotal;

    private PaymentTotal? Cash => Payments.FirstOrDefault(payment => payment.Method == PaymentMethod.Cash);

    /// <summary>The float, plus cash taken, less cash given back: what should be in the drawer.</summary>
    public decimal ExpectedCash => Session.OpeningFloat + (Cash?.Sales ?? 0m) - (Cash?.Refunds ?? 0m);
}
