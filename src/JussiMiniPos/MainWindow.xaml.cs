using System.Windows;
using JussiMiniPos.Models;
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
    private readonly OptionsRepository _options;
    private readonly UserRepository _users;
    private readonly ShoppingAssistant _assistant;

    /// <summary>
    /// Who is signed in, for as long as the application runs. Nothing is
    /// persisted: restarting signs everybody out, which for a till on a shared
    /// counter is the safer default.
    /// </summary>
    private User? _currentUser;

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
        _options = new OptionsRepository(_database);
        _users = new UserRepository(_database);
        _assistant = new ShoppingAssistant(_database, _catalogRepository, _options);

        // Keyed off the table being empty rather than the database being new,
        // unlike the catalogue: Users is new to databases that already exist,
        // and an empty one would mean nobody can ever open Admin again.
        //
        // The first-run password is trivial and known to anyone who has seen
        // the source, so the point of this dialog is not to reveal it but to
        // say it needs replacing. Shown before the window appears, and
        // deliberately blocking, so it cannot be missed.
        if (_users.EnsureDefaultAdmin() is { } password)
        {
            MessageBox.Show(
                $"Ylläpitäjän tunnus luotiin ensimmäistä käyttöä varten:\n\n" +
                $"Käyttäjätunnus:  {UserRepository.DefaultUsername}\n" +
                $"Sähköposti:      {UserRepository.DefaultEmail}\n" +
                $"Salasana:        {password}\n\n" +
                $"Kirjaudu sisään aloitusnäytöltä ja vaihda salasana heti kohdassa " +
                $"Oma profiili. Tämä salasana on tiedossa kaikilla, joilla on " +
                $"sovelluksen lähdekoodi.",
                "JussiMiniPos",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        ShowStartView();
    }

    private void ShowStartView()
    {
        _checkoutView = null;
        _productsView = null;

        var view = new StartView(_currentUser);
        view.Navigate += OnNavigate;
        view.LoginRequested += OnLoginRequested;
        view.LogoutRequested += OnLogoutRequested;
        ViewHost.Content = view;
    }

    /// <summary>
    /// Shows the login prompt and keeps whoever signs in. Returns true when
    /// the session ends up with a signed-in user, so callers that need one can
    /// prompt and continue in a single step.
    /// </summary>
    private bool SignIn(string? purpose = null)
    {
        var window = new LoginWindow(_users, purpose) { Owner = this };

        if (window.ShowDialog() != true || window.SignedInUser is null)
        {
            return false;
        }

        _currentUser = window.SignedInUser;
        return true;
    }

    private void OnLoginRequested()
    {
        if (SignIn())
        {
            // Redrawn so the bottom row shows who signed in.
            ShowStartView();
        }
    }

    private void OnLogoutRequested()
    {
        _currentUser = null;
        ShowStartView();
    }

    private void ShowCheckoutView()
    {
        if (_checkoutView is null)
        {
            _checkoutView = new CheckoutView(_catalogRepository, _imageStore, _assistant);
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

    private void ShowReportsView()
    {
        if (_currentUser is null
            && !SignIn("Raportit ovat ylläpitäjille ja esimiehille. Kirjaudu jatkaaksesi."))
        {
            return;
        }

        if (_currentUser is not { CanOpenReports: true })
        {
            MessageBox.Show(this, "Raportit ovat vain ylläpitäjille ja esimiehille.",
                "JussiMiniPos", MessageBoxButton.OK, MessageBoxImage.Information);
            ShowStartView();
            return;
        }

        var view = new ReportsView(new ReportsRepository(_database));
        view.Back += ShowStartView;
        ViewHost.Content = view;
    }

    /// <summary>
    /// Application settings. Leaving here goes through the start view, which
    /// drops the cached checkout view — so switching the AI assistant off
    /// takes effect the next time Kassa is opened, without a restart.
    /// </summary>
    private void ShowAdminView()
    {
        // Signing in is offered rather than demanded up front: the prompt says
        // why it appeared, so Admin does not just refuse and leave the user to
        // work out that there is a login somewhere.
        if (_currentUser is null
            && !SignIn("Admin-osio on vain ylläpitäjille. Kirjaudu jatkaaksesi."))
        {
            return;
        }

        if (_currentUser is not { CanOpenAdmin: true })
        {
            MessageBox.Show(
                this,
                $"Admin-osio on vain ylläpitäjille.\n\n" +
                $"Olet kirjautunut käyttäjänä {_currentUser!.Display}.",
                "JussiMiniPos",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // Redrawn so a sign-in that happened just now is on screen even
            // though it did not open Admin.
            ShowStartView();
            return;
        }

        var view = new AdminView(_options, _database.DataDirectory, _currentUser);
        view.Back += ShowStartView;
        ViewHost.Content = view;
    }

    /// <summary>
    /// The signed-in user's own details. Reachable only while signed in — the
    /// start screen only offers it then, and there is nothing to edit
    /// otherwise, so this prompts rather than refuses.
    /// </summary>
    private void ShowProfileView()
    {
        if (_currentUser is null && !SignIn("Kirjaudu sisään nähdäksesi omat tietosi."))
        {
            return;
        }

        var view = new ProfileView(_users, _currentUser!);
        view.Back += ShowStartView;

        // A renamed user has to reach the session too, or the start screen
        // keeps greeting them by the old name until they sign in again.
        view.Updated += user => _currentUser = user;

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

        if (destination == AppView.Admin)
        {
            ShowAdminView();
            return;
        }

        if (destination == AppView.Profile)
        {
            ShowProfileView();
            return;
        }

        if (destination == AppView.Reports)
        {
            ShowReportsView();
            return;
        }

        MessageBox.Show(
            this,
            $"{AppViewNames.Finnish(destination)} ei ole vielä käytettävissä.",
            "JussiMiniPos",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
