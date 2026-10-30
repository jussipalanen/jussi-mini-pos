using System;
using System.Globalization;

namespace JussiMiniPos.Models;

/// <summary>
/// One VAT rate from the managed list, as stored in the VatRates table.
/// Products point at a rate rather than carrying a percentage, so a change
/// such as 14 % becoming 13,5 % is one edit here and reaches every product.
/// </summary>
/// <param name="Id">Row id.</param>
/// <param name="Name">What the rate is called in the editor, e.g. "Alennettu".</param>
/// <param name="BasisPoints">
/// The rate in hundredths of a percent, so 25,5 % is 2550. An integer keeps
/// rates such as 25,5 % exact in both engines, like money in cents.
/// </param>
/// <param name="IsActive">False keeps the rate out of the editor for new choices.</param>
public sealed record VatRate(int Id, string Name, int BasisPoints, bool IsActive = true)
{
    /// <summary>The rate as a percentage: 25,5 for 2550.</summary>
    public decimal Percent => BasisPoints / 100m;

    /// <summary>"25,5 %", in the application's Finnish formatting.</summary>
    public string PercentText => Vat.FormatPercent(BasisPoints);

    /// <summary>"Yleinen (25,5 %)", for pickers and lists.</summary>
    public string Display => $"{Name} ({PercentText})";
}

/// <summary>
/// VAT arithmetic. Prices in the catalogue and at the till include VAT, so
/// the tax is always worked out of a gross amount, never added to a net one.
/// </summary>
public static class Vat
{
    /// <summary>Highest rate that can be stored: 100 %.</summary>
    public const int MaxBasisPoints = 10_000;

    /// <summary>
    /// The VAT contained in a VAT-inclusive amount, to the cent:
    /// gross × rate / (100 % + rate), rounded half away from zero.
    /// </summary>
    public static decimal Included(decimal gross, int basisPoints)
    {
        if (basisPoints is < 0 or > MaxBasisPoints)
        {
            throw new ArgumentOutOfRangeException(nameof(basisPoints), basisPoints, "VAT rate must be 0–100 %.");
        }

        return decimal.Round(gross * basisPoints / (MaxBasisPoints + basisPoints), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>"25,5 %" for 2550, "0 %" for 0.</summary>
    public static string FormatPercent(int basisPoints) =>
        string.Format(CultureInfo.GetCultureInfo("fi-FI"), "{0:0.##} %", basisPoints / 100m);

    /// <summary>
    /// Reads a percentage typed in the editor — "25,5", "25.5" or "25,5 %" —
    /// as basis points. False for anything outside 0–100 % or finer than
    /// hundredths of a percent.
    /// </summary>
    public static bool TryParsePercent(string text, out int basisPoints)
    {
        basisPoints = 0;
        var cleaned = text.Replace("%", string.Empty).Trim().Replace('.', ',');
        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.GetCultureInfo("fi-FI"), out var percent))
        {
            return false;
        }

        var scaled = percent * 100m;
        if (scaled != decimal.Truncate(scaled) || scaled < 0 || scaled > MaxBasisPoints)
        {
            return false;
        }

        basisPoints = (int)scaled;
        return true;
    }
}

/// <summary>
/// One line of a VAT breakdown: everything sold at one rate. A null rate
/// stands for sale lines written before VAT was recorded.
/// </summary>
public sealed record VatLine(int? BasisPoints, decimal Gross, decimal? Vat)
{
    /// <summary>The amount without VAT, when the VAT is known.</summary>
    public decimal? Net => Vat is { } vat ? Gross - vat : null;

    /// <summary>"ALV 25,5 %", or a note for lines that predate VAT.</summary>
    public string Label => BasisPoints is { } rate ? $"ALV {Models.Vat.FormatPercent(rate)}" : "ALV ei tiedossa";
}
