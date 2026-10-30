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

    /// <summary>For the void button: a sale is voided once, not twice.</summary>
    public bool CanBeVoided => Void is null;

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

    /// <summary>Builds a sale from the current contents of the cart.</summary>
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
}
