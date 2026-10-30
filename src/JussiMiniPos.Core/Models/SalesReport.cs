namespace JussiMiniPos.Models;

public sealed record ReportDay(DateTime Date, long SaleCount, long ItemCount, decimal Total)
{
    /// <summary>Money given back that day, already taken out of <see cref="Total"/>.</summary>
    public decimal Refunded { get; init; }

    public double BarWidth { get; init; }
}

public sealed record ReportProduct(int ProductId, string Name, long ItemCount, decimal Total);

public sealed record ReportCategory(string Name, long ItemCount, decimal Total);

public sealed record SalesReport(
    DateOnly From,
    DateOnly Through,
    long SaleCount,
    long ItemCount,
    decimal Total,
    IReadOnlyList<ReportDay> Days,
    IReadOnlyList<ReportProduct> Products,
    IReadOnlyList<ReportCategory> Categories,
    IReadOnlyList<VatLine> VatLines)
{
    /// <summary>Refunds made in the range, wherever their sales were made.</summary>
    public long RefundCount { get; init; }

    /// <summary>Money given back in the range, already taken out of <see cref="Total"/>.</summary>
    public decimal RefundTotal { get; init; }

    public bool HasRefunds => RefundCount > 0;

    /// <summary>What the sales themselves came to, before refunds.</summary>
    public decimal GrossSales => Total + RefundTotal;

    /// <summary>The average sale, before refunds, so a return does not make a sale look smaller.</summary>
    public decimal AverageSale => SaleCount == 0 ? 0 : GrossSales / SaleCount;

    /// <summary>The VAT across the range, when every line in it recorded VAT.</summary>
    public decimal? VatTotal => VatLines.All(line => line.Vat is not null)
        ? VatLines.Sum(line => line.Vat ?? 0m)
        : null;
}
