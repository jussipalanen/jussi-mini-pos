using System;
using System.Collections.Generic;
using System.Linq;

namespace JussiMiniPos.Models;

/// <summary>
/// One returned line of a refund: which sold line, how many of it, and the
/// money and VAT given back for them.
/// </summary>
/// <param name="SaleItemId">The sold line this returns, by its row id.</param>
/// <param name="Name">The product name as sold, for display.</param>
/// <param name="UnitPrice">The price it was sold at, VAT included.</param>
/// <param name="VatBasisPoints">The rate it was sold at; null when that was never recorded.</param>
/// <param name="VatAmount">The VAT given back, to the cent; null with an unknown rate.</param>
public sealed record RefundLine(
    long SaleItemId,
    string Name,
    decimal UnitPrice,
    int Quantity,
    int? VatBasisPoints,
    decimal? VatAmount)
{
    public decimal LineTotal => UnitPrice * Quantity;
}

/// <summary>
/// Money given back against a sale. A refund is its own record linked to the
/// sale and its lines; the sale itself is never edited, so what was sold and
/// what came back both stay on file.
/// </summary>
/// <param name="Total">What was paid back: the lines plus <see cref="Rounding"/>.</param>
/// <param name="Rounding">Cash rounding to five cents, zero for card.</param>
public sealed record Refund(
    long Id,
    long SaleId,
    DateTimeOffset RefundedAt,
    string RefundedBy,
    string Reason,
    PaymentMethod PaymentMethod,
    decimal Total,
    decimal Rounding,
    IReadOnlyList<RefundLine> Lines)
{
    public int ItemCount => Lines.Sum(line => line.Quantity);

    public string PaymentMethodName => PaymentMethodNames.Finnish(PaymentMethod);

    /// <summary>"Palautettu 30.10.2026 14.30 (matti), Käteinen 5,00 €: Rikkinäinen", for the receipt.</summary>
    public string Display =>
        $"Palautettu {RefundedAt:dd.MM.yyyy HH:mm} ({RefundedBy}), {PaymentMethodName} {Total:N2} €: {Reason}";
}

/// <summary>A line to give back, as chosen in the refund dialog.</summary>
public sealed record RefundRequestLine(long SaleItemId, int Quantity);
