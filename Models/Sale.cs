using System;
using System.Collections.Generic;
using System.Linq;

namespace JussiMiniPos.Models;

/// <summary>
/// One line of a completed sale. This is a snapshot: the name and unit price
/// are copied from the product at the time of sale, so later catalogue edits
/// never rewrite history.
/// </summary>
public sealed record SaleItem(
    int ProductId,
    string Name,
    string Category,
    decimal UnitPrice,
    int Quantity)
{
    public decimal LineTotal => UnitPrice * Quantity;
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

    /// <summary>Number of individual items across all lines.</summary>
    public int ItemCount => Items.Sum(item => item.Quantity);

    /// <summary>Finnish name of the payment method, for display.</summary>
    public string PaymentMethodName => PaymentMethodNames.Finnish(PaymentMethod);

    /// <summary>Builds a sale from the current contents of the cart.</summary>
    public static Sale FromCart(IEnumerable<CartLine> cart, PaymentMethod paymentMethod)
    {
        var items = cart
            .Select(line => new SaleItem(
                line.Product.Id,
                line.Product.Name,
                line.Product.PrimaryCategory,
                line.UnitPrice,
                line.Quantity))
            .ToList();

        return new Sale(
            DateTimeOffset.Now,
            items.Sum(item => item.LineTotal),
            paymentMethod,
            items);
    }
}
