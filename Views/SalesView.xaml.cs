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
/// Past sales as a paged list, newest first, with a details window and delete
/// per row.
/// </summary>
public partial class SalesView : UserControl, INotifyPropertyChanged
{
    private readonly SalesRepository _sales;

    private IReadOnlyList<Sale> _all = [];

    public SalesView(SalesRepository sales)
    {
        InitializeComponent();

        _sales = sales;
        Pager.Changed += ShowPage;

        Reload();

        DataContext = this;
    }

    /// <summary>Raised when the user wants to leave the sales view.</summary>
    public event Action? Back;

    public Pager Pager { get; } = new("myynti", "myyntiä");

    /// <summary>The rows currently on screen.</summary>
    public ObservableCollection<Sale> PageSales { get; } = [];

    public int SaleCount => _all.Count;

    /// <summary>Items sold across every sale, not just this page.</summary>
    public int TotalItems => _all.Sum(sale => sale.ItemCount);

    /// <summary>Takings across every sale.</summary>
    public decimal GrandTotal => _all.Sum(sale => sale.Total);

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

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Sale sale)
        {
            return;
        }

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Poistetaanko myynti #{sale.Id} ({sale.SoldAt:dd.MM.yyyy HH:mm}, {sale.Total:N2} €)?\n\n" +
            "Myynti ja sen rivit poistetaan lopullisesti, eivätkä ne näy enää raporteissa.",
            "Poista myynti",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _sales.DeleteSale(sale.Id);
        Reload();
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
