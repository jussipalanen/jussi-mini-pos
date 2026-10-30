using System.Globalization;

namespace JussiMiniPos.Views;

/// <summary>Reads a euro amount typed into a form.</summary>
public static class MoneyInput
{
    private static readonly CultureInfo Finnish = CultureInfo.GetCultureInfo("fi-FI");

    /// <summary>
    /// Accepts both "2,50" and "2.50": the app runs in fi-FI, but a numeric
    /// keypad types a full stop. A trailing "€" is ignored.
    /// </summary>
    public static bool TryParse(string text, out decimal value)
    {
        text = text.Trim().TrimEnd('€').Trim().Replace('.', ',');
        return decimal.TryParse(text, NumberStyles.Number, Finnish, out value);
    }
}
