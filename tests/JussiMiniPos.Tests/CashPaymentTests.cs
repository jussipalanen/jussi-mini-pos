using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Tests;

public sealed class CashPaymentTests
{
    /// <summary>Endings 1–2 and 6–7 round down, 3–4 and 8–9 up.</summary>
    [Theory]
    [InlineData(1.00, 1.00)]
    [InlineData(1.01, 1.00)]
    [InlineData(1.02, 1.00)]
    [InlineData(1.03, 1.05)]
    [InlineData(1.04, 1.05)]
    [InlineData(1.05, 1.05)]
    [InlineData(1.06, 1.05)]
    [InlineData(1.07, 1.05)]
    [InlineData(1.08, 1.10)]
    [InlineData(1.09, 1.10)]
    [InlineData(0.01, 0.00)]
    [InlineData(99.98, 100.00)]
    public void Cash_rounds_to_the_nearest_five_cents(double total, double rounded) =>
        Assert.Equal((decimal)rounded, CashRounding.Round((decimal)total));

    [Fact]
    public void A_rounded_cash_sale_keeps_its_lines_and_vat_as_sold()
    {
        var sale = Sale.FromCartForCash(Cart(4.98m), roundCash: true, cashReceived: 10m);

        Assert.Equal(PaymentMethod.Cash, sale.PaymentMethod);
        Assert.Equal(5.00m, sale.Total);
        Assert.Equal(4.98m, sale.Subtotal);
        Assert.Equal(0.02m, sale.Rounding);
        Assert.Equal(5.00m, sale.Change);
        Assert.Equal(4.98m, sale.VatBreakdown.Sum(line => line.Gross));
    }

    [Fact]
    public void Without_rounding_cash_is_charged_to_the_cent()
    {
        var sale = Sale.FromCartForCash(Cart(4.98m), roundCash: false, cashReceived: 4.98m);

        Assert.Equal(4.98m, sale.Total);
        Assert.Equal(0m, sale.Rounding);
        Assert.Equal(0m, sale.Change);
    }

    [Fact]
    public void Cash_that_does_not_cover_the_total_is_refused() =>
        Assert.Throws<ArgumentException>(() => Sale.FromCartForCash(Cart(4.98m), roundCash: true, cashReceived: 4.95m));

    [Fact]
    public void A_card_sale_is_never_rounded()
    {
        var sale = Sale.FromCart(Cart(4.98m), PaymentMethod.Card);

        Assert.Equal(4.98m, sale.Total);
        Assert.Equal(0m, sale.Rounding);
        Assert.Null(sale.CashReceived);
        Assert.Null(sale.Change);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_cash_sale_reads_back_with_its_rounding_and_change(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);

        var saved = sales.Save(Sale.FromCartForCash(Cart(12.37m), roundCash: true, cashReceived: 20m));
        sales.Save(Sale.FromCart(Cart(3.33m), PaymentMethod.Card));

        var cash = Assert.Single(sales.GetSales(), sale => sale.Id == saved.Id);
        Assert.Equal(PaymentMethod.Cash, cash.PaymentMethod);
        Assert.Equal((12.35m, -0.02m, 20m, 7.65m), (cash.Total, cash.Rounding, cash.CashReceived, cash.Change));

        var card = Assert.Single(sales.GetSales(), sale => sale.Id != saved.Id);
        Assert.Equal((3.33m, 0m, (decimal?)null), (card.Total, card.Rounding, card.CashReceived));
    }

    /// <summary>The report's total is the money taken, rounding included.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Report_totals_count_what_was_charged(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var sale = sales.Save(Sale.FromCartForCash(Cart(12.37m), roundCash: true, cashReceived: 20m));
        var day = ReportsRepository.LocalDate(sale.SoldAt);

        var reports = new ReportsRepository(test.Database);
        Assert.Equal(12.35m, reports.GetReport(day, day).Total);

        var daySale = Assert.Single(reports.GetDaySales(day));
        Assert.Equal((12.35m, -0.02m, 20m), (daySale.Total, daySale.Rounding, daySale.CashReceived));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Rounding_is_on_until_turned_off_and_the_change_is_audited(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var options = new OptionsRepository(test.Database);

        Assert.True(TillSettings.Load(options).RoundCash);

        var before = TillSettings.Load(options);
        before.Save(options, before);
        Assert.Empty(new AuditLog(test.Database).GetEntries());

        (before with { RoundCash = false }).Save(options, before);
        Assert.False(TillSettings.Load(options).RoundCash);

        var entry = Assert.Single(new AuditLog(test.Database).GetEntries());
        Assert.Equal(AuditActions.SettingsChanged, entry.Action);
        Assert.Equal("käteistä ei pyöristetä", entry.Details);
    }

    [Fact]
    public void Sales_from_before_cash_read_as_unrounded_card_sales()
    {
        using var test = TestDatabase.CreateUninitialised(TestDatabase.Sqlite);
        test.Execute(
            """
            CREATE TABLE Sales (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                "DateTime"    TEXT    NOT NULL,
                TotalCents    INTEGER NOT NULL,
                PaymentMethod TEXT    NOT NULL
            );
            INSERT INTO Sales ("DateTime", TotalCents, PaymentMethod) VALUES ('2026-01-10T12:00:00.0000000+02:00', 250, 'Card');
            """);

        var database = test.Reopen();
        database.EnsureCreated();

        var sale = Assert.Single(new SalesRepository(database).GetSales());
        Assert.Equal((PaymentMethod.Card, 2.50m, 0m, (decimal?)null), (sale.PaymentMethod, sale.Total, sale.Rounding, sale.CashReceived));
    }

    /// <summary>One line of <paramref name="price"/>, at the general rate.</summary>
    private static List<CartLine> Cart(decimal price) =>
        [new CartLine(new Product(1, "Kahvi", "", price, null, null, true, []) { Vat = new VatRate(1, "Yleinen", 2550) })];
}
