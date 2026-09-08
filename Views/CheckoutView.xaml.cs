using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Checkout view: product catalogue with search and category filtering on the
/// left, the shopping cart and its total on the right.
/// </summary>
public partial class CheckoutView : UserControl, INotifyPropertyChanged
{
    private readonly ListCollectionView _productsView;

    private string _searchText = string.Empty;
    private Category _selectedCategory = Category.All;
    private ProductRow? _selectedProduct;
    private decimal _total;
    private int _itemCount;

    public CheckoutView(CatalogRepository catalog, ImageStore images)
    {
        InitializeComponent();

        // Both come from the database. Only public rows: IsPublic = 0 keeps a
        // product or category out of the till without deleting it.
        var rows = catalog.GetProducts()
            .Select(p => new ProductRow(p, Thumbnails.Load(images, p.FeatureImage, decodeWidth: 96)))
            .ToList();

        _productsView = new ListCollectionView(rows)
        {
            Filter = MatchesFilters,
        };

        Categories = [Category.All, .. catalog.GetCategories()];

        Cart.CollectionChanged += Cart_CollectionChanged;

        DataContext = this;
    }

    /// <summary>Raised when the user wants to leave the checkout view.</summary>
    public event Action? Back;

    /// <summary>Raised when the user is ready to pay for the current cart.</summary>
    public event Action? PayRequested;

    public ICollectionView ProductsView => _productsView;

    public IReadOnlyList<Category> Categories { get; }

    public ObservableCollection<CartLine> Cart { get; } = [];

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
            RefreshProducts();
        }
    }

    public Category SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            // The chip list clears its selection while the source changes; keep
            // the previous filter rather than falling back to nothing.
            if (value is null || _selectedCategory == value)
            {
                return;
            }

            _selectedCategory = value;
            OnPropertyChanged();
            RefreshProducts();
        }
    }

    public ProductRow? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (_selectedProduct == value)
            {
                return;
            }

            _selectedProduct = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedProduct));
        }
    }

    public bool HasSelectedProduct => _selectedProduct is not null;

    /// <summary>
    /// A product plus its resolved thumbnail. Thumbnail is null when there is
    /// no feature image, or the file behind one is gone — the row shows a
    /// placeholder icon in that case.
    /// </summary>
    public sealed record ProductRow(Product Product, ImageSource? Thumbnail);

    /// <summary>How many products the current search and filter leave visible.</summary>
    public string VisibleProductText =>
        _productsView.Count == 1 ? "1 tuote" : $"{_productsView.Count} tuotetta";

    public bool HasNoSearchResults => _productsView.Count == 0;

    /// <summary>Sum of every cart line, in euros.</summary>
    public decimal Total
    {
        get => _total;
        private set
        {
            if (_total == value)
            {
                return;
            }

            _total = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Total number of individual items in the cart.</summary>
    public int ItemCount
    {
        get => _itemCount;
        private set
        {
            if (_itemCount == value)
            {
                return;
            }

            _itemCount = value;
            OnPropertyChanged();
        }
    }

    public bool HasItems => Cart.Count > 0;

    public bool IsCartEmpty => Cart.Count == 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// A product matches when it is in the selected category and the search
    /// text appears in either its id or its name.
    /// </summary>
    private bool MatchesFilters(object item)
    {
        if (item is not ProductRow { Product: var product })
        {
            return false;
        }

        // Matched by CategoryId. Seeding links a product to its subcategory's
        // parent as well, so picking "Juomat" still finds the hot drinks.
        if (!product.IsInCategory(_selectedCategory))
        {
            return false;
        }

        var search = _searchText.Trim();
        if (search.Length == 0)
        {
            return true;
        }

        return product.Id.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
            || product.Name.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshProducts()
    {
        _productsView.Refresh();
        OnPropertyChanged(nameof(VisibleProductText));
        OnPropertyChanged(nameof(HasNoSearchResults));
    }

    private void Cart_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var line in e.OldItems?.Cast<CartLine>() ?? [])
        {
            line.PropertyChanged -= CartLine_PropertyChanged;
        }

        foreach (var line in e.NewItems?.Cast<CartLine>() ?? [])
        {
            line.PropertyChanged += CartLine_PropertyChanged;
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsCartEmpty));
        RecalculateTotal();
    }

    private void CartLine_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RecalculateTotal();

    private void RecalculateTotal()
    {
        Total = Cart.Sum(line => line.LineTotal);
        ItemCount = Cart.Sum(line => line.Quantity);
    }

    /// <summary>Adds one unit of <paramref name="product"/>, merging with an existing line.</summary>
    private void AddToCart(Product product)
    {
        var existing = Cart.FirstOrDefault(line => line.Product.Id == product.Id);
        if (existing is null)
        {
            Cart.Add(new CartLine(product));
        }
        else
        {
            existing.Quantity++;
        }
    }

    private void AddToCart_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProduct is { Product: var product })
        {
            AddToCart(product);
        }
    }

    private void ProductList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedProduct is { Product: var product })
        {
            AddToCart(product);
        }
    }

    private void IncreaseQuantity_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CartLine line)
        {
            line.Quantity++;
        }
    }

    private void DecreaseQuantity_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CartLine line)
        {
            return;
        }

        if (line.Quantity > 1)
        {
            line.Quantity--;
        }
        else
        {
            Cart.Remove(line);
        }
    }

    private void RemoveLine_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CartLine line)
        {
            Cart.Remove(line);
        }
    }

    private void ClearCart_Click(object sender, RoutedEventArgs e)
    {
        foreach (var line in Cart)
        {
            line.PropertyChanged -= CartLine_PropertyChanged;
        }

        Cart.Clear();
    }

    private void Pay_Click(object sender, RoutedEventArgs e) => PayRequested?.Invoke();

    private void Back_Click(object sender, RoutedEventArgs e) => Back?.Invoke();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
