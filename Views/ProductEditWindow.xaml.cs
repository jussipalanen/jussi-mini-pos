using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using JussiMiniPos.Models;

namespace JussiMiniPos.Views;

/// <summary>
/// Modal editor for one product. It validates and exposes the entered values;
/// writing them back is the caller's job, so this window never touches the
/// database itself.
/// </summary>
public partial class ProductEditWindow : Window
{
    public ProductEditWindow(Product product, IReadOnlyList<Category> categories)
    {
        InitializeComponent();

        HeaderText = $"{product.Name} ({product.Id})";
        DataContext = this;

        TitleBox.Text = product.Name;
        DescriptionBox.Text = product.Description;
        PriceBox.Text = product.Price.ToString("N2", CultureInfo.CurrentCulture);
        SalePriceBox.Text = product.SalePrice?.ToString("N2", CultureInfo.CurrentCulture) ?? string.Empty;
        PublicBox.IsChecked = product.IsPublic;

        CategoryList.ItemsSource = categories;
        foreach (var category in categories.Where(c => product.Categories.Any(pc => pc.Id == c.Id)))
        {
            CategoryList.SelectedItems.Add(category);
        }

        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    public string HeaderText { get; }

    public string ProductTitle { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public decimal Price { get; private set; }

    public decimal? SalePrice { get; private set; }

    public bool IsPublic { get; private set; }

    /// <summary>
    /// Selected categories in the order they appear in the list, so the first
    /// stays the product's primary category.
    /// </summary>
    public IReadOnlyList<int> SelectedCategoryIds { get; private set; } = [];

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private bool Validate(out string error)
    {
        error = string.Empty;

        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            error = "Nimi ei voi olla tyhjä.";
            return false;
        }

        if (!TryParsePrice(PriceBox.Text, out var price) || price < 0)
        {
            error = "Hinta ei kelpaa. Anna luku, esimerkiksi 2,50.";
            return false;
        }

        decimal? salePrice = null;
        var saleText = SalePriceBox.Text.Trim();
        if (saleText.Length > 0)
        {
            if (!TryParsePrice(saleText, out var parsed) || parsed < 0)
            {
                error = "Tarjoushinta ei kelpaa. Jätä tyhjäksi, jos tuote ei ole tarjouksessa.";
                return false;
            }

            if (parsed >= price)
            {
                error = "Tarjoushinnan on oltava normaalihintaa pienempi.";
                return false;
            }

            salePrice = parsed;
        }

        var categoryIds = CategoryList.SelectedItems
            .Cast<Category>()
            .OrderBy(c => CategoryList.Items.IndexOf(c))
            .Select(c => c.Id)
            .ToList();

        if (categoryIds.Count == 0)
        {
            error = "Valitse vähintään yksi kategoria.";
            return false;
        }

        ProductTitle = title;
        Description = DescriptionBox.Text.Trim();
        Price = price;
        SalePrice = salePrice;
        IsPublic = PublicBox.IsChecked == true;
        SelectedCategoryIds = categoryIds;
        return true;
    }

    /// <summary>
    /// Accepts both "2,50" and "2.50": the app runs in fi-FI, but a numeric
    /// keypad types a full stop.
    /// </summary>
    private static bool TryParsePrice(string text, out decimal value)
    {
        text = text.Trim().Replace('.', ',');
        return decimal.TryParse(
            text,
            NumberStyles.Number,
            CultureInfo.GetCultureInfo("fi-FI"),
            out value);
    }
}
