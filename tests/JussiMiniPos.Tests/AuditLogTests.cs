using JussiMiniPos.Models;
using JussiMiniPos.Services;
using static JussiMiniPos.Services.CatalogRepository;

namespace JussiMiniPos.Tests;

/// <summary>
/// The audit log records who did what, inside the transaction of the change
/// itself, and never records a password.
/// </summary>
public sealed class AuditLogTests
{
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_void_is_recorded_with_who_did_it_and_why(string engine)
    {
        using var test = TestDatabase.Create(engine);
        test.Database.Audit.Actor = "maija";
        var sales = new SalesRepository(test.Database);
        var sale = sales.Save(new Sale(DateTimeOffset.Now, 9.20m, PaymentMethod.Card, [new SaleItem(1, "Kahvi", "", 4.60m, 2)]));

        sales.VoidSale(sale.Id, "maija", "Väärä tuote");

        var entry = Assert.Single(test.Database.Audit.GetEntries());
        Assert.Equal(("maija", AuditActions.SaleVoided), (entry.Actor, entry.Action));
        Assert.Equal($"Myynti #{sale.Id} (9,20 €). Syy: Väärä tuote", entry.Details);
    }

    /// <summary>A change and its entry are one write: if the entry cannot be stored, neither is the change.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_void_that_cannot_be_logged_does_not_happen(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var sale = sales.Save(new Sale(DateTimeOffset.Now, 1.00m, PaymentMethod.Card, [new SaleItem(1, "Vesi", "", 1.00m, 1)]));
        test.Execute("DROP TABLE AuditLog;");

        Assert.ThrowsAny<System.Data.Common.DbException>(() => sales.VoidSale(sale.Id, "maija", "Testi"));

        Assert.Equal(0, test.Scalar("SELECT COUNT(*) FROM Sales WHERE VoidedAt IS NOT NULL;"));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_price_change_is_recorded_as_before_and_after(string engine)
    {
        using var test = TestDatabase.Create(engine);
        test.Database.Audit.Actor = "matti";
        var catalogue = new CatalogRepository(test.Database);
        var category = catalogue.InsertCategory("Juomat", null, isPublic: true);
        var id = catalogue.InsertProduct(new ProductDraft("Kahvi", "", 2.50m, null, test.VatRateId(1350), true, null, [category], []));

        catalogue.UpdateProduct(id, new ProductDraft("Kahvi", "Uusi kuvaus", 2.80m, 2.20m, test.VatRateId(2550), true, null, [category], []));
        catalogue.UpdateProduct(id, new ProductDraft("Kahvi", "Taas uusi", 2.80m, 2.20m, test.VatRateId(2550), true, null, [category], []));
        catalogue.DeleteProduct(id);

        var entries = test.Database.Audit.GetEntries();
        Assert.Equal(
            [AuditActions.ProductDeleted, AuditActions.ProductUpdated, AuditActions.ProductUpdated, AuditActions.ProductCreated],
            entries.Select(e => e.Action));
        Assert.All(entries, entry => Assert.Equal("matti", entry.Actor));
        Assert.Equal($"Kahvi ({id}): 2,50 €, ALV 13,5 %", entries[3].Details);
        Assert.Equal($"Kahvi ({id}): hinta 2,50 € → 2,80 €; tarjoushinta – → 2,20 €; ALV 13,5 % → 25,5 %", entries[2].Details);
        Assert.Equal($"Kahvi ({id}): kuvaus, kuvat tai kategoriat", entries[1].Details);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_VAT_rate_change_is_recorded(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);

        catalogue.UpdateVatRate(test.VatRateId(1350), "Alennettu", 1400, isActive: true);

        var entry = Assert.Single(test.Database.Audit.GetEntries());
        Assert.Equal((AuditActions.VatRateUpdated, "Alennettu (13,5 %): 13,5 % → 14 %"), (entry.Action, entry.Details));
    }

    /// <summary>
    /// Sign-ins are recorded by the person signing in; a wrong password names
    /// the account, but an unknown username is never repeated, since it may be
    /// a password typed into the wrong field.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Sign_ins_are_recorded_without_ever_recording_what_was_typed(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var users = new UserRepository(test.Database);
        users.InsertUser("matti", "matti@example.com", "Oikea-salasana-123", UserRole.Manager);

        users.Authenticate("matti", "Oikea-salasana-123");
        users.Authenticate("matti", "väärä-salasana");
        users.Authenticate("Oikea-salasana-123", "mitä-tahansa");

        var entries = test.Database.Audit.GetEntries();
        Assert.Equal(
            [AuditActions.SignInFailed, AuditActions.SignInFailed, AuditActions.SignIn, AuditActions.UserCreated],
            entries.Select(e => e.Action));
        Assert.Equal("matti", entries[2].Actor);
        Assert.Equal("Väärä salasana käyttäjälle matti.", entries[1].Details);
        Assert.Equal("Tuntematon käyttäjätunnus tai sähköposti.", entries[0].Details);
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain("salasana-123", entry.Details);
            Assert.DoesNotContain("väärä-salasana", entry.Details);
            Assert.DoesNotContain("pbkdf2", entry.Details);
        });
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void User_changes_are_recorded(string engine)
    {
        using var test = TestDatabase.Create(engine);
        test.Database.Audit.Actor = AuditLog.CommandLineActor;
        var users = new UserRepository(test.Database);
        var id = users.InsertUser("matti", "matti@example.com", "Oikea-salasana-123", UserRole.Seller);

        users.UpdateUser(id, "matti", "matti@example.com", UserRole.Manager);
        users.SetPassword(id, "Uusi-salasana-456");
        users.DeleteUser(id);

        var entries = test.Database.Audit.GetEntries();
        Assert.Equal(
            [AuditActions.UserDeleted, AuditActions.PasswordChanged, AuditActions.UserUpdated, AuditActions.UserCreated],
            entries.Select(e => e.Action));
        Assert.Equal("matti: rooli Myyjä → Esimies", entries[2].Details);
        Assert.Equal("matti", entries[1].Details);
        Assert.All(entries, entry => Assert.StartsWith("komentorivi (", entry.Actor));
    }
}
