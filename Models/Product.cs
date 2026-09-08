using System.Collections.Generic;
using System.Linq;

namespace JussiMiniPos.Models;

/// <summary>
/// A sellable item, as loaded from the Products table.
/// </summary>
/// <param name="Id">Catalogue number, shown in the list and usable in search.</param>
/// <param name="Name">Display name (the Title column).</param>
/// <param name="Description">Longer text, not shown in the till list yet.</param>
/// <param name="Price">Normal price in euros.</param>
/// <param name="SalePrice">Offer price in euros, or null when not on offer.</param>
/// <param name="FeatureImage">Relative path to the list image, if any.</param>
/// <param name="Categories">Every category this product is linked to, main one first.</param>
public sealed record Product(
    int Id,
    string Name,
    string Description,
    decimal Price,
    decimal? SalePrice,
    string? FeatureImage,
    IReadOnlyList<Category> Categories)
{
    /// <summary>True when an offer price applies and actually undercuts the normal one.</summary>
    public bool IsOnSale => SalePrice is { } sale && sale < Price;

    /// <summary>What the customer is charged: the offer price when there is one.</summary>
    public decimal EffectivePrice => IsOnSale ? SalePrice!.Value : Price;

    /// <summary>
    /// The category a receipt line records. Seeding links the main category
    /// first, so that is the one that ends up here.
    /// </summary>
    public string PrimaryCategory => Categories.Count > 0 ? Categories[0].Title : string.Empty;

    public bool IsInCategory(Category category) =>
        category.IsAll || Categories.Any(c => c.Id == category.Id);
}
