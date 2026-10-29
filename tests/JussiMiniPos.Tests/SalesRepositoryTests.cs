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

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Deleting_a_sale_removes_its_lines(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var kept = sales.Save(new Sale(DateTimeOffset.Now, 1.00m, PaymentMethod.Card, [new SaleItem(1, "Vesi", "", 1.00m, 1)]));
        var deleted = sales.Save(new Sale(DateTimeOffset.Now, 2.00m, PaymentMethod.Card, [new SaleItem(1, "Vesi", "", 1.00m, 2)]));

        sales.DeleteSale(deleted.Id);

        Assert.Equal([kept.Id], sales.GetSales().Select(s => s.Id));
        Assert.Equal(1, test.Scalar("SELECT COUNT(*) FROM SaleItems;"));
    }

    /// <summary>Catalogue edits must never rewrite sale history.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_sale_keeps_the_name_and_price_it_was_sold_at(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var category = catalogue.InsertCategory("Kahvit", null, isPublic: true);
        var productId = catalogue.InsertProduct(new ProductDraft("Kahvi", "", 4.50m, 3.90m, true, null, [category], []));
        var product = Assert.Single(catalogue.GetProducts());

        var sales = new SalesRepository(test.Database);
        sales.Save(Sale.FromCart([new CartLine(product, 2)], PaymentMethod.Card));

        catalogue.UpdateProduct(productId, new ProductDraft("Kahvi uusi", "", 9.99m, null, true, null, [category], []));
        catalogue.DeleteProduct(productId);

        var line = Assert.Single(Assert.Single(sales.GetSales()).Items);
        Assert.Equal(("Kahvi", "Kahvit", 3.90m, 2), (line.Name, line.Category, line.UnitPrice, line.Quantity));
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
