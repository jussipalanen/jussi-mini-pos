using System.Data.Common;
using JussiMiniPos.Models;
using JussiMiniPos.Services;
using static JussiMiniPos.Services.CatalogRepository;

namespace JussiMiniPos.Tests;

public sealed class SalesRepositoryTests
{
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_saved_sale_reads_back_exactly(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var soldAt = new DateTimeOffset(2026, 5, 4, 12, 30, 15, TimeSpan.FromHours(3));

        var first = sales.Save(new Sale(soldAt, 9.20m, PaymentMethod.Card,
        [
            new SaleItem(1, "Kahvi", "Kahvit", 4.50m, 1),
            new SaleItem(2, "Pulla", "Leivonnaiset", 2.35m, 2),
        ]));
        var second = sales.Save(new Sale(soldAt.AddMinutes(5), 0.10m, PaymentMethod.Card,
            [new SaleItem(3, "Tikkari", "Makeiset", 0.10m, 1)]));

        Assert.True(second.Id > first.Id);

        var stored = sales.GetSales();

        // Newest first.
        Assert.Equal([second.Id, first.Id], stored.Select(s => s.Id));
        var read = stored[1];
        Assert.Equal(soldAt, read.SoldAt);
        Assert.Equal(9.20m, read.Total);
        Assert.Equal(
            [("Kahvi", "Kahvit", 4.50m, 1), ("Pulla", "Leivonnaiset", 2.35m, 2)],
            read.Items.Select(i => (i.Name, i.Category, i.UnitPrice, i.Quantity)));
    }

    /// <summary>
    /// A sale and its lines are one write: if any line fails, nothing of the
    /// sale is left behind.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_sale_whose_line_fails_is_not_stored_at_all(string engine)
    {
        using var test = TestDatabase.Create(engine);
        RejectLinesNamed(test, engine, "VIRHE");
        var sales = new SalesRepository(test.Database);

        Assert.ThrowsAny<DbException>(() => sales.Save(new Sale(DateTimeOffset.Now, 5.50m, PaymentMethod.Card,
        [
            new SaleItem(1, "Kahvi", "Kahvit", 4.50m, 1),
            new SaleItem(2, "VIRHE", "Testi", 1.00m, 1),
        ])));

        Assert.Equal(0, test.Scalar("SELECT COUNT(*) FROM Sales;"));
        Assert.Equal(0, test.Scalar("SELECT COUNT(*) FROM SaleItems;"));
    }

    /// <summary>
    /// Voiding keeps the sale and its lines, marked with who, when and why;
    /// nothing is deleted, so the record of what happened survives.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_voided_sale_keeps_its_lines_and_records_who_why_and_when(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var kept = sales.Save(new Sale(DateTimeOffset.Now, 1.00m, PaymentMethod.Card, [new SaleItem(1, "Vesi", "", 1.00m, 1)]));
        var voided = sales.Save(new Sale(DateTimeOffset.Now, 2.00m, PaymentMethod.Card, [new SaleItem(1, "Vesi", "", 1.00m, 2)]));
        var before = DateTimeOffset.Now.AddSeconds(-1);

        sales.VoidSale(voided.Id, "maija", "  Väärä tuote  ");

        var stored = sales.GetSales();
        Assert.Equal([voided.Id, kept.Id], stored.Select(s => s.Id));
        Assert.Equal(2, test.Scalar("SELECT COUNT(*) FROM SaleItems;"));

        var record = Assert.IsType<SaleVoid>(stored[0].Void);
        Assert.Equal(("maija", "Väärä tuote"), (record.By, record.Reason));
        Assert.True(record.At >= before);
        Assert.Null(stored[1].Void);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_sale_is_voided_once_and_never_without_a_reason(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var sale = sales.Save(new Sale(DateTimeOffset.Now, 1.00m, PaymentMethod.Card, [new SaleItem(1, "Vesi", "", 1.00m, 1)]));

        Assert.Throws<ArgumentException>(() => sales.VoidSale(sale.Id, "maija", "   "));
        Assert.Null(Assert.Single(sales.GetSales()).Void);

        sales.VoidSale(sale.Id, "maija", "Testimyynti");

        // A second void must not overwrite who did it first.
        Assert.Throws<InvalidOperationException>(() => sales.VoidSale(sale.Id, "matti", "Uudestaan"));
        Assert.Equal("maija", Assert.Single(sales.GetSales()).Void?.By);

        Assert.Throws<InvalidOperationException>(() => sales.VoidSale(9999, "maija", "Ei ole"));
    }

    /// <summary>Voided sales stay listed but drop out of every report figure.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_voided_sale_is_left_out_of_reports_and_product_totals(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var soldAt = DateTimeOffset.Parse("2026-05-04T12:00:00+03:00", System.Globalization.CultureInfo.InvariantCulture);
        sales.Save(new Sale(soldAt, 4.50m, PaymentMethod.Card, [SaleItem.Taxed(7, "Kahvi", "Juomat", 4.50m, 1, 1350)]));
        var voided = sales.Save(new Sale(soldAt.AddHours(1), 9.00m, PaymentMethod.Card, [SaleItem.Taxed(7, "Kahvi", "Juomat", 4.50m, 2, 1350)]));

        sales.VoidSale(voided.Id, "maija", "Asiakas peruutti");

        var day = DateOnly.FromDateTime(soldAt.Date);
        var reports = new ReportsRepository(test.Database);
        var report = reports.GetReport(day, day);
        Assert.Equal((1L, 1L, 4.50m), (report.SaleCount, report.ItemCount, report.Total));
        Assert.Equal(4.50m, Assert.Single(report.VatLines).Gross);
        Assert.Single(reports.GetDaySales(day));

        var summary = new CatalogRepository(test.Database).GetSalesSummary(7);
        Assert.Equal((1, 1, 4.50m), (summary.Lines, summary.Quantity, summary.Revenue));
    }

    /// <summary>Catalogue edits must never rewrite sale history.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_sale_keeps_the_name_and_price_it_was_sold_at(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var category = catalogue.InsertCategory("Kahvit", null, isPublic: true);
        var food = test.VatRateId(1350);
        var productId = catalogue.InsertProduct(new ProductDraft("Kahvi", "", 4.50m, 3.90m, food, true, null, [category], []));
        var product = Assert.Single(catalogue.GetProducts());

        var sales = new SalesRepository(test.Database);
        sales.Save(Sale.FromCart([new CartLine(product, 2)], PaymentMethod.Card));

        catalogue.UpdateProduct(productId, new ProductDraft("Kahvi uusi", "", 9.99m, null, test.VatRateId(2550), true, null, [category], []));
        catalogue.UpdateVatRate(food, "Alennettu", 1400, isActive: true);
        catalogue.DeleteProduct(productId);

        var line = Assert.Single(Assert.Single(sales.GetSales()).Items);
        Assert.Equal(("Kahvi", "Kahvit", 3.90m, 2), (line.Name, line.Category, line.UnitPrice, line.Quantity));

        // 7,80 sold at 13,5 % holds 0,93 of VAT, whatever the rate became since.
        Assert.Equal((1350, 0.93m), (line.VatBasisPoints, line.VatAmount));
    }

    [Theory]
    [InlineData("0.10", 10)]
    [InlineData("19.99", 1999)]
    [InlineData("0.105", 11)]
    [InlineData("0.115", 12)]
    [InlineData("-0.105", -11)]
    public void Money_is_stored_as_whole_cents_rounded_half_away_from_zero(string amount, long cents)
    {
        Assert.Equal(cents, SalesRepository.ToCents(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Cents_read_back_as_exact_euros()
    {
        Assert.Equal(19.99m, SalesRepository.FromCents(1999));
        Assert.Equal(0.01m, SalesRepository.FromCents(1));
    }

    /// <summary>A trigger that makes inserting a line with this name fail, in each engine's syntax.</summary>
    private static void RejectLinesNamed(TestDatabase test, string engine, string name)
    {
        test.Execute(engine == TestDatabase.Sqlite
            ? $"""
              CREATE TRIGGER RejectLine BEFORE INSERT ON SaleItems
              WHEN NEW.Name = '{name}'
              BEGIN SELECT RAISE(ABORT, 'rejected by test'); END;
              """
            : $"""
              CREATE FUNCTION reject_line() RETURNS trigger AS $$
              BEGIN
                  IF NEW.Name = '{name}' THEN RAISE EXCEPTION 'rejected by test'; END IF;
                  RETURN NEW;
              END $$ LANGUAGE plpgsql;
              CREATE TRIGGER RejectLine BEFORE INSERT ON SaleItems
              FOR EACH ROW EXECUTE FUNCTION reject_line();
              """);
    }
}
