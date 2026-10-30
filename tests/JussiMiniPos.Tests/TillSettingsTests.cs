using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Tests;

public sealed class TillSettingsTests
{
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_new_till_has_defaults_and_no_shop_details(string engine)
    {
        using var test = TestDatabase.Create(engine);

        var settings = TillSettings.Load(new OptionsRepository(test.Database));

        Assert.Equal(TillSettings.Default, settings);
        Assert.False(settings.Shop.IsComplete);
        Assert.Equal("Kiitos käynnistä!", settings.Shop.Footer);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Shop_details_and_the_printer_are_saved_and_audited(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var options = new OptionsRepository(test.Database);
        var before = TillSettings.Load(options);

        var after = before with
        {
            Shop = new ShopDetails("Jussin Kahvila", "0112038-9", "Esimerkkikatu 1\n00100 Helsinki", "", ""),
            PrinterName = "Microsoft Print to PDF",
            ReceiptWidthMm = 58,
            AutoPrint = true,
        };
        after.Save(options, before);

        Assert.Equal(after, TillSettings.Load(options));

        var entry = Assert.Single(new AuditLog(test.Database).GetEntries());
        Assert.Equal(AuditActions.SettingsChanged, entry.Action);
        Assert.Equal(
            "kaupan nimi – → Jussin Kahvila; Y-tunnus – → 0112038-9; osoite muutettu; kuitin alateksti muutettu; "
            + "kuittitulostin – → Microsoft Print to PDF; kuitin leveys (mm) 80 → 58; kuitti tulostetaan automaattisesti",
            entry.Details);
    }

    /// <summary>A footer cleared on purpose stays cleared rather than returning to the default.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_cleared_footer_stays_cleared(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var options = new OptionsRepository(test.Database);
        var before = TillSettings.Load(options);

        (before with { Shop = before.Shop with { Footer = string.Empty } }).Save(options, before);

        Assert.Equal(string.Empty, TillSettings.Load(options).Shop.Footer);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void An_unknown_paper_width_falls_back_to_80_mm(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var options = new OptionsRepository(test.Database);
        options.Set(TillSettings.WidthOption, "112");

        Assert.Equal(80, TillSettings.Load(options).ReceiptWidthMm);
    }
}
