using System.Globalization;
using JussiMiniPos.Models;

namespace JussiMiniPos.Core.Tests;

// This project references Core alone: these rules must run without WPF or a database.
public sealed class DomainTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("19.99", 1999)]
    [InlineData("0.105", 11)]
    [InlineData("-0.105", -11)]
    [InlineData("92233720368547758.07", long.MaxValue)]
    [InlineData("-92233720368547758.08", long.MinValue)]
    public void Money_rounds_to_cents_without_losing_integer_precision(string amount, long expected)
    {
        Assert.Equal(expected, Money.ToCents(decimal.Parse(amount, CultureInfo.InvariantCulture)));
        Assert.Equal(expected, Money.ToCents(Money.FromCents(expected)));
    }

    [Fact]
    public void Money_outside_the_cent_range_is_rejected()
    {
        Assert.Throws<OverflowException>(() => Money.ToCents(92233720368547758.08m));
        Assert.Throws<OverflowException>(() => Money.ToCents(-92233720368547758.09m));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Product_visibility_depends_on_the_product_and_not_its_categories(bool productPublic, bool categoryPublic)
    {
        var product = Product() with { IsPublic = productPublic, Categories = [new Category(1, "Kahvit", null, categoryPublic)] };

        Assert.Equal(productPublic, ProductVisibility.IsVisible(product));
    }

    [Theory]
    [InlineData(UserRole.Seller, false, false)]
    [InlineData(UserRole.Manager, true, false)]
    [InlineData(UserRole.Admin, true, true)]
    public void Role_permissions_are_available_without_the_desktop(UserRole role, bool manageSales, bool admin)
    {
        var user = new User(1, "matti", "", "", "", role);

        Assert.True(user.CanUseCheckout);
        Assert.Equal(manageSales, user.CanManageCatalogue);
        Assert.Equal(manageSales, user.CanOpenSales);
        Assert.Equal(manageSales, user.CanOpenReports);
        Assert.Equal(manageSales, user.CanVoidSales);
        Assert.Equal(admin, user.CanOpenAdmin);
        Assert.Equal(admin, user.CanManageUsers);
    }

    [Fact]
    public void A_cart_uses_the_offer_price_and_snapshots_VAT_in_core()
    {
        var product = Product() with { Price = 4m, SalePrice = 2.50m };
        var sale = Sale.FromCart([new CartLine(product, 2)], PaymentMethod.Card);
        var item = Assert.Single(sale.Items);

        Assert.Equal(5m, sale.Total);
        Assert.Equal(1350, item.VatBasisPoints);
        Assert.Equal(0.59m, item.VatAmount);
        Assert.Equal(4.41m, Assert.Single(sale.VatBreakdown).Net);
        Assert.Equal(0.59m, sale.VatTotal);
    }

    [Fact]
    public void Legacy_sale_VAT_stays_unknown_in_core()
    {
        var sale = new Sale(DateTimeOffset.Now, 1m, PaymentMethod.Card, [new SaleItem(1, "Kahvi", "", 1m, 1)]);

        Assert.Null(sale.VatTotal);
        var line = Assert.Single(sale.VatBreakdown);
        Assert.Null(line.BasisPoints);
        Assert.Null(line.Vat);
        Assert.Null(line.Net);
    }

    private static Product Product() => new(1, "Kahvi", "", 1m, null, null, true, [])
    {
        Vat = new VatRate(1, "Alennettu", 1350),
    };
}
