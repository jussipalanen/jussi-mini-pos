using System.Data.Common;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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

    /// <summary>When the keyboard, mouse or touch screen was last used, for the idle sign-out.</summary>
    private DateTime _lastInput = DateTime.UtcNow;

    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainWindow()
    {
        InitializeComponent();

        InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is KeyboardEventArgs or MouseEventArgs or TouchEventArgs)
            {
                _lastInput = DateTime.UtcNow;
            }
        };
        _idleTimer.Tick += (_, _) => SignOutIfIdle();
        _idleTimer.Start();

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
        _ = RunScheduledBackupAsync();
    }

    /// <summary>
    /// Takes the daily automatic backup when one is due, off the UI thread so
    /// the till is usable at once. A failure is shown, because a till that
    /// silently stopped backing up is worse than one that says so.
    /// </summary>
    private async Task RunScheduledBackupAsync()
    {
        try
        {
            var settings = BackupSettings.Load(_options);
            if (settings.Automatic)
            {
                var backups = new BackupService(_database);
                await Task.Run(() => backups.RunScheduled(settings.Folder, settings.Keep, DateTimeOffset.Now));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbException or InvalidOperationException)
        {
            MessageBox.Show(this,
                $"Automaattinen varmuuskopio epäonnistui:\n\n{ex.Message}\n\nTarkista kansio kohdassa Admin → Varmuuskopiot.",
                "JussiMiniPos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        _database.Audit.Actor = _currentUser.Username;
        return true;
    }

    /// <summary>The signed-in user, prompting for a sign-in first when nobody is.</summary>
    private User? RequireUser(string purpose) =>
        _currentUser is not null || SignIn(purpose) ? _currentUser : null;

    /// <summary>
    /// Whether the session may open a view: prompts for a sign-in when nobody
    /// is signed in, then checks the domain rule, explaining a refusal. The
    /// rule is a <see cref="User"/> property such as
    /// <see cref="User.CanManageCatalogue"/>, never which buttons are shown.
    /// </summary>
    private bool Authorize(Func<User, bool> allowed, string signInPurpose, string refusal)
    {
        if (RequireUser(signInPurpose) is not { } user)
        {
            return false;
        }

        if (allowed(user))
        {
            return true;
        }

        MessageBox.Show(
            this,
            $"{refusal}\n\nOlet kirjautunut käyttäjänä {user.Display}.",
            "JussiMiniPos",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        // Redrawn so a sign-in that happened just now is on screen even
        // though it did not open the view.
        ShowStartView();
        return false;
    }

    /// <summary>Ends the session: recorded, then back to the start screen signed out.</summary>
    private void SignOut(string reason)
    {
        if (_currentUser is { } user)
        {
            ViewErrors.Try(this, "Uloskirjautumista ei voitu kirjata.",
                () => _database.Audit.Record(AuditActions.SignOut, $"{user.Display}{reason}"));
        }

        _currentUser = null;
        _database.Audit.Actor = null;
        ShowStartView();
    }

    /// <summary>
    /// Signs out a session nobody has touched for <see cref="IdleSession.Timeout"/>,
    /// so a till left signed in on a shared counter does not stay usable by
    /// whoever walks up. Checked every half minute.
    /// </summary>
    private void SignOutIfIdle()
    {
        if (IdleSession.ShouldSignOut(
                _currentUser is not null,
                DateTime.UtcNow - _lastInput,
                paymentInProgress: ViewHost.Content is PaymentView,
                dialogOpen: OwnedWindows.Count > 0))
        {
            SignOut(" (ei toimintaa, automaattinen uloskirjautuminen)");
        }
    }

    private void OnLoginRequested()
    {
        if (SignIn())
        {
            // Redrawn so the bottom row shows who signed in.
            ShowStartView();
        }
    }

    private void OnLogoutRequested() => SignOut(string.Empty);

    private void ShowCheckoutView()
    {
        if (_checkoutView is null)
        {
            _checkoutView = new CheckoutView(_catalogRepository, _imageStore, _assistant);
            _checkoutView.Back += ShowStartView;
            _checkoutView.PayRequested += ShowPaymentView;
            _checkoutView.RefundRequested += ShowRefundPicker;
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
        var view = new SalesView(_salesRepository, _options, RequireUser);
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
        var view = new AdminView(_options, _database.DataDirectory, _currentUser);
        view.Back += ShowStartView;
        view.AuditLogRequested += () => new AuditLogWindow(_database.Audit) { Owner = this }.ShowDialog();
        view.UsersRequested += ShowUsersWindow;
        view.BackupsRequested += () => new BackupWindow(_database, _options) { Owner = this }.ShowDialog();
        ViewHost.Content = view;
    }

    /// <summary>User management, for administrators only; the session's own user is passed so it can be protected.</summary>
    private void ShowUsersWindow()
    {
        if (_currentUser is not { CanManageUsers: true } admin)
        {
            return;
        }

        new UsersWindow(_users, admin) { Owner = this }.ShowDialog();
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
        // The session may have timed out while the cart was being built; the
        // sale is recorded under whoever is signed in when it is paid.
        if (_checkoutView is null || RequireUser("Kirjaudu sisään jatkaaksesi myyntiä.") is not { } seller)
        {
            return;
        }

        var settings = TillSettings.Default;
        if (!ViewErrors.Try(this, "Kassan asetuksia ei voitu lukea.", () => settings = TillSettings.Load(_options)))
        {
            return;
        }

        var view = new PaymentView(_checkoutView.Cart, _salesRepository, seller.Username, settings);
        view.Cancelled += ShowCheckoutView;
        view.Finished += StartNewSale;
        ViewHost.Content = view;
    }

    /// <summary>
    /// Today's sales to refund from, opened from Kassa. Whoever is signed in
    /// when the refund is made is the one recorded on it.
    /// </summary>
    private void ShowRefundPicker()
    {
        if (RequireUser("Kirjaudu sisään tehdäksesi palautuksen.") is not { } user)
        {
            return;
        }

        var settings = TillSettings.Default;
        if (ViewErrors.Try(this, "Kassan asetuksia ei voitu lukea.", () => settings = TillSettings.Load(_options)))
        {
            new RefundPickerWindow(_salesRepository, user, settings) { Owner = this }.ShowDialog();
        }
    }

    /// <summary>After a paid sale the next customer starts from an empty cart.</summary>
    private void StartNewSale()
    {
        _checkoutView = null;
        ShowCheckoutView();
    }

    /// <summary>
    /// Every view needs a signed-in user. A seller may use Kassa only;
    /// Tuotteet, Myynti and Raportit need a manager or an administrator, and
    /// Admin an administrator. Each check is a <see cref="User"/> rule.
    /// </summary>
    private void OnNavigate(AppView destination)
    {
        if (destination == AppView.Checkout)
        {
            if (Authorize(user => user.CanUseCheckout, "Kirjaudu sisään myydäksesi.", "Kassa ei ole käytettävissä."))
            {
                ShowCheckoutView();
            }

            return;
        }

        if (destination == AppView.Products)
        {
            if (Authorize(user => user.CanManageCatalogue,
                    "Tuotteet ovat esimiehille ja ylläpitäjille. Kirjaudu jatkaaksesi.",
                    "Tuotteita voivat hallita vain esimiehet ja ylläpitäjät."))
            {
                ShowProductsView();
            }

            return;
        }

        if (destination == AppView.Sales)
        {
            if (Authorize(user => user.CanOpenSales,
                    "Myynti on esimiehille ja ylläpitäjille. Kirjaudu jatkaaksesi.",
                    "Myyntihistorian näkevät vain esimiehet ja ylläpitäjät."))
            {
                ShowSalesView();
            }

            return;
        }

        if (destination == AppView.Admin)
        {
            if (Authorize(user => user.CanOpenAdmin,
                    "Admin-osio on vain ylläpitäjille. Kirjaudu jatkaaksesi.",
                    "Admin-osio on vain ylläpitäjille."))
            {
                ShowAdminView();
            }

            return;
        }

        if (destination == AppView.Profile)
        {
            ShowProfileView();
            return;
        }

        if (destination == AppView.Reports)
        {
            if (Authorize(user => user.CanOpenReports,
                    "Raportit ovat ylläpitäjille ja esimiehille. Kirjaudu jatkaaksesi.",
                    "Raportit ovat vain ylläpitäjille ja esimiehille."))
            {
                ShowReportsView();
            }

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
