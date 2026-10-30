using System.Linq;

namespace JussiMiniPos.Models;

/// <summary>
/// The barcodes a product can carry: EAN-8, UPC-A and EAN-13, all GTINs with
/// the same modulo-10 check digit.
/// </summary>
/// <remarks>
/// The check digit is verified rather than trusted, so a code typed by hand
/// with one digit wrong is refused at the product editor instead of silently
/// never matching a scan. A scanner reports UPC-A as twelve digits, so it is
/// accepted as it arrives rather than padded to thirteen.
/// </remarks>
public static class Barcode
{
    /// <summary>The digit counts a till scanner reports for retail goods.</summary>
    public static readonly int[] Lengths = [8, 12, 13];

    /// <summary>
    /// Trims what was typed or scanned and drops inner spaces, which printed
    /// codes are often read out with. Empty means no barcode.
    /// </summary>
    public static string Normalize(string? text) =>
        string.Concat((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// Null when <paramref name="code"/> is a valid barcode, otherwise the
    /// Finnish reason it is not. Expects a normalised code.
    /// </summary>
    public static string? Validate(string code)
    {
        if (!code.All(char.IsAsciiDigit))
        {
            return "Viivakoodissa saa olla vain numeroita.";
        }

        if (!Lengths.Contains(code.Length))
        {
            return "Viivakoodin on oltava 8, 12 tai 13 numeroa (EAN-8, UPC-A tai EAN-13).";
        }

        if (CheckDigit(code[..^1]) != code[^1] - '0')
        {
            return "Viivakoodin tarkistusnumero ei täsmää. Tarkista numerot.";
        }

        return null;
    }

    /// <summary>True for a code that <see cref="Validate"/> accepts.</summary>
    public static bool IsValid(string code) => Validate(code) is null;

    /// <summary>
    /// The GTIN check digit for the digits before it: weights 3 and 1
    /// alternating from the rightmost digit, then whatever tops the sum up to
    /// a multiple of ten.
    /// </summary>
    public static int CheckDigit(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[digits.Length - 1 - i] - '0';
            sum += i % 2 == 0 ? digit * 3 : digit;
        }

        return (10 - sum % 10) % 10;
    }
}
