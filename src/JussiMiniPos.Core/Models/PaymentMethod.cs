namespace JussiMiniPos.Models;

/// <summary>
/// How a sale was paid. The enum name is what gets stored in the database, so
/// renaming a member changes existing rows' meaning — add, don't rename.
/// </summary>
public enum PaymentMethod
{
    Card,
}

/// <summary>Finnish display names for <see cref="PaymentMethod"/>.</summary>
public static class PaymentMethodNames
{
    public static string Finnish(PaymentMethod method) => method switch
    {
        PaymentMethod.Card => "Maksukortti",
        _ => method.ToString(),
    };
}
