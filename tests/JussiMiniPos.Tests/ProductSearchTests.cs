using JussiMiniPos.Services;
using static JussiMiniPos.Services.CatalogRepository;

namespace JussiMiniPos.Tests;

/// <summary>
/// The keyword retrieval behind the AI assistant: which public products a
/// question could be about, and in what order.
/// </summary>
public sealed class ProductSearchTests
{
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_title_hit_outranks_a_category_hit_which_outranks_a_description_hit(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var coffees = catalogue.InsertCategory("Kahvit", null, isPublic: true);
        var pastries = catalogue.InsertCategory("Leivonnaiset", null, isPublic: true);

        var described = Insert(catalogue, "Pulla", 2.20m, pastries, description: "Hyvää kahvin kanssa");
        var categorised = Insert(catalogue, "Espresso", 3.50m, coffees);
        var titled = Insert(catalogue, "Kahvi 500 g", 4.50m, pastries);

        var ids = new ProductSearch(test.Database).FindProductIds("Onko kahvia?");

        Assert.Equal([titled, categorised, described], ids.Take(3));
    }

    /// <summary>
    /// SQLite's own lower() and LIKE fold ASCII only; the search has to match
    /// "Äyriäiset" from "äyriäisiä" on both engines.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Finnish_letters_match_regardless_of_case(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var soups = catalogue.InsertCategory("Keitot", null, isPublic: true);
        Insert(catalogue, "Kahvi 500 g", 4.50m, soups);
        var shellfish = Insert(catalogue, "ÄYRIÄISKEITTO", 9.90m, soups);

        var ids = new ProductSearch(test.Database).FindProductIds("äyriäisiä");

        Assert.Equal(shellfish, ids[0]);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Hidden_products_are_never_candidates(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var drinks = catalogue.InsertCategory("Juomat", null, isPublic: true);
        var visible = Insert(catalogue, "Kahvi", 4.50m, drinks);
        var hidden = Insert(catalogue, "Kahvi salainen", 1.00m, drinks, isPublic: false);

        var ids = new ProductSearch(test.Database).FindProductIds("kahvia");

        Assert.Contains(visible, ids);
        Assert.DoesNotContain(hidden, ids);
    }

    /// <summary>The ceiling applies to what the till charges: the offer price when it undercuts.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_price_ceiling_applies_to_the_offer_price(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var drinks = catalogue.InsertCategory("Juomat", null, isPublic: true);
        var onOffer = Insert(catalogue, "Kahvi tarjous", 4.50m, drinks, salePrice: 3.90m);
        var tooDear = Insert(catalogue, "Kahvi luomu", 6.00m, drinks);
        var cheap = Insert(catalogue, "Vesi", 1.00m, drinks);

        var ids = new ProductSearch(test.Database).FindProductIds("kahvia alle 4 euroa");

        Assert.Equal(onOffer, ids[0]);
        Assert.Contains(cheap, ids);
        Assert.DoesNotContain(tooDear, ids);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Asking_for_something_cheap_puts_the_cheapest_match_first(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var drinks = catalogue.InsertCategory("Juomat", null, isPublic: true);
        var dear = Insert(catalogue, "Kahvi luomu", 6.00m, drinks);
        var budget = Insert(catalogue, "Kahvi perus", 3.00m, drinks);
        var unrelated = Insert(catalogue, "Vesi", 0.50m, drinks);

        var ids = new ProductSearch(test.Database).FindProductIds("halpaa kahvia");

        // Cheapest keyword match first; the unrelated bargain only tops the list up.
        Assert.Equal([budget, dear, unrelated], ids.Take(3));
    }

    /// <summary>
    /// Few keyword hits are topped up with other products. Both passes are
    /// capped on their own, so the top-up must stop at the cap rather than
    /// adding a full page of its own.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_topped_up_list_never_exceeds_the_cap(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var catalogue = new CatalogRepository(test.Database);
        var drinks = catalogue.InsertCategory("Juomat", null, isPublic: true);
        for (var i = 0; i < ProductSearch.MaxCandidates + 10; i++)
        {
            Insert(catalogue, i < 5 ? $"Kahvi {i}" : $"Tee {i}", 1.00m + i, drinks);
        }

        var ids = new ProductSearch(test.Database).FindProductIds("kahvia");

        Assert.Equal(ProductSearch.MaxCandidates, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void A_question_is_reduced_to_stems_without_filler_words()
    {
        var terms = ProductSearch.Parse("Mitä sopii kahvin kanssa? 0,5 l");

        Assert.Equal(["kahv"], terms.Stems);
        Assert.Null(terms.MaxPrice);
        Assert.False(terms.PreferCheap);
    }

    [Theory]
    [InlineData("alle 10 euron", 10)]
    [InlineData("enintään 3,50 €", 3.50)]
    [InlineData("max 5", 5)]
    [InlineData("korkeintaan 2.25 euroa", 2.25)]
    public void A_price_ceiling_is_read_from_Finnish(string question, double expected)
    {
        Assert.Equal((decimal)expected, ProductSearch.Parse(question).MaxPrice);
    }

    [Theory]
    [InlineData("halpaa kahvia")]
    [InlineData("jotain halvempaa")]
    [InlineData("edullinen lounas")]
    public void Asking_for_cheap_is_recognised_in_its_inflected_forms(string question)
    {
        Assert.True(ProductSearch.Parse(question).PreferCheap);
    }

    private static int Insert(
        CatalogRepository catalogue,
        string title,
        decimal price,
        int categoryId,
        string description = "",
        decimal? salePrice = null,
        bool isPublic = true) =>
        catalogue.InsertProduct(new ProductDraft(
            title, description, price, salePrice, catalogue.GetVatRates()[0].Id, isPublic, FeatureImage: null, [categoryId], []));
}
