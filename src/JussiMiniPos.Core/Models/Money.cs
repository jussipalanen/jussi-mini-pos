namespace JussiMiniPos.Models;

/// <summary>Exact conversions between domain euros and whole cents for storage and transport.</summary>
public static class Money
{
    /// <summary>Rounds half a cent away from zero, including negative amounts.</summary>
    public static long ToCents(decimal amount) =>
        (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal FromCents(long cents) => cents / 100m;
}
