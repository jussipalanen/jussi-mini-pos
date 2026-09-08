using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JussiMiniPos.Models;
using JussiMiniPos.Services;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos.Views;

/// <summary>
/// What a user may change about their own account: their name, their email and
/// their password. Not their username and not their role — those belong to an
/// administrator, and <see cref="UserRepository.UpdateProfile"/> cannot write
/// them at all.
///
/// The two cards save independently. Renaming yourself should not require your
/// password, and changing your password should not be bundled with an edit you
/// might not want to keep.
/// </summary>
public partial class ProfileView : UserControl, INotifyPropertyChanged
{
    private readonly UserRepository _users;

    /// <summary>
    /// The account as last read from the database. Replaced after a successful
    /// save, so the baseline the form compares against moves with it.
    /// </summary>
    private User _user;

    private bool _isChangingPassword;
    private bool _canSaveDetails;

    private string _detailsStatusText = string.Empty;
    private Geometry? _detailsStatusIcon;
    private Brush? _detailsStatusBrush;

    private string _passwordStatusText = string.Empty;
    private Geometry? _passwordStatusIcon;
    private Brush? _passwordStatusBrush;

    public ProfileView(UserRepository users, User user)
    {
        InitializeComponent();

        _users = users;
        _user = user;

        DataContext = this;

        LoadDetails();
    }

    /// <summary>Raised when the user wants to leave the profile view.</summary>
    public event Action? Back;

    /// <summary>
    /// Raised with the updated account after a saved change, so the shell can
    /// replace the session's copy and the start screen shows the new name.
    /// </summary>
    public event Action<User>? Updated;

    /// <summary>Which login these details belong to.</summary>
    public string AccountText => $"{_user.Username} · {UserRoleNames.Finnish(_user.Role)}";

    public string PasswordRuleText =>
        $"Vaihtaminen vaatii nykyisen salasanan. Uuden on oltava vähintään " +
        $"{PasswordHasher.MinimumLength} merkkiä pitkä.";

    /// <summary>Nothing to save until a field differs from what is stored.</summary>
    public bool CanSaveDetails
    {
        get => _canSaveDetails;
        private set
        {
            if (_canSaveDetails == value)
            {
                return;
            }

            _canSaveDetails = value;
            OnPropertyChanged();
        }
    }

    public bool IsChangingPassword
    {
        get => _isChangingPassword;
        private set
        {
            if (_isChangingPassword == value)
            {
                return;
            }

            _isChangingPassword = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanChangePassword));
        }
    }

    /// <summary>
    /// All three boxes filled, the two new ones matching, and no attempt
    /// already running. The old password is not checked here — that costs a
    /// PBKDF2 derivation and belongs to the attempt itself.
    /// </summary>
    public bool CanChangePassword =>
        !_isChangingPassword
        && OldPasswordBox.Password.Length > 0
        && NewPasswordBox.Password.Length > 0
        && NewPasswordBox.Password == ConfirmPasswordBox.Password;

    /// <summary>
    /// True once the confirmation has something in it that does not match, so
    /// a typo is pointed out while it is being made rather than on save.
    /// </summary>
    public bool HasConfirmMismatch =>
        ConfirmPasswordBox.Password.Length > 0
        && NewPasswordBox.Password != ConfirmPasswordBox.Password;

    public string DetailsStatusText
    {
        get => _detailsStatusText;
        private set
        {
            _detailsStatusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDetailsStatus));
        }
    }

    public bool HasDetailsStatus => _detailsStatusText.Length > 0;

    public Geometry? DetailsStatusIcon
    {
        get => _detailsStatusIcon;
        private set
        {
            _detailsStatusIcon = value;
            OnPropertyChanged();
        }
    }

    public Brush? DetailsStatusBrush
    {
        get => _detailsStatusBrush;
        private set
        {
            _detailsStatusBrush = value;
            OnPropertyChanged();
        }
    }

    public string PasswordStatusText
    {
        get => _passwordStatusText;
        private set
        {
            _passwordStatusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPasswordStatus));
        }
    }

    public bool HasPasswordStatus => _passwordStatusText.Length > 0;

    public Geometry? PasswordStatusIcon
    {
        get => _passwordStatusIcon;
        private set
        {
            _passwordStatusIcon = value;
            OnPropertyChanged();
        }
    }

    public Brush? PasswordStatusBrush
    {
        get => _passwordStatusBrush;
        private set
        {
            _passwordStatusBrush = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Fills the detail boxes from the stored account.</summary>
    private void LoadDetails()
    {
        FirstNameBox.Text = _user.FirstName;
        LastNameBox.Text = _user.LastName;
        EmailBox.Text = _user.Email;

        CanSaveDetails = false;
        OnPropertyChanged(nameof(AccountText));
    }

    private void Details_Changed(object sender, TextChangedEventArgs e)
    {
        // Compared against the stored account rather than tracked with a flag,
        // so typing a change and undoing it leaves nothing to save.
        CanSaveDetails =
            FirstNameBox.Text.Trim() != _user.FirstName
            || LastNameBox.Text.Trim() != _user.LastName
            || EmailBox.Text.Trim() != _user.Email;

        if (HasDetailsStatus)
        {
            DetailsStatusText = string.Empty;
        }
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(CanChangePassword));
        OnPropertyChanged(nameof(HasConfirmMismatch));

        if (HasPasswordStatus)
        {
            PasswordStatusText = string.Empty;
        }
    }

    private void SaveDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSaveDetails)
        {
            return;
        }

        var firstName = FirstNameBox.Text.Trim();
        var lastName = LastNameBox.Text.Trim();
        var email = EmailBox.Text.Trim();

        if (email.Length == 0)
        {
            SetDetailsStatus("Sähköposti ei voi olla tyhjä.", ok: false);
            return;
        }

        // Not a full address check — that is a losing game — just enough to
        // catch a name typed into the wrong box.
        if (!email.Contains('@') || email.StartsWith('@') || email.EndsWith('@'))
        {
            SetDetailsStatus("Sähköpostiosoite ei näytä oikealta.", ok: false);
            return;
        }

        // Checked before writing, so the failure is a sentence rather than a
        // SQLite error about a unique index.
        if (!string.Equals(email, _user.Email, StringComparison.OrdinalIgnoreCase)
            && _users.FindUser(email) is { } other
            && other.Id != _user.Id)
        {
            SetDetailsStatus($"Sähköposti {email} on jo toisen käyttäjän käytössä.", ok: false);
            return;
        }

        var saved = ViewErrors.Try(this, "Tietoja ei voitu tallentaa.", () =>
            _users.UpdateProfile(_user.Id, firstName, lastName, email));

        if (!saved)
        {
            return;
        }

        // Re-read rather than patched together locally, so what is on screen
        // is what the database actually holds.
        Reload();
        SetDetailsStatus("Tiedot tallennettu.", ok: true);
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangePassword)
        {
            return;
        }

        var oldPassword = OldPasswordBox.Password;
        var newPassword = NewPasswordBox.Password;

        if (newPassword.Length < PasswordHasher.MinimumLength)
        {
            SetPasswordStatus(
                $"Uuden salasanan on oltava vähintään {PasswordHasher.MinimumLength} merkkiä.",
                ok: false);
            return;
        }

        if (newPassword == oldPassword)
        {
            SetPasswordStatus("Uusi salasana on sama kuin nykyinen.", ok: false);
            return;
        }

        IsChangingPassword = true;
        PasswordStatusText = string.Empty;

        try
        {
            // Verifying and hashing are two PBKDF2 derivations at 600k
            // iterations each, so neither belongs on the UI thread.
            var verified = await Task.Run(() => _users.VerifyPassword(_user.Id, oldPassword));

            if (!verified)
            {
                SetPasswordStatus("Nykyinen salasana on väärä.", ok: false);
                OldPasswordBox.Clear();
                OldPasswordBox.Focus();
                return;
            }

            await Task.Run(() => _users.SetPassword(_user.Id, newPassword));

            OldPasswordBox.Clear();
            NewPasswordBox.Clear();
            ConfirmPasswordBox.Clear();

            SetPasswordStatus("Salasana vaihdettu.", ok: true);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            // An exception out of an async void handler is unhandled and would
            // take the application down. A failed change is not worth that.
            SetPasswordStatus($"Salasanaa ei voitu vaihtaa: {ex.Message}", ok: false);
        }
        finally
        {
            IsChangingPassword = false;
        }
    }

    /// <summary>
    /// Re-reads the account and tells the shell, so the start screen's
    /// "Kirjautunut" line follows a renamed user.
    /// </summary>
    private void Reload()
    {
        if (_users.FindUserById(_user.Id) is { } fresh)
        {
            _user = fresh;
            Updated?.Invoke(fresh);
        }

        LoadDetails();
    }

    private void SetDetailsStatus(string text, bool ok)
    {
        DetailsStatusIcon = Status(ok);
        DetailsStatusBrush = StatusBrush(ok);
        DetailsStatusText = text;
    }

    private void SetPasswordStatus(string text, bool ok)
    {
        PasswordStatusIcon = Status(ok);
        PasswordStatusBrush = StatusBrush(ok);
        PasswordStatusText = text;
    }

    private Geometry Status(bool ok) =>
        (Geometry)FindResource(ok ? "Icon.CircleCheck" : "Icon.CircleAlert");

    private Brush StatusBrush(bool ok) =>
        (Brush)FindResource(ok ? "Brush.Success" : "Brush.Danger");

    private void Back_Click(object sender, RoutedEventArgs e) => Back?.Invoke();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
