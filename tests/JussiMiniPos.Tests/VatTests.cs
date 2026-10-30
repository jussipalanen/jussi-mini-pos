using System.Data.Common;
using System.Globalization;
using JussiMiniPos.Models;
using JussiMiniPos.Services;
using static JussiMiniPos.Services.CatalogRepository;

namespace JussiMiniPos.Tests;

/// <summary>
/// VAT: the arithmetic, the managed rate list, what a sale records, and what
/// the reports add up. Prices include VAT throughout.
/// </summary>
public sealed class VatTests
{
    [Theory]
    [InlineData("10.00", 2550, "2.03")]
    [InlineData("10.00", 1350, "1.19")]
    [InlineData("1.00", 1000, "0.09")]
    [InlineData("12.34", 0, "0")]
    [InlineData("0", 2550, "0")]
    [InlineData("7.80", 1350, "0.93")]
    public void The_VAT_in_a_gross_price_is_worked_out_to_the_cent(string gross, int basisPoints, string expected)
    {
        Assert.Equal(Money(expected), Vat.Included(Money(gross), basisPoints));
    }

    [Theory]
    [InlineData("25,5", 2550)]
    [InlineData("25.5", 2550)]
    [InlineData("13,5 %", 1350)]
    [InlineData(" 10 ", 1000)]
    [InlineData("0", 0)]
    [InlineData("100", 10000)]
    public void A_typed_percentage_becomes_basis_points(string text, int expected)
    {
        Assert.True(Vat.TryParsePercent(text, out var basisPoints));
        Assert.Equal(expected, basisPoints);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("100,01")]
    [InlineData("25,555")]
    public void A_percentage_outside_0_to_100_or_too_fine_is_refused(string text)
    {
        Assert.False(Vat.TryParsePercent(text, out _));
    }

    [Theory]
    [InlineData(2550, "25,5 %")]
    [InlineData(1000, "10 %")]
    [InlineData(0, "0 %")]
    public void A_rate_is_shown_the_Finnish_way(int basisPoints, string expected)
    {
        Assert.Equal(expected, Vat.FormatPercent(basisPoints));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_new_database_starts_with_the_Finnish_rates(string engine)
    {
        using var test = TestDatabase.Create(engine);

        var rates = new CatalogRepository(test.Database).GetVatRates();

        Assert.Equal([2550, 1350, 1000, 0], rates.Select(rate => rate.BasisPoints));
        Assert.All(rates, rate => Assert.True(rate.IsActive));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Rates_can_be_added_changed_and_retired_but_stay_within_0_to_100(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);

        var id = catalogue.InsertVatRate("Erikois", 2400, isActive: true);
        catalogue.UpdateVatRate(id, "Erikois", 2450, isActive: false);

        var rate = Assert.Single(catalogue.GetVatRates(), r => r.Id == id);
        Assert.Equal(("Erikois", 2450, false), (rate.Name, rate.BasisPoints, rate.IsActive));
        Assert.DoesNotContain(catalogue.GetVatRates(activeOnly: true), r => r.Id == id);

        Assert.ThrowsAny<DbException>(() => catalogue.InsertVatRate("Liikaa", 10_001, isActive: true));
    }

    /// <summary>A rate change reaches every product on the rate at once; that is what the list is for.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Changing_a_rate_reaches_every_product_on_it(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var category = catalogue.InsertCategory("Ruoat", null, isPublic: true);
        var food = test.VatRateId(1350);
        catalogue.InsertProduct(Draft("Puuro", food, category));
        catalogue.InsertProduct(Draft("Keitto", food, category));

        Assert.Equal(2, catalogue.CountProductsWithVatRate(food));
        catalogue.UpdateVatRate(food, "Alennettu", 1400, isActive: true);

        Assert.All(catalogue.GetProducts(), product => Assert.Equal(1400, product.Vat.BasisPoints));
    }

    /// <summary>A row written without a rate, by hand or by an old tool, is charged the highest rate.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_product_without_a_rate_is_charged_the_highest_rate(string engine)
    {
        using var test = TestDatabase.Create(engine);
        test.Execute("INSERT INTO Products (Title, PriceCents) VALUES ('Käsin lisätty', 100);");

        var product = Assert.Single(new CatalogRepository(test.Database).GetProducts());

        Assert.Equal(2550, product.Vat.BasisPoints);
    }

    [Fact]
    public void A_sale_records_each_line_at_its_products_rate()
    {
        var coffee = Product(1, "Kahvi", 2.50m, 1350);
        var mug = Product(2, "Muki", 9.90m, 2550);

        var sale = Sale.FromCart([new CartLine(coffee, 2), new CartLine(mug, 1)], PaymentMethod.Card);

        Assert.Equal(14.90m, sale.Total);
        Assert.Equal(
            [(1350, 0.59m), (2550, 2.01m)],
            sale.Items.Select(item => (item.VatBasisPoints ?? -1, item.VatAmount ?? -1m)));

        // Highest rate first, and the net and gross add back up to the total.
        Assert.Collection(sale.VatBreakdown,
            line => Assert.Equal((2550, 9.90m, 2.01m, 7.89m), (line.BasisPoints ?? -1, line.Gross, line.Vat ?? -1m, line.Net ?? -1m)),
            line => Assert.Equal((1350, 5.00m, 0.59m, 4.41m), (line.BasisPoints ?? -1, line.Gross, line.Vat ?? -1m, line.Net ?? -1m)));
        Assert.Equal(2.60m, sale.VatTotal);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_saved_sale_keeps_its_VAT_exactly(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var sale = Sale.FromCart(
            [new CartLine(Product(1, "Kahvi", 2.50m, 1350), 2), new CartLine(Product(2, "Muki", 9.90m, 2550), 1)],
            PaymentMethod.Card);

        sales.Save(sale);

        var stored = Assert.Single(sales.GetSales());
        // Ids are assigned on save, so compare what was sold, not which row it landed in.
        Assert.Equal(sale.Items, stored.Items.Select(item => item with { Id = 0 }));
        Assert.Equal(2.60m, stored.VatTotal);
    }

    /// <summary>
    /// Raportit sums the VAT each line recorded, per rate. Lines from before VAT
    /// was recorded form their own group with no VAT figure.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_report_breaks_VAT_down_per_rate(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var soldAt = DateTimeOffset.Parse("2026-05-04T12:00:00+03:00", CultureInfo.InvariantCulture);

        sales.Save(new Sale(soldAt, 14.90m, PaymentMethod.Card,
        [
            SaleItem.Taxed(1, "Kahvi", "Juomat", 2.50m, 2, 1350),
            SaleItem.Taxed(2, "Muki", "Tarvikkeet", 9.90m, 1, 2550),
        ]));
        sales.Save(new Sale(soldAt.AddHours(1), 4.00m, PaymentMethod.Card,
            [SaleItem.Taxed(3, "Pulla", "Leivonnaiset", 4.00m, 1, 1350)]));
        sales.Save(new Sale(soldAt.AddHours(2), 1.00m, PaymentMethod.Card,
            [new SaleItem(4, "Vanha", "Muut", 1.00m, 1)]));

        var day = DateOnly.FromDateTime(soldAt.Date);
        var report = new ReportsRepository(test.Database).GetReport(day, day);

        Assert.Equal(
            [(2550, 9.90m, (decimal?)2.01m), (1350, 9.00m, 0.59m + 0.48m), (-1, 1.00m, null)],
            report.VatLines.Select(line => (line.BasisPoints ?? -1, line.Gross, line.Vat)));
        Assert.Null(report.VatTotal);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void The_demo_catalogue_is_seeded_at_the_food_rate(string engine)
    {
        using var test = TestDatabase.Create(engine);

        CatalogSeeder.Seed(test.Database);

        Assert.All(new CatalogRepository(test.Database).GetProducts(), product => Assert.Equal(1350, product.Vat.BasisPoints));
    }

    /// <summary>
    /// A PostgreSQL database from before VAT, as 1.0.0-beta.4 created it: the
    /// migration has to add the columns there too, not only in SQLite.
    /// </summary>
    [Fact]
    public void A_PostgreSQL_database_from_before_VAT_is_migrated()
    {
        using var test = TestDatabase.Create(TestDatabase.PostgreSql);
        test.Execute(
            """
            ALTER TABLE Products DROP COLUMN VatRateId;
            ALTER TABLE SaleItems DROP COLUMN VatRateBasisPoints;
            ALTER TABLE SaleItems DROP COLUMN VatCents;
            DROP TABLE VatRates;
            INSERT INTO Products (Title, PriceCents) VALUES ('Vanha', 300);
            """);

        test.Reopen().EnsureCreated();

        var product = Assert.Single(new CatalogRepository(test.Database).GetProducts());
        Assert.Equal(2550, product.Vat.BasisPoints);
    }

    private static ProductDraft Draft(string title, int vatRateId, int categoryId) =>
        new(title, "", 5.00m, null, vatRateId, true, null, [categoryId], []);

    private static Product Product(int id, string name, decimal price, int basisPoints) =>
        new(id, name, "", price, null, null, true, [new Category(1, "Testi", null)])
        {
            Vat = new VatRate(basisPoints, "Testi", basisPoints),
        };

    private static decimal Money(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
