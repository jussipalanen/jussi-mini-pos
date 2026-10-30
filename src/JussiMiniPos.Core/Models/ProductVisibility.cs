namespace JussiMiniPos.Models;

/// <summary>The catalogue boundary shared by the till, assistant and future API.</summary>
public static class ProductVisibility
{
    /// <summary>
    /// Hidden products never reach a public catalogue. Category visibility only
    /// hides category labels; it does not hide an otherwise public product.
    /// </summary>
    public static bool IsVisible(Product product) => product.IsPublic;
}
