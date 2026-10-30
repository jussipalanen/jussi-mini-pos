using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Tests;

public sealed class TillSessionTests
{
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Each_till_opens_once_until_it_is_closed(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var first = new TillSessionRepository(test.Database, "KASSA-1");
        var second = new TillSessionRepository(test.Database, "KASSA-2");

        Assert.Null(first.GetOpen());
        var session = first.Open("matti", 100m);
        Assert.Equal((100m, "matti", true), (session.OpeningFloat, session.OpenedBy, session.IsOpen));
        Assert.Equal(session.Id, first.GetOpen()?.Id);

        Assert.Throws<InvalidOperationException>(() => first.Open("liisa", 50m));
        Assert.Equal("KASSA-2", second.Open("liisa", 50m).Till);

        first.Close(session.Id, "matti", 100m, null);
        Assert.Null(first.GetOpen());
        Assert.NotNull(first.Open("matti", 80m));
    }

    /// <summary>
    /// A card sale, a rounded cash sale, a voided sale and a cash refund: the
    /// report has each payment method's takings and refunds, leaves the void
    /// out of the totals, and the drawer should hold the float plus cash in
    /// less cash out.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void The_end_of_day_report_adds_up_the_session(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var tills = new TillSessionRepository(test.Database, "KASSA-1");
        var sales = new SalesRepository(test.Database);
        var session = tills.Open("matti", 100m);

        var card = sales.Save(new Sale(DateTimeOffset.Now, 9.20m, PaymentMethod.Card,
        [
            SaleItem.Taxed(1, "Kahvi", "Kahvit", 4.50m, 1, 1350),
            SaleItem.Taxed(2, "Pulla", "Leivonnaiset", 2.35m, 2, 1350),
        ])
        { TillSessionId = session.Id });

        sales.Save(new Sale(DateTimeOffset.Now, 5.00m, PaymentMethod.Cash,
            [SaleItem.Taxed(3, "Leipä", "Leivät", 4.98m, 1, 1350)])
        { TillSessionId = session.Id, Rounding = 0.02m, CashReceived = 10m });

        var voided = sales.Save(new Sale(DateTimeOffset.Now, 3.00m, PaymentMethod.Cash,
            [SaleItem.Taxed(4, "Keksi", "Makeiset", 3.00m, 1, 1350)])
        { TillSessionId = session.Id, CashReceived = 3m });
        sales.VoidSale(voided.Id, "esimies", "Väärin näppäilty");

        // Outside the session: neither counts.
        sales.Save(new Sale(DateTimeOffset.Now, 50m, PaymentMethod.Cash, [SaleItem.Taxed(5, "Muu", "Muu", 50m, 1, 2550)]));

        var bun = Assert.Single(sales.GetSales(), sale => sale.Id == card.Id).Items.Single(item => item.Name == "Pulla");
        sales.Refund(card.Id, [new RefundRequestLine(bun.Id, 1)], PaymentMethod.Cash, true, "matti", "Kuiva", session.Id);

        var report = tills.GetReport(session.Id);

        Assert.Equal(
            [(PaymentMethod.Card, 1, 9.20m, 0, 0m), (PaymentMethod.Cash, 1, 5.00m, 1, 2.35m)],
            report.Payments.Select(p => (p.Method, p.SaleCount, p.Sales, p.RefundCount, p.Refunds)));
        Assert.Equal((2, 4, 1, 3.00m, 0.02m), (report.SaleCount, report.ItemCount, report.VoidCount, report.Voided, report.Rounding));
        Assert.Equal((14.20m, 2.35m, 11.85m), (report.GrossSales, report.RefundTotal, report.NetTotal));
        Assert.Equal(102.65m, report.ExpectedCash);

        var vat = Assert.Single(report.Vat);
        Assert.Equal(11.83m, vat.Gross);
        Assert.Equal(
            Vat.Included(4.50m, 1350) + Vat.Included(4.70m, 1350) + Vat.Included(4.98m, 1350) - Vat.Included(2.35m, 1350),
            vat.Vat);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Closing_records_the_count_and_the_difference_once(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var tills = new TillSessionRepository(test.Database, "KASSA-1");
        var session = tills.Open("matti", 100m);
        test.Database.Audit.Actor = "matti";
        new SalesRepository(test.Database).Save(new Sale(DateTimeOffset.Now, 5.00m, PaymentMethod.Cash,
            [SaleItem.Taxed(1, "Leipä", "Leivät", 5.00m, 1, 1350)])
        { TillSessionId = session.Id, CashReceived = 5m });

        var closed = tills.Close(session.Id, "liisa", 104.50m, "  Viisikymppinen puuttuu  ");

        Assert.Equal((105m, 104.50m, -0.50m, "liisa", "Viisikymppinen puuttuu"),
            (closed.Session.ExpectedCash, closed.Session.CountedCash, closed.Session.Difference, closed.Session.ClosedBy, closed.Session.Note));

        var stored = Assert.Single(tills.GetSessions());
        Assert.False(stored.IsOpen);
        Assert.Equal((105m, 104.50m), (stored.ExpectedCash, stored.CountedCash));
        Assert.Throws<InvalidOperationException>(() => tills.Close(session.Id, "liisa", 105m, null));

        var entries = new AuditLog(test.Database).GetEntries();
        Assert.Contains(entries, e => e.Action == AuditActions.TillOpened && e.Details == "Kassa KASSA-1, aloituskassa 100,00 €");
        Assert.Contains(entries, e => e.Action == AuditActions.TillClosed
            && e.Details.Contains("erotus −0,50 €") && e.Details.EndsWith("Viisikymppinen puuttuu"));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_negative_float_or_count_is_refused(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var tills = new TillSessionRepository(test.Database, "KASSA-1");

        Assert.Throws<ArgumentOutOfRangeException>(() => tills.Open("matti", -1m));
        var session = tills.Open("matti", 0m);
        Assert.Throws<ArgumentOutOfRangeException>(() => tills.Close(session.Id, "matti", -1m, null));
    }
}
