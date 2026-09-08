using System;
using System.Windows;
using System.Windows.Controls;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Landing view with the four main navigation tiles, and below them the
/// session: who is signed in, signing in and out, and the way into Admin.
/// </summary>
public partial class StartView : UserControl
{
    /// <param name="currentUser">
    /// Who is signed in, or null. The view is rebuilt rather than updated when
    /// this changes — the shell recreates it on every visit anyway.
    /// </param>
    public StartView(User? currentUser = null)
    {
        InitializeComponent();

        CurrentUser = currentUser;
        DataContext = this;
    }

    /// <summary>Raised with the destination the user picked.</summary>
    public event Action<AppView>? Navigate;

    /// <summary>Raised when the user wants the login prompt.</summary>
    public event Action? LoginRequested;

    /// <summary>Raised when the user wants to end the session.</summary>
    public event Action? LogoutRequested;

    public User? CurrentUser { get; }

    /// <summary>The build's version, stamped in the corner.</summary>
    public string Version => AppInfo.DisplayVersion;

    public bool IsSignedIn => CurrentUser is not null;

    public bool IsSignedOut => CurrentUser is null;

    /// <summary>"Kirjautunut: admin (Ylläpitäjä)".</summary>
    public string SignedInText =>
        CurrentUser is null ? string.Empty : $"Kirjautunut: {CurrentUser.Display}";

    private void Checkout_Click(object sender, RoutedEventArgs e) => Navigate?.Invoke(AppView.Checkout);

    private void Products_Click(object sender, RoutedEventArgs e) => Navigate?.Invoke(AppView.Products);

    private void Sales_Click(object sender, RoutedEventArgs e) => Navigate?.Invoke(AppView.Sales);

    private void Reports_Click(object sender, RoutedEventArgs e) => Navigate?.Invoke(AppView.Reports);

    private void Admin_Click(object sender, RoutedEventArgs e) => Navigate?.Invoke(AppView.Admin);

    private void Profile_Click(object sender, RoutedEventArgs e) => Navigate?.Invoke(AppView.Profile);

    private void Login_Click(object sender, RoutedEventArgs e) => LoginRequested?.Invoke();

    private void Logout_Click(object sender, RoutedEventArgs e) => LogoutRequested?.Invoke();
}
