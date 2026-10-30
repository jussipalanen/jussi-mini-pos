using JussiMiniPos.Models;

namespace JussiMiniPos.Core.Tests;

public sealed class ReceiptTests
{
    private static readonly ShopDetails Shop = new(
        "Jussin Kahvila", "0112038-9", "Esimerkkikatu 1\n00100 Helsinki", "040 123 4567", "Kiitos käynnistä!\nTervetuloa uudelleen");

    [Theory]
    [InlineData("0112038-9")]
    [InlineData("1572860-0")]
    [InlineData("0737546-2")]
    [InlineData("")]
    public void Valid_business_ids_and_an_empty_one_are_accepted(string id) =>
        Assert.Null(BusinessId.Validate(id));

    [Theory]
    [InlineData("0112038-8", "tarkistusnumero")]
    [InlineData("1000008-1", "tarkistusnumero")] // remainder 1: no check digit is valid
    [InlineData("1000008-0", "tarkistusnumero")]
    [InlineData("0112038", "muoto")]
    [InlineData("01120389", "muoto")]
    [InlineData("011203A-9", "muoto")]
    public void Malformed_business_ids_are_refused(string id, string reason) =>
        Assert.Contains(reason, BusinessId.Validate(id));

    [Fact]
    public void A_receipt_names_the_seller_and_lists_items_total_vat_and_payment()
    {
        var lines = Receipts.ForSale(CashSale(), Shop, isCopy: false);
        var text = Text(lines);

        Assert.Equal((ReceiptLineKind.Heading, "Jussin Kahvila"), (lines[0].Kind, lines[0].Left));
        Assert.Contains("Esimerkkikatu 1", text);
        Assert.Contains("00100 Helsinki", text);
        Assert.Contains("Y-tunnus 0112038-9", text);
        Assert.Contains("Puh. 040 123 4567", text);
        Assert.Contains("Kuitti #7 | 04.05.2026 12.30", text);
        Assert.Contains("Myyjä: matti", text);
        Assert.Contains("Kahvi | 4,50", text);
        Assert.Contains("Pulla | 4,96", text);
        Assert.Contains("2 × 2,48", text);
        Assert.Contains("Välisumma | 9,46", text);
        Assert.Contains("Pyöristys | -0,01", text);
        Assert.Contains("YHTEENSÄ | 9,45 €", text);
        Assert.Contains("Käteinen | 20,00", text);
        Assert.Contains("Takaisin | 10,55", text);
        Assert.Contains("13,5 % | 8,33 | 1,13 | 9,46", text);
        Assert.Contains("Kiitos käynnistä!", text);
        Assert.Contains("Tervetuloa uudelleen", text);
        Assert.DoesNotContain("KOPIO", text);
    }

    [Fact]
    public void A_reprint_says_it_is_a_copy_and_a_card_sale_names_its_method()
    {
        var sale = new Sale(SoldAt, 4.50m, PaymentMethod.Card, [SaleItem.Taxed(1, "Kahvi", "Kahvit", 4.50m, 1, 2550)]) { Id = 3 };
        var text = Text(Receipts.ForSale(sale, Shop, isCopy: true));

        Assert.Contains("KUITIN KOPIO", text);
        Assert.Contains("Maksukortti | 4,50", text);
        Assert.DoesNotContain("Välisumma", text);
        Assert.DoesNotContain("Takaisin", text);
        Assert.DoesNotContain("× ", text);
    }

    [Fact]
    public void A_refund_receipt_shows_what_came_back_as_negative_amounts()
    {
        var sale = CashSale();
        var refund = new Refund(12, sale.Id, SoldAt.AddHours(2), "liisa", "Kuiva", PaymentMethod.Cash, 2.50m, 0.02m,
            [new RefundLine(2, "Pulla", 2.48m, 1, 1350, 0.29m)]);

        var text = Text(Receipts.ForRefund(sale, refund, Shop));

        Assert.Contains("PALAUTUSKUITTI", text);
        Assert.Contains("Palautus #12 | 04.05.2026 14.30", text);
        Assert.Contains("Alkuperäinen kuitti #7 | 04.05.2026 12.30", text);
        Assert.Contains("Palauttaja: liisa", text);
        Assert.Contains("Syy: Kuiva", text);
        Assert.Contains("Pulla | -2,48", text);
        Assert.Contains("Pyöristys | -0,02", text);
        Assert.Contains("PALAUTETTU | -2,50 €", text);
        Assert.Contains("Käteinen | -2,50", text);
        Assert.Contains("13,5 % | -2,19 | -0,29 | -2,48", text);
    }

    [Fact]
    public void Without_shop_details_the_receipt_still_prints()
    {
        var lines = Receipts.ForSale(CashSale(), ShopDetails.Empty, isCopy: false);

        Assert.Equal("Kuitti", lines[0].Left);
        Assert.DoesNotContain("Y-tunnus", Text(lines));
        Assert.False(ShopDetails.Empty.IsComplete);
        Assert.True(Shop.IsComplete);
    }

    private static readonly DateTimeOffset SoldAt = new(2026, 5, 4, 12, 30, 0, TimeSpan.FromHours(3));

    /// <summary>Kahvi 4,50 and two Pulla at 2,48, at 13,5 %, paid with 20 € in cash and rounded to 9,45.</summary>
    private static Sale CashSale() =>
        new Sale(SoldAt, 9.45m, PaymentMethod.Cash,
        [
            SaleItem.Taxed(1, "Kahvi", "Kahvit", 4.50m, 1, 1350) with { Id = 1 },
            SaleItem.Taxed(2, "Pulla", "Leivonnaiset", 2.48m, 2, 1350) with { Id = 2 },
        ])
        { Id = 7, SoldBy = "matti", Rounding = -0.01m, CashReceived = 20m };

    private static string Text(IReadOnlyList<ReceiptLine> lines) =>
        string.Join("\n", lines.Select(line => string.Join(" | ", line.Cells)));
}
