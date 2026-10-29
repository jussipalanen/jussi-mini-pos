using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Login prompt. Verifying a password is deliberately slow — see
/// <see cref="PasswordHasher"/> — so it runs off the UI thread and the window
/// says it is working.
/// </summary>
public partial class LoginWindow : Window, INotifyPropertyChanged
{
    /// <summary>
    /// Held after a failed attempt, so guessing passwords in bulk is slower
    /// than typing them. Short enough that a mistyped password is not annoying.
    /// </summary>
    private static readonly TimeSpan FailureDelay = TimeSpan.FromMilliseconds(700);

    private readonly UserRepository _users;

    private bool _isChecking;
    private string _errorMessage = string.Empty;

    /// <param name="users">Where the credentials are checked.</param>
    /// <param name="purpose">
    /// Why the prompt appeared, so opening Admin without being signed in
    /// explains itself rather than just demanding a password.
    /// </param>
    public LoginWindow(UserRepository users, string? purpose = null)
    {
        InitializeComponent();

        _users = users;
        PurposeText = purpose ?? "Kirjaudu sisään jatkaaksesi.";

        DataContext = this;

        UsernameBox.Focus();
    }

    /// <summary>The signed-in user, once <see cref="Window.DialogResult"/> is true.</summary>
    public User? SignedInUser { get; private set; }

    public string PurposeText { get; }

    /// <summary>Nothing to check until both fields have something in them.</summary>
    public bool CanSignIn =>
        !_isChecking && UsernameBox.Text.Trim().Length > 0 && PasswordBox.Password.Length > 0;

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (_isChecking == value)
            {
                return;
            }

            _isChecking = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSignIn));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            _errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _errorMessage.Length > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(CanSignIn));

        // The old failure should not sit there while a new attempt is typed.
        if (HasError)
        {
            ErrorMessage = string.Empty;
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSignIn)
        {
            return;
        }

        var name = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;

        IsChecking = true;
        ErrorMessage = string.Empty;

        try
        {
            // PBKDF2 at 600k iterations blocks for a noticeable moment, and
            // SQLite is synchronous, so neither belongs on the UI thread.
            var user = await Task.Run(() => _users.Authenticate(name, password));

            if (user is null)
            {
                await Task.Delay(FailureDelay);

                ErrorMessage = "Käyttäjätunnus tai salasana on väärä.";
                PasswordBox.Clear();
                PasswordBox.Focus();
                return;
            }

            SignedInUser = user;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            // An exception out of an async void handler is unhandled and would
            // take the application down. A failed login is not worth that.
            ErrorMessage = $"Kirjautuminen ei onnistunut: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
