using System.Data.Common;
using JussiMiniPos.Models;
using JussiMiniPos.Services;
using static JussiMiniPos.Services.CatalogRepository;

namespace JussiMiniPos.Tests;

public sealed class CatalogRepositoryTests
{
    /// <summary>The category given first is the one a receipt line records.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void The_first_category_given_stays_the_primary_one(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var older = catalogue.InsertCategory("Juomat", null, isPublic: true);
        var newer = catalogue.InsertCategory("Kahvit", null, isPublic: true);

        var id = catalogue.InsertProduct(Draft(test, "Kahvi", [newer, older]));
        Assert.Equal("Kahvit", Assert.Single(catalogue.GetProducts()).PrimaryCategory);

        catalogue.UpdateProduct(id, Draft(test, "Kahvi", [older, newer]));
        Assert.Equal("Juomat", Assert.Single(catalogue.GetProducts()).PrimaryCategory);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_parent_category_with_children_cannot_be_deleted(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var parent = catalogue.InsertCategory("Juomat", null, isPublic: true);
        var child = catalogue.InsertCategory("Kahvit", parent, isPublic: true);
        catalogue.InsertProduct(Draft(test, "Kahvi", [child]));

        Assert.Equal(1, catalogue.CountChildCategories(parent));
        Assert.ThrowsAny<DbException>(() => catalogue.DeleteCategory(parent));

        // The child can go; its product stays, just without the link.
        catalogue.DeleteCategory(child);
        catalogue.DeleteCategory(parent);
        var product = Assert.Single(catalogue.GetProducts());
        Assert.Empty(product.Categories);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Hidden_products_and_categories_stay_out_of_the_till(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var visible = catalogue.InsertCategory("Juomat", null, isPublic: true);
        var hiddenCategory = catalogue.InsertCategory("Varasto", null, isPublic: false);
        catalogue.InsertProduct(Draft(test, "Kahvi", [visible]));
        catalogue.InsertProduct(Draft(test, "Salainen", [visible], isPublic: false));

        Assert.Equal(["Kahvi"], catalogue.GetProducts().Select(p => p.Name));
        Assert.Equal(["Kahvi", "Salainen"], catalogue.GetProducts(publicOnly: false).Select(p => p.Name).Order());

        Assert.DoesNotContain(catalogue.GetCategories(), c => c.Id == hiddenCategory);
        Assert.Contains(catalogue.GetCategories(publicOnly: false), c => c.Id == hiddenCategory);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Prices_and_the_gallery_read_back_in_order(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var category = catalogue.InsertCategory("Juomat", null, isPublic: true);
        catalogue.InsertProduct(new ProductDraft(
            "Kahvi", "Tumma paahto", 4.50m, 3.95m, test.VatRateId(1350), true, "images/feature.jpg", [category],
            ["images/b.jpg", "images/a.jpg"]));

        var product = Assert.Single(catalogue.GetProducts());
        Assert.Equal((4.50m, 3.95m, 3.95m), (product.Price, product.SalePrice, product.EffectivePrice));
        Assert.Equal(1350, product.Vat.BasisPoints);
        Assert.Equal("images/feature.jpg", product.FeatureImage);
        Assert.Equal(["images/b.jpg", "images/a.jpg"], product.Images);
    }

    /// <summary>A missing offer price means no discount, and an offer that does not undercut is ignored.</summary>
    [Theory]
    [InlineData(null, 4.50, false)]
    [InlineData(3.90, 3.90, true)]
    [InlineData(4.50, 4.50, false)]
    [InlineData(5.00, 4.50, false)]
    public void The_till_charges_the_effective_price(double? salePrice, double charged, bool onSale)
    {
        var product = new Product(1, "Kahvi", "", 4.50m, (decimal?)salePrice, null, true, [])
        {
            Vat = new VatRate(1, "Alennettu", 1350),
        };

        Assert.Equal((decimal)charged, product.EffectivePrice);
        Assert.Equal(onSale, product.IsOnSale);
    }

    private static ProductDraft Draft(TestDatabase test, string title, IReadOnlyList<int> categoryIds, bool isPublic = true) =>
        new(title, "", 1.00m, null, test.VatRateId(), isPublic, null, categoryIds, []);
}
