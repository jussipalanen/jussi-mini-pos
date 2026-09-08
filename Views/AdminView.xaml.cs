using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Admin: the application's own settings, as opposed to its data. Right now
/// that is the AI assistant — whether it runs at all, the Gemini API key it
/// calls with, and which model it calls.
///
/// The on/off flag and the model are rows in the Options table. The key is
/// not: it is a secret, and the database file is what gets copied around as a
/// backup, so it goes to a file beside it instead.
/// </summary>
public partial class AdminView : UserControl, INotifyPropertyChanged
{
    private readonly OptionsRepository _options;
    private readonly string? _dataDirectory;

    /// <summary>
    /// The control state as it was loaded. "Has something changed" is answered
    /// by comparing against this rather than by a flag set while the controls
    /// are populated: the model list's SelectionChanged arrives after the
    /// constructor has returned, because its ItemsSource is a binding, and any
    /// such flag would already have been cleared by then.
    /// </summary>
    private bool _baseEnabled;

    private string? _baseModel;

    /// <summary>False until the controls hold real values worth comparing.</summary>
    private bool _ready;

    private bool _isAiEnabled;
    private bool _hasChanges;
    private bool _isTesting;
    private string _statusText = string.Empty;
    private Geometry? _statusIcon;
    private Brush? _statusBrush;

    /// <param name="options">Where the on/off flag and the model are stored.</param>
    /// <param name="dataDirectory">Where the key file belongs.</param>
    /// <param name="currentUser">
    /// Who got in. The shell has already checked the role; this is only shown,
    /// so the person at the till can see which account is making the change.
    /// </param>
    public AdminView(OptionsRepository options, string? dataDirectory, User? currentUser = null)
    {
        InitializeComponent();

        _options = options;
        _dataDirectory = dataDirectory;
        CurrentUser = currentUser;

        DataContext = this;

        Load();
    }

    public User? CurrentUser { get; }

    public bool IsSignedIn => CurrentUser is not null;

    public string SignedInText =>
        CurrentUser is null ? string.Empty : $"Kirjautunut: {CurrentUser.Display}";

    /// <summary>Raised when the user wants to leave the admin view.</summary>
    public event Action? Back;

    public IReadOnlyList<AiSettings.ModelChoice> Models => AiSettings.Models;

    /// <summary>
    /// Mirrors the checkbox. The key and model sections below it are useless
    /// while the assistant is off, so they follow this.
    /// </summary>
    public bool IsAiEnabled
    {
        get => _isAiEnabled;
        private set
        {
            if (_isAiEnabled == value)
            {
                return;
            }

            _isAiEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SectionOpacity));
            OnPropertyChanged(nameof(CanTest));
        }
    }

    /// <summary>Dims the disabled sections, since IsEnabled alone barely shows.</summary>
    public double SectionOpacity => _isAiEnabled ? 1.0 : 0.45;

    /// <summary>True while a connection test is out, which parks its button.</summary>
    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (_isTesting == value)
            {
                return;
            }

            _isTesting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanTest));
        }
    }

    /// <summary>Nothing to save until something is actually different.</summary>
    public bool HasChanges
    {
        get => _hasChanges;
        private set
        {
            if (_hasChanges == value)
            {
                return;
            }

            _hasChanges = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// There is something to test when the assistant is on, a request is not
    /// already out, and a key exists — either typed in or already stored.
    /// </summary>
    public bool CanTest =>
        _isAiEnabled && !_isTesting && (KeyBox.Password.Length > 0 || HasStoredKey || HasEnvironmentKey);

    /// <summary>True when a key file is there for the assistant to read.</summary>
    public bool HasStoredKey => AiSettings.HasStoredApiKey(_dataDirectory);

    /// <summary>
    /// True when the environment is supplying a key. Worth saying out loud:
    /// otherwise an empty field next to a working assistant makes no sense.
    /// </summary>
    public bool HasEnvironmentKey => AiSettings.HasApiKeyFromEnvironment;

    public string EnvironmentKeyText =>
        $"Ympäristömuuttuja {AiSettings.ApiKeyVariable} on asetettu. Sitä käytetään, " +
        $"kun avainta ei ole tallennettu tähän.";

    /// <summary>Says what saving an empty field will and will not do.</summary>
    public string KeyStatusText => HasStoredKey
        ? "Avain on tallennettu. Kirjoita uusi vain, jos haluat vaihtaa sen — tyhjä kenttä ei poista avainta."
        : "Avainta ei ole tallennettu. Ilman avainta avustaja näyttää pelkät hakutulokset.";

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _statusText.Length > 0;

    public Geometry? StatusIcon
    {
        get => _statusIcon;
        private set
        {
            _statusIcon = value;
            OnPropertyChanged();
        }
    }

    public Brush? StatusBrush
    {
        get => _statusBrush;
        private set
        {
            _statusBrush = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The model the list is currently on.</summary>
    private string SelectedModelId =>
        (ModelList.SelectedItem as AiSettings.ModelChoice)?.Id ?? AiSettings.DefaultModel;

    /// <summary>Fills the controls from what is currently stored.</summary>
    private void Load()
    {
        var settings = AiSettings.Load(_options, _dataDirectory);

        EnabledBox.IsChecked = settings.IsEnabled;
        IsAiEnabled = settings.IsEnabled;

        // A model set by hand or by an environment variable may not be on the
        // list at all. Selecting nothing would then read as the cheapest one,
        // so fall back to the default's entry — and take the baseline from
        // what actually ended up selected, so that fallback is not reported as
        // an unsaved change.
        ModelList.SelectedItem =
            AiSettings.Models.FirstOrDefault(m => m.Id == settings.Model)
            ?? AiSettings.Models.FirstOrDefault(m => m.Id == AiSettings.DefaultModel);

        KeyBox.Clear();

        Snapshot();
        Refresh();
    }

    /// <summary>Takes the current control state as the "nothing changed" mark.</summary>
    private void Snapshot()
    {
        _baseEnabled = EnabledBox.IsChecked == true;
        _baseModel = SelectedModelId;
        _ready = true;

        HasChanges = false;
    }

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        IsAiEnabled = EnabledBox.IsChecked == true;
        Recompute();
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e) => Recompute();

    private void Key_Changed(object sender, RoutedEventArgs e) => Recompute();

    /// <summary>
    /// Works out whether anything now differs from the loaded state. A blank
    /// key field is not a change: it means "leave the stored key alone".
    /// </summary>
    private void Recompute()
    {
        if (!_ready)
        {
            return;
        }

        var changed = EnabledBox.IsChecked == true != _baseEnabled
            || SelectedModelId != _baseModel
            || KeyBox.Password.Length > 0;

        // Typing the first character of a key makes the test worth offering.
        OnPropertyChanged(nameof(CanTest));

        if (changed == HasChanges)
        {
            return;
        }

        HasChanges = changed;

        // The old "saved" tick would otherwise sit there next to fresh edits.
        if (changed)
        {
            StatusText = string.Empty;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var enabled = EnabledBox.IsChecked == true;
        var model = SelectedModelId;
        var key = KeyBox.Password;

        var saved = ViewErrors.Try(this, "Asetuksia ei voitu tallentaa.", () =>
        {
            _options.SetBool(AiSettings.EnabledOption, enabled);
            _options.Set(AiSettings.ModelOption, model);

            // An empty field means "leave the key alone", so that saving the
            // other two settings does not quietly log the till out of Gemini.
            if (!string.IsNullOrWhiteSpace(key))
            {
                AiSettings.SaveApiKey(_dataDirectory, key);
            }
        });

        if (!saved)
        {
            return;
        }

        KeyBox.Clear();

        Snapshot();
        SetStatus("Asetukset tallennettu.", ok: true);
        Refresh();
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            "Poistetaanko tallennettu Gemini API -avain?\n\n" +
            "AI-avustaja näyttää tämän jälkeen pelkät hakutulokset, kunnes uusi avain lisätään.",
            "JussiMiniPos",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        // Deletes straight away rather than on save: a destructive action
        // waiting behind a Save button is how a key gets removed by accident.
        if (!ViewErrors.Try(this, "Avainta ei voitu poistaa.", () =>
            AiSettings.SaveApiKey(_dataDirectory, null)))
        {
            return;
        }

        SetStatus("Tallennettu avain poistettu.", ok: false);
        Refresh();
    }

    /// <summary>
    /// Calls Gemini once with what is on screen. Nothing is saved first, so a
    /// key can be checked before it is committed — and a key that turns out to
    /// be wrong never replaces a working one.
    /// </summary>
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!CanTest)
        {
            return;
        }

        // A typed key beats the stored one, since that is the one being tried.
        var key = KeyBox.Password.Length > 0
            ? KeyBox.Password.Trim()
            : AiSettings.Load(_options, _dataDirectory).ApiKey;

        var model = SelectedModelId;

        IsTesting = true;
        SetStatus($"Testataan yhteyttä malliin {model}…", ok: true);

        try
        {
            await new GeminiClient(new AiSettings(true, key, model)).TestAsync(CancellationToken.None);
            SetStatus($"Yhteys toimii. Malli {model} vastasi.", ok: true);
        }
        catch (AssistantException ex)
        {
            SetStatus(ex.Message, ok: false);
        }
        catch (Exception ex)
        {
            // An exception out of an async void handler is unhandled and would
            // take the application down. A failed test is not worth that.
            SetStatus($"Testi ei onnistunut: {ex.Message}", ok: false);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void OpenLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        // Only ever the http(s) address in the XAML. Checked anyway, so this
        // can never be talked into launching something else.
        if (e.Uri.Scheme != Uri.UriSchemeHttp && e.Uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            // UseShellExecute, or .NET tries to run the URL as a program.
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // No browser, or it refused to start. The address is on screen to
            // be typed by hand, so this is not worth an error dialog.
            SetStatus($"Selainta ei voitu avata: {ex.Message}", ok: false);
        }
    }

    private void SetStatus(string text, bool ok)
    {
        StatusIcon = (Geometry)FindResource(ok ? "Icon.CircleCheck" : "Icon.CircleAlert");
        StatusBrush = (Brush)FindResource(ok ? "Brush.Success" : "Brush.Danger");
        StatusText = text;
    }

    /// <summary>Re-reads what only the file system knows.</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(HasStoredKey));
        OnPropertyChanged(nameof(HasEnvironmentKey));
        OnPropertyChanged(nameof(KeyStatusText));
        OnPropertyChanged(nameof(CanTest));
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        // Settings are worth one question: leaving loses them silently
        // otherwise, and the flag and model are two clicks to redo but the key
        // is a trip back to the browser.
        if (HasChanges)
        {
            var confirm = MessageBox.Show(
                Window.GetWindow(this),
                "Asetuksia on muutettu, mutta niitä ei ole tallennettu.\n\nPoistutaanko tallentamatta?",
                "JussiMiniPos",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.OK)
            {
                return;
            }
        }

        Back?.Invoke();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
