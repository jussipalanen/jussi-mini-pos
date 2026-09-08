using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Product management: the catalogue as a paged table, with search, category
/// filtering, and edit/delete per row. Unlike the till this shows hidden
/// products too — hiding one is done here, so they have to stay visible.
/// </summary>
public partial class ProductsView : UserControl, INotifyPropertyChanged
{
    private readonly CatalogRepository _catalog;
    private readonly ImageStore _images;

    private IReadOnlyList<Product> _allProducts = [];
    private IReadOnlyList<Product> _matches = [];
    private IReadOnlyList<Category> _allCategories = [];

    private string _searchText = string.Empty;
    private Category _selectedCategory = Category.All;

    public ProductsView(CatalogRepository catalog, ImageStore images)
    {
        InitializeComponent();

        _catalog = catalog;
        _images = images;

        Pager.Changed += ShowPage;

        Reload();

        DataContext = this;
    }

    /// <summary>Raised when the user wants to leave the products view.</summary>
    public event Action? Back;

    /// <summary>Raised when the user opens category management.</summary>
    public event Action? ManageCategories;

    /// <summary>Filter chips: "Kaikki" plus every category, hidden ones included.</summary>
    public ObservableCollection<Category> Categories { get; } = [];

    /// <summary>The rows currently on screen.</summary>
    public ObservableCollection<ProductRow> PageProducts { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
            ApplyFilters();
        }
    }

    public Category SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            // The chip list drops its selection while the source changes; keep
            // the previous filter rather than falling back to nothing.
            if (value is null || _selectedCategory == value)
            {
                return;
            }

            _selectedCategory = value;
            OnPropertyChanged();
            ApplyFilters();
        }
    }

    /// <summary>Paging state, shared with the sales view.</summary>
    public Pager Pager { get; } = new("tuote", "tuotetta");

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// A product plus its resolved thumbnail. Thumbnail is null when there is
    /// no feature image, or the file behind one is gone — the row shows a
    /// placeholder icon in that case.
    /// </summary>
    public sealed record ProductRow(Product Product, ImageSource? Thumbnail);

    /// <summary>Re-reads the catalogue, keeping the current filters and page.</summary>
    public void Reload()
    {
        // publicOnly: false — this is where products and categories get hidden
        // and unhidden, so hidden rows have to be listed.
        _allCategories = _catalog.GetCategories(publicOnly: false);

        var previous = _selectedCategory;
        Categories.Clear();
        Categories.Add(Category.All);
        foreach (var category in _allCategories)
        {
            Categories.Add(category);
        }

        // Keep the chip selection, unless that category has just been deleted.
        _selectedCategory = Categories.FirstOrDefault(c => c.Id == previous.Id) ?? Category.All;
        OnPropertyChanged(nameof(SelectedCategory));

        _allProducts = _catalog.GetProducts(publicOnly: false);
        ApplyFilters(keepPage: true);
    }

    private void ApplyFilters(bool keepPage = false)
    {
        var search = _searchText.Trim();

        _matches = _allProducts
            .Where(p => p.IsInCategory(_selectedCategory))
            .Where(p => search.Length == 0
                || p.Id.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A narrower filter can leave the current page past the end; the pager
        // clamps for that.
        Pager.SetCount(_matches.Count, keepPage);
    }

    private void ShowPage()
    {
        PageProducts.Clear();
        foreach (var product in _matches.Skip(Pager.Skip).Take(Pager.Take))
        {
            PageProducts.Add(new ProductRow(
                product,
                Thumbnails.Load(_images, product.FeatureImage, decodeWidth: 96)));
        }
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => Pager.Previous();

    private void NextPage_Click(object sender, RoutedEventArgs e) => Pager.Next();

    private void PageNumber_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Pager.PageButton { IsEllipsis: false } button)
        {
            Pager.GoTo(button.Target);
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (Edit(product: null) is { } draft)
        {
            _catalog.InsertProduct(draft);
            Reload();
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ProductRow { Product: var product })
        {
            return;
        }

        if (Edit(product) is { } draft)
        {
            _catalog.UpdateProduct(product.Id, draft);
            Reload();
        }
    }

    /// <summary>Opens the editor and returns the draft, or null if cancelled.</summary>
    private CatalogRepository.ProductDraft? Edit(Product? product)
    {
        // Editing needs every category, including hidden ones, so a product
        // already in a hidden category does not silently lose it on save.
        var window = new ProductEditWindow(product, _allCategories, _images)
        {
            Owner = Window.GetWindow(this),
        };

        return window.ShowDialog() == true ? window.Draft : null;
    }

    private void Categories_Click(object sender, RoutedEventArgs e) => ManageCategories?.Invoke();

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ProductRow { Product: var product })
        {
            return;
        }

        new ProductDetailsWindow(product, _images, _catalog)
        {
            Owner = Window.GetWindow(this),
        }.ShowDialog();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ProductRow { Product: var product })
        {
            return;
        }

        var soldLines = _catalog.CountSoldLines(product.Id);
        var warning = soldLines > 0
            ? $"\n\nTuote on {soldLines} aiemmalla myyntirivillä. Ne säilyvät ennallaan."
            : string.Empty;

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Poistetaanko tuote \"{product.Name}\" ({product.Id})?{warning}",
            "Poista tuote",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _catalog.DeleteProduct(product.Id);

        // The rows cascade away, but the files on disk are ours to clean up.
        _images.Delete(product.Images.Append(product.FeatureImage));

        Reload();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Back?.Invoke();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
