using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Tests;

public sealed class RefundTests
{
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_refund_is_its_own_record_and_the_sale_is_untouched(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var sale = SaveCoffeeAndBuns(sales);
        var buns = BunLine(sales, sale.Id);

        var refund = sales.Refund(sale.Id, [new RefundRequestLine(buns.Id, 1)], PaymentMethod.Card, true, "matti", " Rikki ");

        Assert.Equal((2.35m, 0m, "Rikki", "matti"), (refund.Total, refund.Rounding, refund.Reason, refund.RefundedBy));

        var read = Assert.Single(sales.GetSales());
        Assert.Equal(9.20m, read.Total);
        Assert.Equal(2, read.Items.Single(item => item.Name == "Pulla").Quantity);

        var stored = Assert.Single(read.Refunds);
        Assert.Equal(refund.Id, stored.Id);
        Assert.Equal([("Pulla", 1, 2.35m)], stored.Lines.Select(line => (line.Name, line.Quantity, line.LineTotal)));
        Assert.Equal(1, read.RefundableQuantity(read.Items.Single(item => item.Name == "Pulla")));
        Assert.Equal(2.35m, read.RefundedTotal);
    }

    /// <summary>
    /// Returning a line bit by bit gives back exactly the VAT it recorded, so
    /// rounding on each part cannot leave a cent of VAT behind or overshoot.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_line_returned_in_parts_gives_back_exactly_its_vat(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var sale = sales.Save(new Sale(DateTimeOffset.Now, 1.00m, PaymentMethod.Card,
            [SaleItem.Taxed(1, "Karkki", "Makeiset", 0.10m, 10, 1350)]));
        var line = Assert.Single(Assert.Single(sales.GetSales()).Items);

        var vatBack = 0m;
        foreach (var quantity in new[] { 3, 3, 4 })
        {
            vatBack += sales.Refund(sale.Id, [new RefundRequestLine(line.Id, quantity)], PaymentMethod.Card, true, "matti", "Palautus")
                .Lines.Sum(refunded => refunded.VatAmount ?? 0m);
        }

        Assert.Equal(line.VatAmount, vatBack);
        Assert.False(Assert.Single(sales.GetSales()).CanBeRefunded);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void More_cannot_come_back_than_was_sold(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var sale = SaveCoffeeAndBuns(sales);
        var buns = BunLine(sales, sale.Id);

        sales.Refund(sale.Id, [new RefundRequestLine(buns.Id, 1)], PaymentMethod.Card, true, "matti", "Yksi");

        var error = Assert.Throws<InvalidOperationException>(() =>
            sales.Refund(sale.Id, [new RefundRequestLine(buns.Id, 2)], PaymentMethod.Card, true, "matti", "Kaksi"));
        Assert.Contains("enää 1 kpl", error.Message);
        Assert.Single(Assert.Single(sales.GetSales()).Refunds);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_line_from_another_sale_is_refused(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var first = SaveCoffeeAndBuns(sales);
        var second = SaveCoffeeAndBuns(sales);
        var otherLine = BunLine(sales, second.Id);

        Assert.Throws<InvalidOperationException>(() =>
            sales.Refund(first.Id, [new RefundRequestLine(otherLine.Id, 1)], PaymentMethod.Card, true, "matti", "Väärä"));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_reason_and_something_to_return_are_required(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var sale = SaveCoffeeAndBuns(sales);
        var buns = BunLine(sales, sale.Id);

        Assert.Throws<ArgumentException>(() =>
            sales.Refund(sale.Id, [new RefundRequestLine(buns.Id, 1)], PaymentMethod.Card, true, "matti", "  "));
        Assert.Throws<ArgumentException>(() =>
            sales.Refund(sale.Id, [new RefundRequestLine(buns.Id, 0)], PaymentMethod.Card, true, "matti", "Syy"));
        Assert.Empty(Assert.Single(sales.GetSales()).Refunds);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_cash_refund_is_rounded_like_a_cash_sale(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var sale = SaveCoffeeAndBuns(sales);
        var buns = BunLine(sales, sale.Id);

        var rounded = sales.Refund(sale.Id, [new RefundRequestLine(buns.Id, 1)], PaymentMethod.Cash, true, "matti", "A");
        var exact = sales.Refund(sale.Id, [new RefundRequestLine(buns.Id, 1)], PaymentMethod.Cash, false, "matti", "B");

        Assert.Equal((2.35m, 0m), (rounded.Total, rounded.Rounding));
        var coffee = Assert.Single(Assert.Single(sales.GetSales()).Items, item => item.Name == "Kahvi");
        var coffeeBack = sales.Refund(sale.Id, [new RefundRequestLine(coffee.Id, 1)], PaymentMethod.Cash, true, "matti", "C");
        Assert.Equal((4.50m, 0m), (coffeeBack.Total, coffeeBack.Rounding));
        Assert.Equal(2.35m, exact.Total);

        var odd = sales.Save(new Sale(DateTimeOffset.Now, 4.98m, PaymentMethod.Cash,
            [SaleItem.Taxed(5, "Leipä", "Leivät", 4.98m, 1, 1350)])
        { CashReceived = 5m, Rounding = 0m });
        var oddLine = Assert.Single(sales.GetSales(), s => s.Id == odd.Id).Items[0];
        var oddBack = sales.Refund(odd.Id, [new RefundRequestLine(oddLine.Id, 1)], PaymentMethod.Cash, true, "matti", "D");
        Assert.Equal((5.00m, 0.02m), (oddBack.Total, oddBack.Rounding));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Voids_and_refunds_exclude_each_other(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);

        var voided = SaveCoffeeAndBuns(sales);
        var voidedLine = BunLine(sales, voided.Id);
        sales.VoidSale(voided.Id, "esimies", "Väärä myynti");
        Assert.Throws<InvalidOperationException>(() =>
            sales.Refund(voided.Id, [new RefundRequestLine(voidedLine.Id, 1)], PaymentMethod.Card, true, "matti", "Ei"));

        var refunded = SaveCoffeeAndBuns(sales);
        sales.Refund(refunded.Id, [new RefundRequestLine(BunLine(sales, refunded.Id).Id, 1)], PaymentMethod.Card, true, "matti", "Rikki");
        Assert.Throws<InvalidOperationException>(() => sales.VoidSale(refunded.Id, "esimies", "Myöhässä"));

        var read = Assert.Single(sales.GetSales(), sale => sale.Id == refunded.Id);
        Assert.False(read.CanBeVoided);
        Assert.False(read.IsVoided);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_refund_is_audited_in_its_own_transaction(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var sale = SaveCoffeeAndBuns(sales);
        test.Database.Audit.Actor = "matti";

        sales.Refund(sale.Id, [new RefundRequestLine(BunLine(sales, sale.Id).Id, 2)], PaymentMethod.Cash, true, "matti", "Kuivia");

        var entry = Assert.Single(new AuditLog(test.Database).GetEntries());
        Assert.Equal((AuditActions.RefundCreated, "matti"), (entry.Action, entry.Actor));
        Assert.Equal($"Myynti #{sale.Id}: 2 × Pulla, Käteinen 4,70 €. Syy: Kuivia", entry.Details);
    }

    /// <summary>
    /// A refund counts on the day it was made: the day of the sale keeps its
    /// figures, and the day of the refund goes down by it, in the total, the
    /// products, the categories and the VAT alike.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Reports_take_refunds_off_the_day_they_were_made(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);
        var soldAt = new DateTimeOffset(2026, 3, 2, 12, 0, 0, TimeSpan.FromHours(2));
        var sale = sales.Save(new Sale(soldAt, 9.20m, PaymentMethod.Card,
        [
            SaleItem.Taxed(1, "Kahvi", "Kahvit", 4.50m, 1, 1350),
            SaleItem.Taxed(2, "Pulla", "Leivonnaiset", 2.35m, 2, 1350),
        ]));
        var buns = BunLine(sales, sale.Id);
        var refund = sales.Refund(sale.Id, [new RefundRequestLine(buns.Id, 2)], PaymentMethod.Card, true, "matti", "Kuivia");

        // Moved back to the next day, as if the customer came back then.
        test.Execute($"UPDATE Refunds SET \"DateTime\" = '2026-03-03T10:00:00.0000000+02:00' WHERE Id = {refund.Id};");

        var reports = new ReportsRepository(test.Database);
        var saleDay = reports.GetReport(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 2));
        Assert.Equal((9.20m, 0m, 0L), (saleDay.Total, saleDay.RefundTotal, saleDay.RefundCount));

        var refundDay = reports.GetReport(new DateOnly(2026, 3, 3), new DateOnly(2026, 3, 3));
        Assert.Equal((-4.70m, 4.70m, 1L, 0L, -2L), (refundDay.Total, refundDay.RefundTotal, refundDay.RefundCount, refundDay.SaleCount, refundDay.ItemCount));

        var both = reports.GetReport(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3));
        Assert.Equal((4.50m, 9.20m, 9.20m), (both.Total, both.GrossSales, both.AverageSale));
        Assert.Equal([("Kahvi", 1L, 4.50m), ("Pulla", 0L, 0m)], both.Products.Select(p => (p.Name, p.ItemCount, p.Total)));
        Assert.Equal(0m, both.Categories.Single(c => c.Name == "Leivonnaiset").Total);

        var vat = Assert.Single(both.VatLines);
        Assert.Equal(4.50m, vat.Gross);
        Assert.Equal(Vat.Included(4.50m, 1350), vat.Vat);
    }

    [Theory]
    [InlineData(UserRole.Admin, "joku", false, true)]
    [InlineData(UserRole.Manager, "joku", false, true)]
    [InlineData(UserRole.Seller, "matti", true, true)]
    [InlineData(UserRole.Seller, "MATTI", true, true)]
    [InlineData(UserRole.Seller, "matti", false, false)]
    [InlineData(UserRole.Seller, "joku", true, false)]
    [InlineData(UserRole.Seller, null, true, false)]
    public void A_seller_refunds_only_their_own_sales_of_the_day(UserRole role, string? soldBy, bool soldToday, bool allowed)
    {
        var user = new User(1, "matti", "matti@example.com", "", "", role);

        Assert.Equal(allowed, user.CanRefund(soldBy, soldToday));
    }

    /// <summary>Kahvi 4,50 € and two Pulla at 2,35 €, card, 9,20 € in all.</summary>
    private static Sale SaveCoffeeAndBuns(SalesRepository sales) =>
        sales.Save(new Sale(DateTimeOffset.Now, 9.20m, PaymentMethod.Card,
        [
            SaleItem.Taxed(1, "Kahvi", "Kahvit", 4.50m, 1, 1350),
            SaleItem.Taxed(2, "Pulla", "Leivonnaiset", 2.35m, 2, 1350),
        ]));

    private static SaleItem BunLine(SalesRepository sales, long saleId) =>
        Assert.Single(sales.GetSales(), sale => sale.Id == saleId).Items.Single(item => item.Name == "Pulla");
}
