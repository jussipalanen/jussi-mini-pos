using System.Data.Common;
using JussiMiniPos.Models;
using JussiMiniPos.Services;
using static JussiMiniPos.Services.CatalogRepository;

namespace JussiMiniPos.Tests;

public sealed class BarcodeTests
{
    [Theory]
    [InlineData("6408430000258")] // EAN-13
    [InlineData("4006381333931")] // EAN-13
    [InlineData("96385074")]      // EAN-8
    [InlineData("036000291452")]  // UPC-A, as a scanner reports it
    public void Real_codes_are_accepted(string code) =>
        Assert.Null(Barcode.Validate(code));

    [Theory]
    [InlineData("6408430000259")] // last digit wrong
    [InlineData("4006381333913")] // two digits swapped
    [InlineData("96385075")]
    public void A_wrong_check_digit_is_refused(string code) =>
        Assert.Contains("tarkistusnumero", Barcode.Validate(code));

    [Theory]
    [InlineData("123")]
    [InlineData("12345678901234")]
    public void Other_lengths_are_refused(string code) =>
        Assert.Contains("8, 12 tai 13", Barcode.Validate(code));

    [Fact]
    public void Letters_are_refused() =>
        Assert.Contains("numeroita", Barcode.Validate("64084300A0258"));

    [Fact]
    public void Spaces_a_code_is_read_out_with_are_dropped() =>
        Assert.Equal("6408430000258", Barcode.Normalize(" 6 408430 000258 "));

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_barcode_is_saved_changed_and_cleared(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var category = catalogue.InsertCategory("Juomat", null, isPublic: true);

        // Leading zeros matter: a UPC-A code starts with one.
        var id = catalogue.InsertProduct(Draft(test, category, "036000291452"));
        Assert.Equal("036000291452", Assert.Single(catalogue.GetProducts()).Barcode);
        Assert.Equal(new BarcodeOwner(id, "Kahvi"), catalogue.FindBarcodeOwner("036000291452"));

        catalogue.UpdateProduct(id, Draft(test, category, "6408430000258"));
        Assert.Equal("6408430000258", Assert.Single(catalogue.GetProducts()).Barcode);
        Assert.Null(catalogue.FindBarcodeOwner("036000291452"));

        catalogue.UpdateProduct(id, Draft(test, category, null));
        Assert.Null(Assert.Single(catalogue.GetProducts()).Barcode);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Two_products_cannot_share_a_barcode_but_many_can_have_none(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var category = catalogue.InsertCategory("Juomat", null, isPublic: true);

        catalogue.InsertProduct(Draft(test, category, "6408430000258"));
        Assert.ThrowsAny<DbException>(() => catalogue.InsertProduct(Draft(test, category, "6408430000258")));

        catalogue.InsertProduct(Draft(test, category, null));
        catalogue.InsertProduct(Draft(test, category, null));
        Assert.Equal(3, catalogue.GetProducts().Count);
    }

    /// <summary>A hidden product still owns its code, so the editor cannot hand it out twice.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_hidden_product_still_owns_its_barcode(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var category = catalogue.InsertCategory("Juomat", null, isPublic: true);

        var id = catalogue.InsertProduct(Draft(test, category, "96385074", isPublic: false));

        Assert.Empty(catalogue.GetProducts());
        Assert.Equal(id, catalogue.FindBarcodeOwner("96385074")?.Id);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_barcode_change_is_audited(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var category = catalogue.InsertCategory("Juomat", null, isPublic: true);

        var id = catalogue.InsertProduct(Draft(test, category, "96385074"));
        catalogue.UpdateProduct(id, Draft(test, category, "6408430000258"));

        var entries = new AuditLog(test.Database).GetEntries();
        Assert.Contains(entries, e => e.Action == AuditActions.ProductCreated && e.Details.Contains("viivakoodi 96385074"));
        Assert.Contains(entries, e => e.Action == AuditActions.ProductUpdated
            && e.Details.Contains("viivakoodi 96385074 → 6408430000258"));
    }

    [Fact]
    public void An_old_database_gains_the_barcode_column_and_its_index()
    {
        using var test = TestDatabase.CreateUninitialised(TestDatabase.Sqlite);
        test.Execute(
            """
            CREATE TABLE Products (
                Id    INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT    NOT NULL
            );
            INSERT INTO Products (Title) VALUES ('A'), ('B');
            """);

        test.Reopen().EnsureCreated();

        test.Execute("UPDATE Products SET Barcode = '96385074' WHERE Title = 'A';");
        Assert.ThrowsAny<DbException>(() => test.Execute("UPDATE Products SET Barcode = '96385074' WHERE Title = 'B';"));
    }

    private static ProductDraft Draft(TestDatabase test, int category, string? barcode, bool isPublic = true) =>
        new("Kahvi", "", 2.50m, null, test.VatRateId(), isPublic, null, [category], [], barcode);
}
