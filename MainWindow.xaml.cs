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

    /// <summary>
    /// Kept alive across the payment flow so cancelling a payment returns to
    /// the cart the user built rather than an empty one.
    /// </summary>
    private CheckoutView? _checkoutView;

    public MainWindow()
    {
        InitializeComponent();

        _database.EnsureCreated();
        _salesRepository = new SalesRepository(_database);

        ShowStartView();
    }

    private void ShowStartView()
    {
        _checkoutView = null;

        var view = new StartView();
        view.Navigate += OnNavigate;
        ViewHost.Content = view;
    }

    private void ShowCheckoutView()
    {
        if (_checkoutView is null)
        {
            _checkoutView = new CheckoutView();
            _checkoutView.Back += ShowStartView;
            _checkoutView.PayRequested += ShowPaymentView;
        }

        ViewHost.Content = _checkoutView;
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

        // TODO: Products, Sales and Reports still need their own views.
        MessageBox.Show(
            this,
            $"{AppViewNames.Finnish(destination)} ei ole vielä käytettävissä.",
            "JussiMiniPos",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
