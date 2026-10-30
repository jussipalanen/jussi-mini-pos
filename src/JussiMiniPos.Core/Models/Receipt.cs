using System.Globalization;

namespace JussiMiniPos.Models;

/// <summary>Who the receipt says sold it, as entered in Admin → Kassa.</summary>
/// <param name="Name">Business name, the receipt's heading.</param>
/// <param name="BusinessId">Y-tunnus, validated by <see cref="Models.BusinessId"/>; empty when not set.</param>
/// <param name="Address">Street, postcode and town, one per line.</param>
/// <param name="Phone">Optional contact number.</param>
/// <param name="Footer">A closing line such as "Kiitos käynnistä!"; may be several lines.</param>
public sealed record ShopDetails(string Name, string BusinessId, string Address, string Phone, string Footer)
{
    public static ShopDetails Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// A Finnish receipt has to name the seller and its business ID; the
    /// settings window warns while either is missing.
    /// </summary>
    public bool IsComplete => Name.Trim().Length > 0 && BusinessId.Trim().Length > 0;
}

/// <summary>How a receipt line is laid out by whatever prints it.</summary>
public enum ReceiptLineKind
{
    /// <summary>The shop's name: large, centred.</summary>
    Heading,

    /// <summary>Centred text: address, business ID, footer.</summary>
    Centered,

    /// <summary>A bold, centred marker such as KUITIN KOPIO.</summary>
    Banner,

    /// <summary>Left text and a right-aligned amount.</summary>
    Row,

    /// <summary>A smaller, indented row under an item: "2 × 2,35".</summary>
    Detail,

    /// <summary>The bold total row.</summary>
    Total,

    /// <summary>Four right-aligned columns: rate, VAT-free, VAT, with VAT.</summary>
    VatRow,

    /// <summary>A horizontal rule.</summary>
    Separator,
}

/// <summary>One printed line; <see cref="Cells"/> holds one, two or four texts by kind.</summary>
public sealed record ReceiptLine(ReceiptLineKind Kind, params string[] Cells)
{
    public string Left => Cells.Length > 0 ? Cells[0] : string.Empty;

    public string Right => Cells.Length > 1 ? Cells[^1] : string.Empty;
}

/// <summary>
/// What a receipt says, as lines a printer lays out. Kept apart from printing
/// so the content — the part the law cares about — is tested without a
/// printer: the seller and business ID, the date, each item, the total, the
/// VAT per rate, and how it was paid.
/// </summary>
public static class Receipts
{
    private static readonly CultureInfo Finnish = CultureInfo.GetCultureInfo("fi-FI");

    /// <summary>
    /// Finnish number formatting with a plain hyphen for minus. fi-FI uses the
    /// Unicode minus sign, which not every receipt printer font has.
    /// </summary>
    private static readonly NumberFormatInfo Numbers = CreateNumbers();

    /// <summary>The receipt for a sale. A reprint says so at the top.</summary>
    public static IReadOnlyList<ReceiptLine> ForSale(Sale sale, ShopDetails shop, bool isCopy)
    {
        var lines = Header(shop);

        if (isCopy)
        {
            lines.Add(new(ReceiptLineKind.Banner, "KUITIN KOPIO"));
        }

        if (sale.IsVoided)
        {
            lines.Add(new(ReceiptLineKind.Banner, "MITÄTÖITY"));
        }

        lines.Add(new(ReceiptLineKind.Row, $"Kuitti #{sale.Id}", Date(sale.SoldAt)));
        if (sale.SoldBy is { Length: > 0 } seller)
        {
            lines.Add(new(ReceiptLineKind.Detail, $"Myyjä: {seller}"));
        }

        lines.Add(new(ReceiptLineKind.Separator));

        foreach (var item in sale.Items)
        {
            AddItem(lines, item.Name, item.Quantity, item.UnitPrice, item.LineTotal);
        }

        lines.Add(new(ReceiptLineKind.Separator));

        if (sale.HasRounding)
        {
            lines.Add(new(ReceiptLineKind.Row, "Välisumma", Amount(sale.Subtotal)));
            lines.Add(new(ReceiptLineKind.Row, "Pyöristys", Signed(sale.Rounding)));
        }

        lines.Add(new(ReceiptLineKind.Total, "YHTEENSÄ", Euros(sale.Total)));

        if (sale.CashReceived is { } received)
        {
            lines.Add(new(ReceiptLineKind.Row, "Käteinen", Amount(received)));
            lines.Add(new(ReceiptLineKind.Row, "Takaisin", Amount(sale.Change ?? 0m)));
        }
        else
        {
            lines.Add(new(ReceiptLineKind.Row, sale.PaymentMethodName, Amount(sale.Total)));
        }

        AddVat(lines, sale.VatBreakdown);

        if (sale.Void is { } voided)
        {
            lines.Add(new(ReceiptLineKind.Separator));
            lines.Add(new(ReceiptLineKind.Detail, voided.Display));
        }

        if (sale.HasRefunds)
        {
            lines.Add(new(ReceiptLineKind.Separator));
            foreach (var refund in sale.Refunds)
            {
                lines.Add(new(ReceiptLineKind.Detail, refund.Display));
            }
        }

        AddFooter(lines, shop);
        return lines;
    }

    /// <summary>
    /// The receipt for money given back: what came back, against which sale,
    /// how, by whom and why, with the VAT given back per rate. Amounts are
    /// negative, as on a credit note.
    /// </summary>
    public static IReadOnlyList<ReceiptLine> ForRefund(Sale sale, Refund refund, ShopDetails shop)
    {
        var lines = Header(shop);
        lines.Add(new(ReceiptLineKind.Banner, "PALAUTUSKUITTI"));
        lines.Add(new(ReceiptLineKind.Row, $"Palautus #{refund.Id}", Date(refund.RefundedAt)));
        lines.Add(new(ReceiptLineKind.Row, $"Alkuperäinen kuitti #{sale.Id}", Date(sale.SoldAt)));
        lines.Add(new(ReceiptLineKind.Detail, $"Palauttaja: {refund.RefundedBy}"));
        lines.Add(new(ReceiptLineKind.Detail, $"Syy: {refund.Reason}"));
        lines.Add(new(ReceiptLineKind.Separator));

        foreach (var line in refund.Lines)
        {
            AddItem(lines, line.Name, line.Quantity, line.UnitPrice, -line.LineTotal);
        }

        lines.Add(new(ReceiptLineKind.Separator));

        if (refund.Rounding != 0m)
        {
            lines.Add(new(ReceiptLineKind.Row, "Välisumma", Amount(-refund.Lines.Sum(line => line.LineTotal))));
            lines.Add(new(ReceiptLineKind.Row, "Pyöristys", Signed(-refund.Rounding)));
        }

        lines.Add(new(ReceiptLineKind.Total, "PALAUTETTU", Euros(-refund.Total)));
        lines.Add(new(ReceiptLineKind.Row, refund.PaymentMethodName, Amount(-refund.Total)));

        AddVat(lines, [.. refund.Lines
            .GroupBy(line => line.VatBasisPoints)
            .OrderByDescending(group => group.Key ?? -1)
            .Select(group => new VatLine(
                group.Key,
                -group.Sum(line => line.LineTotal),
                group.Key is null ? null : -group.Sum(line => line.VatAmount ?? 0m)))]);

        AddFooter(lines, shop);
        return lines;
    }

    /// <summary>
    /// The end-of-day report for a till session: who opened and closed it, the
    /// takings per payment method with refunds, voids and rounding, VAT per
    /// rate, and the cash count. Printed on the receipt printer, so it lives
    /// with the receipts.
    /// </summary>
    public static IReadOnlyList<ReceiptLine> ForTillReport(TillReport report, ShopDetails shop)
    {
        var session = report.Session;
        var lines = Header(shop);
        lines.Add(new(ReceiptLineKind.Banner, session.IsOpen ? "VÄLIRAPORTTI" : "PÄIVÄN RAPORTTI"));
        lines.Add(new(ReceiptLineKind.Row, "Kassa", session.Till));
        lines.Add(new(ReceiptLineKind.Row, $"Avattu ({session.OpenedBy})", Date(session.OpenedAt)));
        if (session.ClosedAt is { } closedAt)
        {
            lines.Add(new(ReceiptLineKind.Row, $"Suljettu ({session.ClosedBy})", Date(closedAt)));
        }
        else
        {
            lines.Add(new(ReceiptLineKind.Row, "Tulostettu", Date(report.GeneratedAt)));
        }

        lines.Add(new(ReceiptLineKind.Separator));
        lines.Add(new(ReceiptLineKind.Row, "Myyntejä", $"{report.SaleCount}"));
        lines.Add(new(ReceiptLineKind.Row, "Tuotteita", $"{report.ItemCount}"));

        foreach (var payment in report.Payments)
        {
            lines.Add(new(ReceiptLineKind.Separator));
            lines.Add(new(ReceiptLineKind.Row, $"{payment.Name} ({payment.SaleCount})", Amount(payment.Sales)));
            if (payment.RefundCount > 0)
            {
                lines.Add(new(ReceiptLineKind.Row, $"Palautukset ({payment.RefundCount})", Amount(-payment.Refunds)));
            }

            lines.Add(new(ReceiptLineKind.Detail, $"Netto {Amount(payment.Net)}"));
        }

        lines.Add(new(ReceiptLineKind.Separator));
        lines.Add(new(ReceiptLineKind.Total, "YHTEENSÄ", Euros(report.NetTotal)));
        if (report.RefundCount > 0)
        {
            lines.Add(new(ReceiptLineKind.Detail, $"Myynti {Amount(report.GrossSales)}, palautukset {Amount(-report.RefundTotal)}"));
        }

        if (report.Rounding != 0m)
        {
            lines.Add(new(ReceiptLineKind.Row, "Käteisen pyöristykset", Signed(report.Rounding)));
        }

        if (report.VoidCount > 0)
        {
            lines.Add(new(ReceiptLineKind.Row, $"Mitätöidyt ({report.VoidCount}), ei summissa", Amount(report.Voided)));
        }

        AddVat(lines, report.Vat);

        lines.Add(new(ReceiptLineKind.Separator));
        lines.Add(new(ReceiptLineKind.Row, "Aloituskassa", Amount(session.OpeningFloat)));
        lines.Add(new(ReceiptLineKind.Row, "Käteisellä sisään", Amount(report.Payments.FirstOrDefault(p => p.Method == PaymentMethod.Cash)?.Sales ?? 0m)));
        lines.Add(new(ReceiptLineKind.Row, "Käteisellä ulos", Amount(-(report.Payments.FirstOrDefault(p => p.Method == PaymentMethod.Cash)?.Refunds ?? 0m))));
        lines.Add(new(ReceiptLineKind.Total, "Kassassa pitäisi olla", Euros(session.ExpectedCash ?? report.ExpectedCash)));
        if (session.CountedCash is { } counted)
        {
            lines.Add(new(ReceiptLineKind.Row, "Laskettu", Amount(counted)));
            lines.Add(new(ReceiptLineKind.Row, "Erotus", Signed(session.Difference ?? 0m)));
        }

        if (session.Note is { Length: > 0 } note)
        {
            lines.Add(new(ReceiptLineKind.Detail, $"Huomautus: {note}"));
        }

        return lines;
    }

    private static List<ReceiptLine> Header(ShopDetails shop)
    {
        var lines = new List<ReceiptLine>
        {
            new(ReceiptLineKind.Heading, shop.Name.Trim().Length > 0 ? shop.Name.Trim() : "Kuitti"),
        };

        lines.AddRange(Split(shop.Address).Select(line => new ReceiptLine(ReceiptLineKind.Centered, line)));

        if (shop.Phone.Trim().Length > 0)
        {
            lines.Add(new(ReceiptLineKind.Centered, $"Puh. {shop.Phone.Trim()}"));
        }

        if (shop.BusinessId.Trim().Length > 0)
        {
            lines.Add(new(ReceiptLineKind.Centered, $"Y-tunnus {shop.BusinessId.Trim()}"));
        }

        lines.Add(new(ReceiptLineKind.Separator));
        return lines;
    }

    private static void AddItem(List<ReceiptLine> lines, string name, int quantity, decimal unitPrice, decimal total)
    {
        lines.Add(new(ReceiptLineKind.Row, name, Amount(total)));
        if (quantity != 1)
        {
            lines.Add(new(ReceiptLineKind.Detail, $"{quantity} × {Amount(unitPrice)}"));
        }
    }

    /// <summary>The VAT breakdown a Finnish receipt carries: per rate, VAT-free, VAT and with VAT.</summary>
    private static void AddVat(List<ReceiptLine> lines, IReadOnlyList<VatLine> vat)
    {
        if (vat.Count == 0)
        {
            return;
        }

        lines.Add(new(ReceiptLineKind.Separator));
        lines.Add(new(ReceiptLineKind.VatRow, "ALV", "Veroton", "Vero", "Verollinen"));
        foreach (var line in vat)
        {
            lines.Add(new(ReceiptLineKind.VatRow,
                line.BasisPoints is { } rate ? Vat.FormatPercent(rate) : "ei tiedossa",
                line.Net is { } net ? Amount(net) : "–",
                line.Vat is { } tax ? Amount(tax) : "–",
                Amount(line.Gross)));
        }
    }

    private static void AddFooter(List<ReceiptLine> lines, ShopDetails shop)
    {
        var footer = Split(shop.Footer);
        if (footer.Count == 0)
        {
            return;
        }

        lines.Add(new(ReceiptLineKind.Separator));
        lines.AddRange(footer.Select(line => new ReceiptLine(ReceiptLineKind.Centered, line)));
    }

    private static List<string> Split(string text) =>
        [.. text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];

    /// <summary>
    /// The time as the till's clock showed it: timestamps are stored with the
    /// till's own offset, so a reprint elsewhere does not move the time.
    /// </summary>
    private static string Date(DateTimeOffset when) => when.ToString("dd.MM.yyyy HH.mm", Finnish);

    private static string Amount(decimal amount) => amount.ToString("N2", Numbers);

    private static NumberFormatInfo CreateNumbers()
    {
        var numbers = (NumberFormatInfo)Finnish.NumberFormat.Clone();
        numbers.NegativeSign = "-";
        return NumberFormatInfo.ReadOnly(numbers);
    }

    private static string Euros(decimal amount) => Amount(amount) + " €";

    private static string Signed(decimal amount) => (amount > 0 ? "+" : string.Empty) + Amount(amount);
}
