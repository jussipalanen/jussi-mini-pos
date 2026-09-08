namespace JussiMiniPos.Models;

/// <summary>
/// A single sellable item in the product catalogue.
/// </summary>
/// <param name="Id">Catalogue number shown on the tile and usable in search.</param>
/// <param name="Name">Display name.</param>
/// <param name="Category">Category used by the filter chips.</param>
/// <param name="Price">Unit price in euros.</param>
public record Product(int Id, string Name, string Category, decimal Price);
