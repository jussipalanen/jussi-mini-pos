using System;
using System.Collections.Generic;
using System.Linq;

namespace JussiMiniPos.Models;

/// <summary>
/// One line of a completed sale. This is a snapshot: the name, unit price and
/// VAT are copied from the product at the time of sale, so later catalogue or
/// rate edits never rewrite history.
/// </summary>
/// <param name="VatBasisPoints">
/// The rate the line was sold at, in hundredths of a percent. Null only for
/// lines sold before VAT was recorded.
/// </param>
/// <param name="VatAmount">The VAT contained in <see cref="LineTotal"/>, to the cent.</param>
public sealed record SaleItem(
    int ProductId,
    string Name,
    string Category,
    decimal UnitPrice,
    int Quantity,
    int? VatBasisPoints = null,
    decimal? VatAmount = null)
{
    /// <summary>Database row id, which a refund points back at; zero until saved.</summary>
    public long Id { get; init; }

    public decimal LineTotal => UnitPrice * Quantity;

    /// <summary>A line taxed at <paramref name="basisPoints"/>, with its VAT worked out.</summary>
    public static SaleItem Taxed(int productId, string name, string category, decimal unitPrice, int quantity, int basisPoints) =>
        new(productId, name, category, unitPrice, quantity, basisPoints,
            Vat.Included(unitPrice * quantity, basisPoints));
}

/// <summary>Who voided a sale, when, and why.</summary>
/// <param name="At">When it was voided.</param>
/// <param name="By">The username of whoever voided it, copied so it survives the account.</param>
/// <param name="Reason">Why, as typed when voiding. Never empty.</param>
public sealed record SaleVoid(DateTimeOffset At, string By, string Reason)
{
    /// <summary>"Mitätöity 04.05.2026 14.30 (matti): Väärä tuote", for the receipt.</summary>
    public string Display => $"Mitätöity {At:dd.MM.yyyy HH:mm} ({By}): {Reason}";
}

/// <summary>A completed, paid checkout.</summary>
/// <param name="Total">
/// What the customer was charged: the sum of the lines plus <see cref="Rounding"/>.
/// </param>
public sealed record Sale(
    DateTimeOffset SoldAt,
    decimal Total,
    PaymentMethod PaymentMethod,
    IReadOnlyList<SaleItem> Items)
{
    /// <summary>Database row id, assigned when the sale is saved.</summary>
    public long Id { get; init; }

    /// <summary>
    /// Set once the sale has been voided. A voided sale stays in the database
    /// with its lines, so the record of what happened is never lost; it is
    /// only left out of totals and reports.
    /// </summary>
    public SaleVoid? Void { get; init; }

    public bool IsVoided => Void is not null;

    /// <summary>
    /// The username of the seller who rang it up, copied so it survives the
    /// account. Null for sales made before sign-in was required at the till.
    /// </summary>
    public string? SoldBy { get; init; }

    /// <summary>
    /// What cash rounding added to the lines, from -0,02 to +0,02 €; zero for
    /// card and for cash when the shop does not round. Kept apart from the
    /// lines so their VAT stays exactly what was sold.
    /// </summary>
    public decimal Rounding { get; init; }

    /// <summary>The cash the customer handed over, or null for a card payment.</summary>
    public decimal? CashReceived { get; init; }

    /// <summary>Change given back, for a cash sale.</summary>
    public decimal? Change => CashReceived is { } received ? received - Total : null;

    /// <summary>The lines added up, before any rounding.</summary>
    public decimal Subtotal => Items.Sum(item => item.LineTotal);

    public bool HasRounding => Rounding != 0m;

    public bool IsCash => CashReceived is not null;

    /// <summary>Money given back against this sale, oldest first.</summary>
    public IReadOnlyList<Refund> Refunds { get; init; } = [];

    public bool HasRefunds => Refunds.Count > 0;

    /// <summary>Everything paid back so far.</summary>
    public decimal RefundedTotal => Refunds.Sum(refund => refund.Total);

    /// <summary>How many of a sold line have come back already.</summary>
    public int RefundedQuantity(long saleItemId) =>
        Refunds.SelectMany(refund => refund.Lines).Where(line => line.SaleItemId == saleItemId).Sum(line => line.Quantity);

    /// <summary>How many of a sold line can still be returned.</summary>
    public int RefundableQuantity(SaleItem item) => item.Quantity - RefundedQuantity(item.Id);

    /// <summary>
    /// A sale can take a refund while it stands and something is left to
    /// return. A voided sale never happened, so nothing can come back from it.
    /// </summary>
    public bool CanBeRefunded => Void is null && Items.Any(item => RefundableQuantity(item) > 0);

    /// <summary>
    /// For the void button: a sale is voided once, not twice, and not after
    /// money has been given back against it — voiding would leave the refund
    /// paying out for a sale that no longer counts.
    /// </summary>
    public bool CanBeVoided => Void is null && Refunds.Count == 0;

    /// <summary>Number of individual items across all lines.</summary>
    public int ItemCount => Items.Sum(item => item.Quantity);

    /// <summary>Finnish name of the payment method, for display.</summary>
    public string PaymentMethodName => PaymentMethodNames.Finnish(PaymentMethod);

    /// <summary>
    /// What the receipt shows under the total: per rate, the gross amount and
    /// the VAT in it, highest rate first. Each line's VAT was rounded when it
    /// was sold, and these are the sums of those, so the receipt adds up to
    /// exactly what was recorded.
    /// </summary>
    public IReadOnlyList<VatLine> VatBreakdown =>
        [.. Items
            .GroupBy(item => item.VatBasisPoints)
            .OrderByDescending(group => group.Key ?? -1)
            .Select(group => new VatLine(
                group.Key,
                group.Sum(item => item.LineTotal),
                group.Key is null ? null : group.Sum(item => item.VatAmount ?? 0m)))];

    /// <summary>The VAT in the whole sale, when every line recorded it.</summary>
    public decimal? VatTotal => Items.All(item => item.VatAmount is not null)
        ? Items.Sum(item => item.VatAmount ?? 0m)
        : null;

    /// <summary>Builds a card sale from the current contents of the cart.</summary>
    public static Sale FromCart(IEnumerable<CartLine> cart, PaymentMethod paymentMethod)
    {
        var items = cart
            .Select(line => SaleItem.Taxed(
                line.Product.Id,
                line.Product.Name,
                line.Product.PrimaryCategory,
                line.UnitPrice,
                line.Quantity,
                line.Product.Vat.BasisPoints))
            .ToList();

        return new Sale(
            DateTimeOffset.Now,
            items.Sum(item => item.LineTotal),
            paymentMethod,
            items);
    }

    /// <summary>
    /// What a cash customer is asked for: the cart's total, rounded to five
    /// cents when <paramref name="roundCash"/> is on.
    /// </summary>
    public static decimal CashTotal(decimal subtotal, bool roundCash) =>
        roundCash ? CashRounding.Round(subtotal) : subtotal;

    /// <summary>Builds a cash sale, with its rounding and the cash handed over.</summary>
    /// <exception cref="ArgumentException">The cash does not cover the total.</exception>
    public static Sale FromCartForCash(IEnumerable<CartLine> cart, bool roundCash, decimal cashReceived)
    {
        var sale = FromCart(cart, PaymentMethod.Cash);
        var total = CashTotal(sale.Total, roundCash);

        if (cashReceived < total)
        {
            throw new ArgumentException("Annettu käteinen ei riitä maksuun.", nameof(cashReceived));
        }

        return sale with
        {
            Total = total,
            Rounding = total - sale.Total,
            CashReceived = cashReceived,
        };
    }
}
