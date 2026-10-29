using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Adds a user or changes one, as an administrator. Validates and exposes the
/// result as a draft; writing it is the caller's job.
/// </summary>
public partial class UserEditWindow : Window
{
    private readonly UserRepository _users;
    private readonly User? _user;

    /// <param name="user">The account to change, or null for a new one.</param>
    /// <param name="isSelf">
    /// True when this is the signed-in administrator's own account, whose
    /// role is not changed here: demoting yourself mid-session would leave the
    /// session holding a role it no longer has.
    /// </param>
    public UserEditWindow(UserRepository users, User? user, bool isSelf)
    {
        InitializeComponent();

        _users = users;
        _user = user;
        IsExisting = user is not null;
        CanChangeRole = !isSelf;
        WindowTitle = user is null ? "Uusi käyttäjä" : $"Muokkaa käyttäjää {user.Username}";
        RoleHelp = isSelf
            ? "Omaa rooliasi ei voi muuttaa täältä. Pyydä toista ylläpitäjää muuttamaan se."
            : "Myyjä: vain kassa. Esimies: myös tuotteet, myynti ja raportit. Ylläpitäjä: kaikki, myös Admin.";
        DataContext = this;

        UsernameBox.Text = user?.Username ?? string.Empty;
        EmailBox.Text = user?.Email ?? string.Empty;
        FirstNameBox.Text = user?.FirstName ?? string.Empty;
        LastNameBox.Text = user?.LastName ?? string.Empty;

        RoleList.ItemsSource = Roles;
        RoleList.SelectedItem = Roles.First(role => role.Role == (user?.Role ?? UserRole.Seller));

        // A new user always gets a password; an existing one only when asked.
        PasswordPanel.Visibility = IsExisting ? Visibility.Collapsed : Visibility.Visible;

        UsernameBox.Focus();
    }

    /// <summary>A role as a chip, with its Finnish name.</summary>
    public sealed record RoleChoice(UserRole Role, string Name);

    private static readonly IReadOnlyList<RoleChoice> Roles =
        [.. new[] { UserRole.Seller, UserRole.Manager, UserRole.Admin }.Select(r => new RoleChoice(r, UserRoleNames.Finnish(r)))];

    public string WindowTitle { get; }

    public string RoleHelp { get; }

    public bool IsExisting { get; }

    public bool CanChangeRole { get; }

    /// <summary>The values to save, filled in once the dialog returns true.</summary>
    public sealed record UserDraft(string Username, string Email, string FirstName, string LastName, UserRole Role, string? Password);

    public UserDraft? Draft { get; private set; }

    private void SetPassword_Changed(object sender, RoutedEventArgs e) =>
        PasswordPanel.Visibility = SetPasswordBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void Generate_Click(object sender, RoutedEventArgs e) => PasswordBox.Text = PasswordGenerator.Generate();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var error = Validate();
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private string? Validate()
    {
        var username = UsernameBox.Text.Trim();
        var email = EmailBox.Text.Trim();

        if (username.Length == 0 || username.Any(char.IsWhiteSpace))
        {
            return "Käyttäjätunnus ei voi olla tyhjä eikä sisältää välilyöntejä.";
        }

        // Not a full address check — that is a losing game — just enough to
        // catch a name typed into the wrong box, as Oma profiili does.
        if (!email.Contains('@') || email.StartsWith('@') || email.EndsWith('@'))
        {
            return "Sähköpostiosoite ei näytä oikealta.";
        }

        // Checked before writing, so the failure is a sentence rather than a
        // database error about a unique index.
        if (_users.FindUser(username) is { } byName && byName.Id != _user?.Id)
        {
            return $"Käyttäjätunnus {username} on jo käytössä.";
        }

        if (_users.FindUser(email) is { } byEmail && byEmail.Id != _user?.Id)
        {
            return $"Sähköposti {email} on jo toisen käyttäjän käytössä.";
        }

        if (RoleList.SelectedItem is not RoleChoice role)
        {
            return "Valitse rooli.";
        }

        string? password = null;
        if (PasswordPanel.Visibility == Visibility.Visible)
        {
            password = PasswordBox.Text;
            if (password.Length < PasswordHasher.MinimumLength)
            {
                return $"Salasanan on oltava vähintään {PasswordHasher.MinimumLength} merkkiä. Voit myös luoda sen painikkeella.";
            }
        }

        Draft = new UserDraft(username, email, FirstNameBox.Text.Trim(), LastNameBox.Text.Trim(), role.Role, password);
        return null;
    }
}
