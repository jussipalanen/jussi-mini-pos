using JussiMiniPos.Services;
using static JussiMiniPos.Services.CatalogRepository;

namespace JussiMiniPos.Tests;

/// <summary>The demo catalogue behind <c>--seed</c>, <c>--seed --reset</c> and <c>--clear</c>.</summary>
public sealed class CatalogSeederTests
{
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Clearing_removes_the_catalogue_but_keeps_sales(string engine)
    {
        using var test = TestDatabase.Create(engine);
        CatalogSeeder.Seed(test.Database);
        test.Execute("INSERT INTO Sales (\"DateTime\", TotalCents, PaymentMethod) VALUES ('2026-05-04T12:00:00+03:00', 250, 'Card');");

        CatalogSeeder.Clear(test.Database);

        Assert.False(CatalogSeeder.HasData(test.Database));
        Assert.Equal(1, test.Scalar("SELECT COUNT(*) FROM Sales;"));
    }

    /// <summary>
    /// What <c>--seed --reset</c> does. The seeder writes the demo ids
    /// (1001 and up) explicitly, so the numbering for products added
    /// afterwards has to carry on above them on both engines.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Reseeding_replaces_the_catalogue_and_new_products_number_after_it(string engine)
    {
        using var test = TestDatabase.Create(engine);
        CatalogSeeder.Seed(test.Database);

        CatalogSeeder.Clear(test.Database);
        var result = CatalogSeeder.Seed(test.Database);

        Assert.Equal(DemoCatalog.Products.Count, result.Products);
        var catalogue = new CatalogRepository(test.Database);
        Assert.Equal(DemoCatalog.Products.Count, catalogue.GetProducts().Count);

        var id = catalogue.InsertProduct(new ProductDraft(
            "Uusi", "", 1.00m, null, test.VatRateId(), true, null, [catalogue.GetCategories()[0].Id], []));
        Assert.True(id > DemoCatalog.Products.Max(p => p.Id), $"New product got id {id}.");
    }
}
