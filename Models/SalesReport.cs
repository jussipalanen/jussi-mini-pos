namespace JussiMiniPos.Models;

public sealed record ReportDay(DateTime Date, long SaleCount, long ItemCount, decimal Total)
{
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
    IReadOnlyList<ReportCategory> Categories)
{
    public decimal AverageSale => SaleCount == 0 ? 0 : Total / SaleCount;
}
