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
    /// <summary>How many page buttons to show before falling back to ellipsis.</summary>
    private const int MaxPageButtons = 7;

    private readonly CatalogRepository _catalog;
    private readonly ImageStore _images;

    private IReadOnlyList<Product> _allProducts = [];
    private IReadOnlyList<Product> _matches = [];
    private IReadOnlyList<Category> _allCategories = [];

    private string _searchText = string.Empty;
    private Category _selectedCategory = Category.All;
    private int _pageSize = 10;
    private int _currentPage = 1;

    public ProductsView(CatalogRepository catalog, ImageStore images)
    {
        InitializeComponent();

        _catalog = catalog;
        _images = images;

        Reload();

        DataContext = this;
    }

    /// <summary>Raised when the user wants to leave the products view.</summary>
    public event Action? Back;

    /// <summary>Raised when the user opens category management.</summary>
    public event Action? ManageCategories;

    /// <summary>Filter chips: "Kaikki" plus every category, hidden ones included.</summary>
    public ObservableCollection<Category> Categories { get; } = [];

    public IReadOnlyList<int> PageSizes { get; } = [10, 25, 50, 100];

    /// <summary>The rows currently on screen.</summary>
    public ObservableCollection<ProductRow> PageProducts { get; } = [];

    /// <summary>Page buttons for the current result set.</summary>
    public ObservableCollection<PageButton> PageNumbers { get; } = [];

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

    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (value <= 0 || _pageSize == value)
            {
                return;
            }

            _pageSize = value;
            OnPropertyChanged();

            // Keep the first row of the current page in view rather than
            // jumping back to the start.
            var firstRow = ((_currentPage - 1) * _pageSize) + 1;
            _currentPage = Math.Max(1, ((firstRow - 1) / _pageSize) + 1);

            ShowPage(_currentPage);
        }
    }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(_matches.Count / (double)_pageSize));

    public bool HasPreviousPage => _currentPage > 1;

    public bool HasNextPage => _currentPage < TotalPages;

    public bool HasResults => _matches.Count > 0;

    public bool HasNoResults => _matches.Count == 0;

    /// <summary>"Näytetään 1–10 / 20 tuotetta".</summary>
    public string ResultText
    {
        get
        {
            if (_matches.Count == 0)
            {
                return "Ei tuloksia";
            }

            var first = ((_currentPage - 1) * _pageSize) + 1;
            var last = Math.Min(first + _pageSize - 1, _matches.Count);
            return $"Näytetään {first}–{last} / {_matches.Count} tuotetta";
        }
    }

    public string PageText => $"Sivu {_currentPage} / {TotalPages}";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// A product plus its resolved thumbnail. Thumbnail is null when there is
    /// no feature image, or the file behind one is gone — the row shows a
    /// placeholder icon in that case.
    /// </summary>
    public sealed record ProductRow(Product Product, ImageSource? Thumbnail);

    /// <summary>One page button. Ellipsis entries are not clickable.</summary>
    public sealed record PageButton(string Number, bool IsCurrent, int Target)
    {
        public bool IsEllipsis => Target == 0;

        public string AutomationName => IsEllipsis ? "…" : $"Sivu {Number}";
    }

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

        // A narrower filter can leave the current page past the end.
        var page = keepPage ? Math.Min(_currentPage, TotalPages) : 1;
        ShowPage(page);
    }

    private void ShowPage(int page)
    {
        _currentPage = Math.Clamp(page, 1, TotalPages);

        PageProducts.Clear();
        foreach (var product in _matches.Skip((_currentPage - 1) * _pageSize).Take(_pageSize))
        {
            PageProducts.Add(new ProductRow(
                product,
                Thumbnails.Load(_images, product.FeatureImage, decodeWidth: 96)));
        }

        BuildPageButtons();

        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(PageText));
    }

    /// <summary>
    /// Builds the page buttons. Beyond <see cref="MaxPageButtons"/> pages it
    /// shows the first and last with a window around the current page, so the
    /// bar does not grow without limit.
    /// </summary>
    private void BuildPageButtons()
    {
        PageNumbers.Clear();

        var total = TotalPages;
        var pages = new List<int>();

        if (total <= MaxPageButtons)
        {
            pages.AddRange(Enumerable.Range(1, total));
        }
        else
        {
            pages.Add(1);

            var from = Math.Max(2, _currentPage - 1);
            var to = Math.Min(total - 1, _currentPage + 1);

            // Keep the window the same width when it sits at either end.
            if (_currentPage <= 3)
            {
                to = 4;
            }
            else if (_currentPage >= total - 2)
            {
                from = total - 3;
            }

            pages.AddRange(Enumerable.Range(from, to - from + 1));
            pages.Add(total);
        }

        var previous = 0;
        foreach (var page in pages.Distinct())
        {
            if (previous != 0 && page - previous > 1)
            {
                PageNumbers.Add(new PageButton("…", false, 0));
            }

            PageNumbers.Add(new PageButton(page.ToString(), page == _currentPage, page));
            previous = page;
        }
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => ShowPage(_currentPage - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => ShowPage(_currentPage + 1);

    private void PageNumber_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PageButton { IsEllipsis: false } button)
        {
            ShowPage(button.Target);
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
