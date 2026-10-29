using System.Globalization;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Tests;

/// <summary>
/// Raportit files every sale under its date on the Finnish wall clock,
/// daylight saving included, and totals the sale snapshots.
/// </summary>
public sealed class ReportsTests
{
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_sale_is_filed_under_its_Finnish_calendar_date(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);

        // Winter, EET (+02): 23:59 on the 14th, then 00:30 on the 15th, which
        // is still the 14th in UTC.
        Save(sales, "2026-01-14T21:59:00+00:00", "Talvi ilta");
        Save(sales, "2026-01-14T22:30:00+00:00", "Talvi yö");

        // Summer, EEST (+03): 00:30 on 16 July. A fixed +02 offset would put
        // it at 23:30 on the 15th.
        Save(sales, "2026-07-15T21:30:00+00:00", "Kesä yö");

        // Stored with an offset that is neither UTC nor Finnish: 23:00 UTC on
        // the 15th, which is 02:00 on 16 July in Helsinki.
        Save(sales, "2026-07-16T08:00:00+09:00", "Tokio");

        // The night clocks go forward (29 March 2026, 03:00 EET -> 04:00
        // EEST). 00:30 on the 30th; a fixed +02 would say 23:30 on the 29th.
        Save(sales, "2026-03-29T21:30:00+00:00", "Kesäaika alkoi");

        var reports = new ReportsRepository(test.Database);
        Assert.Equal(["Talvi ilta"], ProductNames(reports, "2026-01-14"));
        Assert.Equal(["Talvi yö"], ProductNames(reports, "2026-01-15"));
        Assert.Empty(ProductNames(reports, "2026-07-15"));
        Assert.Equal(["Kesä yö", "Tokio"], ProductNames(reports, "2026-07-16"));
        Assert.Empty(ProductNames(reports, "2026-03-29"));
        Assert.Equal(["Kesäaika alkoi"], ProductNames(reports, "2026-03-30"));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_report_totals_sales_days_products_and_categories(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);

        sales.Save(Sale("2026-05-04T10:00:00+03:00",
            new SaleItem(1, "Kahvi", "Kahvit", 4.50m, 2),
            new SaleItem(2, "Pulla", "Leivonnaiset", 2.20m, 1)));
        sales.Save(Sale("2026-05-04T15:00:00+03:00",
            new SaleItem(1, "Kahvi", "Kahvit", 4.50m, 1),
            new SaleItem(3, "Vesi", "", 1.00m, 3)));
        sales.Save(Sale("2026-05-05T09:00:00+03:00",
            new SaleItem(2, "Pulla", "Leivonnaiset", 2.20m, 2)));

        var report = new ReportsRepository(test.Database).GetReport(Date("2026-05-04"), Date("2026-05-05"));

        Assert.Equal(3, report.SaleCount);
        Assert.Equal(9, report.ItemCount);
        Assert.Equal(23.10m, report.Total);
        Assert.Equal(7.70m, report.AverageSale);

        Assert.Collection(report.Days,
            day => Assert.Equal((new DateTime(2026, 5, 4), 2L, 7L, 18.70m), (day.Date, day.SaleCount, day.ItemCount, day.Total)),
            day => Assert.Equal((new DateTime(2026, 5, 5), 1L, 2L, 4.40m), (day.Date, day.SaleCount, day.ItemCount, day.Total)));

        // Highest takings first.
        Assert.Equal(
            [("Kahvi", 3L, 13.50m), ("Pulla", 3L, 6.60m), ("Vesi", 3L, 3.00m)],
            report.Products.Select(p => (p.Name, p.ItemCount, p.Total)));

        Assert.Equal(
            [("Kahvit", 13.50m), ("Leivonnaiset", 6.60m), ("Ei kategoriaa", 3.00m)],
            report.Categories.Select(c => (c.Name, c.Total)));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_day_lists_its_sales_newest_first_with_their_lines(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var morning = sales.Save(Sale("2026-05-04T08:00:00+03:00", new SaleItem(1, "Kahvi", "Kahvit", 4.50m, 1)));
        var evening = sales.Save(Sale("2026-05-04T20:00:00+03:00",
            new SaleItem(2, "Pulla", "Leivonnaiset", 2.20m, 2),
            new SaleItem(3, "Vesi", "Juomat", 1.00m, 1)));
        sales.Save(Sale("2026-05-05T08:00:00+03:00", new SaleItem(1, "Kahvi", "Kahvit", 4.50m, 1)));

        var day = new ReportsRepository(test.Database).GetDaySales(Date("2026-05-04"));

        Assert.Equal([evening.Id, morning.Id], day.Select(s => s.Id));
        Assert.Equal(["Pulla", "Vesi"], day[0].Items.Select(i => i.Name));
        Assert.Equal(5.40m, day[0].Total);
    }

    [Fact]
    public void A_range_that_ends_before_it_starts_is_refused()
    {
        using var test = TestDatabase.Create(TestDatabase.Sqlite);

        Assert.Throws<ArgumentException>(() =>
            new ReportsRepository(test.Database).GetReport(Date("2026-05-05"), Date("2026-05-04")));
    }

    private static void Save(SalesRepository sales, string soldAt, string productName) =>
        sales.Save(Sale(soldAt, new SaleItem(1, productName, "Testi", 1.00m, 1)));

    private static Sale Sale(string soldAt, params SaleItem[] items) =>
        new(DateTimeOffset.Parse(soldAt, CultureInfo.InvariantCulture),
            items.Sum(item => item.LineTotal),
            PaymentMethod.Card,
            items);

    private static IEnumerable<string> ProductNames(ReportsRepository reports, string day) =>
        reports.GetReport(Date(day), Date(day)).Products.Select(p => p.Name).Order(StringComparer.Ordinal);

    private static DateOnly Date(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
