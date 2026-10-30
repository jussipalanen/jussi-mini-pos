namespace JussiMiniPos.Models;

/// <summary>
/// Rounding a cash total to the nearest five cents, as Finnish shops do since
/// one- and two-cent coins left circulation. Card payments are never rounded.
/// </summary>
/// <remarks>
/// Endings 1 and 2 round down, 3 and 4 up to 5, 6 and 7 down to 5, and 8 and
/// 9 up. A cent total never lands exactly halfway, so there is no midpoint
/// rule to choose. Only the total is rounded: the lines and their VAT stay as
/// sold, and the difference is recorded on the sale as its own amount.
/// </remarks>
public static class CashRounding
{
    /// <summary><paramref name="total"/> to the nearest five cents.</summary>
    public static decimal Round(decimal total) =>
        decimal.Round(total * 20m, 0, MidpointRounding.AwayFromZero) / 20m;
}
