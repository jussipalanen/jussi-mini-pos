using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// User management for administrators: add, change, unlock and remove
/// accounts. The rules — the last administrator stays, passwords are long
/// enough — live in <see cref="UserRepository"/>; this only asks.
/// </summary>
public partial class UsersWindow : Window, INotifyPropertyChanged
{
    private readonly UserRepository _users;

    /// <summary>The administrator using this window, whose own account is protected here.</summary>
    private readonly User _admin;

    public UsersWindow(UserRepository users, User admin)
    {
        InitializeComponent();
        _users = users;
        _admin = admin;
        DataContext = this;
        Reload();
    }

    public IReadOnlyList<User> Users { get; private set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private User? Selected => UserGrid.SelectedItem as User;

    private void Reload()
    {
        IReadOnlyList<User> users = [];
        if (ViewErrors.Try(this, "Käyttäjiä ei voitu lukea.", () => users = _users.GetUsers()))
        {
            Users = users;
            OnPropertyChanged(nameof(Users));
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var editor = new UserEditWindow(_users, user: null, isSelf: false) { Owner = this };
        if (editor.ShowDialog() != true || editor.Draft is not { Password: { } password } draft)
        {
            return;
        }

        if (ViewErrors.Try(this, "Käyttäjää ei voitu lisätä.", () =>
                _users.InsertUser(draft.Username, draft.Email, password, draft.Role, draft.FirstName, draft.LastName)))
        {
            Reload();
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => EditSelected();

    private void UserGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelected();

    private void EditSelected()
    {
        if (Selected is not { } user)
        {
            return;
        }

        var editor = new UserEditWindow(_users, user, isSelf: user.Id == _admin.Id) { Owner = this };
        if (editor.ShowDialog() != true || editor.Draft is not { } draft)
        {
            return;
        }

        if (ViewErrors.Try(this, "Käyttäjää ei voitu tallentaa.", () =>
                _users.UpdateAccount(user.Id, draft.Username, draft.Email, draft.Role,
                    draft.FirstName, draft.LastName, draft.Password)))
        {
            Reload();
        }
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsLocked: true } user)
        {
            return;
        }

        if (ViewErrors.Try(this, "Lukitusta ei voitu avata.", () => _users.Unlock(user.Id)))
        {
            Reload();
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } user)
        {
            return;
        }

        if (user.Id == _admin.Id)
        {
            MessageBox.Show(this, "Et voi poistaa omaa tunnustasi.", "Käyttäjät",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Poistetaanko käyttäjä {user.Display}?\n\nTehdyt myynnit ja tapahtumaloki säilyttävät käyttäjätunnuksen.",
            "Poista käyttäjä",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Yes
            && ViewErrors.Try(this, "Käyttäjää ei voitu poistaa.", () => _users.DeleteUser(user.Id)))
        {
            Reload();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
