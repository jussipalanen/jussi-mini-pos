using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Past sales as a paged list, newest first, with a details window and void
/// per row. Voided sales stay listed, marked, but no longer count.
/// </summary>
public partial class SalesView : UserControl, INotifyPropertyChanged
{
    private readonly SalesRepository _sales;
    private readonly OptionsRepository _options;

    /// <summary>
    /// Returns the signed-in user, asking for a sign-in first when nobody is,
    /// so voiding does not send the user back to the start screen to log in.
    /// </summary>
    private readonly Func<string, User?> _signIn;

    private IReadOnlyList<Sale> _all = [];

    public SalesView(SalesRepository sales, OptionsRepository options, Func<string, User?> signIn)
    {
        InitializeComponent();

        _sales = sales;
        _options = options;
        _signIn = signIn;
        Pager.Changed += ShowPage;

        Reload();

        DataContext = this;
    }

    /// <summary>Raised when the user wants to leave the sales view.</summary>
    public event Action? Back;

    public Pager Pager { get; } = new("myynti", "myyntiä");

    /// <summary>The rows currently on screen.</summary>
    public ObservableCollection<Sale> PageSales { get; } = [];

    /// <summary>Sales that count: voided ones are listed but left out of every total.</summary>
    public int SaleCount => _all.Count(sale => !sale.IsVoided);

    /// <summary>Items sold across every sale that counts, not just this page.</summary>
    public int TotalItems => _all.Where(sale => !sale.IsVoided).Sum(sale => sale.ItemCount);

    /// <summary>Takings across every sale that counts, less what was refunded.</summary>
    public decimal GrandTotal => _all.Where(sale => !sale.IsVoided).Sum(sale => sale.Total - sale.RefundedTotal);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Reload()
    {
        _all = _sales.GetSales();

        // keepPage: deleting the last row of a page should not strand the user
        // on an empty one, and SetCount clamps for that.
        Pager.SetCount(_all.Count, keepPage: true);

        OnPropertyChanged(nameof(SaleCount));
        OnPropertyChanged(nameof(TotalItems));
        OnPropertyChanged(nameof(GrandTotal));
    }

    private void ShowPage()
    {
        PageSales.Clear();
        foreach (var sale in _all.Skip(Pager.Skip).Take(Pager.Take))
        {
            PageSales.Add(sale);
        }
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Sale sale)
        {
            return;
        }

        new SaleDetailsWindow(sale) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    /// <summary>
    /// Voids a sale after a sign-in check and a reason. The rule for who may
    /// is <see cref="User.CanVoidSales"/>; the button being there is not it.
    /// </summary>
    private void Void_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Sale { CanBeVoided: true } sale)
        {
            return;
        }

        var user = _signIn("Myynnin mitätöinti on esimiehille ja ylläpitäjille. Kirjaudu jatkaaksesi.");
        if (user is null)
        {
            return;
        }

        if (!user.CanVoidSales)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"Myynnin voi mitätöidä vain esimies tai ylläpitäjä.\n\nOlet kirjautunut käyttäjänä {user.Display}.",
                "JussiMiniPos",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new VoidSaleWindow(sale, user) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (ViewErrors.Try(this, "Myyntiä ei voitu mitätöidä.", () => _sales.VoidSale(sale.Id, user.Username, dialog.Reason)))
        {
            Reload();
        }
    }

    /// <summary>
    /// Gives money back against a sale. Myynti is for managers and
    /// administrators, who may refund any sale; the rule itself is checked in
    /// <see cref="RefundWindow.Run"/>.
    /// </summary>
    private void Refund_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Sale { CanBeRefunded: true } sale
            || _signIn("Kirjaudu sisään tehdäksesi palautuksen.") is not { } user)
        {
            return;
        }

        var settings = TillSettings.Default;
        if (ViewErrors.Try(this, "Kassan asetuksia ei voitu lukea.", () => settings = TillSettings.Load(_options))
            && RefundWindow.Run(this, sale, user, _sales, settings))
        {
            Reload();
        }
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => Pager.Previous();

    private void NextPage_Click(object sender, RoutedEventArgs e) => Pager.Next();

    private void PageNumber_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Pager.PageButton { IsEllipsis: false } button)
        {
            Pager.GoTo(button.Target);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Back?.Invoke();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
