using System.Windows;
using JussiMiniPos.Services;
using JussiMiniPos.Views;

namespace JussiMiniPos;

/// <summary>
/// Application shell. Hosts one view at a time and handles navigation between
/// the start screen and the feature views.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Database _database = new();
    private readonly SalesRepository _salesRepository;
    private readonly CatalogRepository _catalogRepository;
    private readonly ImageStore _imageStore;

    /// <summary>
    /// Kept alive across the payment flow so cancelling a payment returns to
    /// the cart the user built rather than an empty one.
    /// </summary>
    private CheckoutView? _checkoutView;

    private ProductsView? _productsView;

    public MainWindow()
    {
        InitializeComponent();

        _database.EnsureCreated();

        // A brand new database is not much use to look at, so fill it with the
        // demo catalogue. Keyed off the file being new rather than the tables
        // being empty: otherwise "--clear", or deleting the last product by
        // hand, would be undone by the next restart.
        if (_database.IsNew)
        {
            CatalogSeeder.Seed(_database);
        }

        _salesRepository = new SalesRepository(_database);
        _catalogRepository = new CatalogRepository(_database);
        _imageStore = new ImageStore(_database);

        ShowStartView();
    }

    private void ShowStartView()
    {
        _checkoutView = null;
        _productsView = null;

        var view = new StartView();
        view.Navigate += OnNavigate;
        ViewHost.Content = view;
    }

    private void ShowCheckoutView()
    {
        if (_checkoutView is null)
        {
            _checkoutView = new CheckoutView(_catalogRepository, _imageStore);
            _checkoutView.Back += ShowStartView;
            _checkoutView.PayRequested += ShowPaymentView;
        }

        ViewHost.Content = _checkoutView;
    }

    private void ShowProductsView()
    {
        // Kept alive so returning from category management lands back on the
        // same page and filter, refreshed.
        if (_productsView is null)
        {
            _productsView = new ProductsView(_catalogRepository, _imageStore);
            _productsView.Back += ShowStartView;
            _productsView.ManageCategories += ShowCategoriesView;
        }
        else
        {
            _productsView.Reload();
        }

        ViewHost.Content = _productsView;
    }

    private void ShowSalesView()
    {
        var view = new SalesView(_salesRepository);
        view.Back += ShowStartView;
        ViewHost.Content = view;
    }

    private void ShowCategoriesView()
    {
        var view = new CategoriesView(_catalogRepository);
        view.Back += ShowProductsView;
        ViewHost.Content = view;
    }

    private void ShowPaymentView()
    {
        if (_checkoutView is null)
        {
            return;
        }

        var view = new PaymentView(_checkoutView.Cart, _salesRepository);
        view.Cancelled += ShowCheckoutView;
        view.Finished += StartNewSale;
        ViewHost.Content = view;
    }

    /// <summary>After a paid sale the next customer starts from an empty cart.</summary>
    private void StartNewSale()
    {
        _checkoutView = null;
        ShowCheckoutView();
    }

    private void OnNavigate(AppView destination)
    {
        if (destination == AppView.Checkout)
        {
            ShowCheckoutView();
            return;
        }

        if (destination == AppView.Products)
        {
            ShowProductsView();
            return;
        }

        if (destination == AppView.Sales)
        {
            ShowSalesView();
            return;
        }

        // TODO: Reports still needs its own view.
        MessageBox.Show(
            this,
            $"{AppViewNames.Finnish(destination)} ei ole vielä käytettävissä.",
            "JussiMiniPos",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
