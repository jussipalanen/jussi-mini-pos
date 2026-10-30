using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Kassa's way into a refund: today's sales that still have something to
/// give back. A seller sees only their own, which is all
/// <see cref="User.CanRefund"/> lets them refund; older sales are refunded
/// from Myynti by a manager.
/// </summary>
public partial class RefundPickerWindow : Window, INotifyPropertyChanged
{
    private readonly SalesRepository _sales;
    private readonly User _user;
    private readonly TillSettings _settings;

    public RefundPickerWindow(SalesRepository sales, User user, TillSettings settings)
    {
        InitializeComponent();

        _sales = sales;
        _user = user;
        _settings = settings;

        DataContext = this;
        Reload();
    }

    public ObservableCollection<Sale> Sales { get; } = [];

    public bool IsEmpty => Sales.Count == 0;

    public string Summary => _user.CanOpenSales
        ? "Kaikki tämän päivän myynnit, joista voi vielä palauttaa tuotteita. Vanhemmat myynnit löytyvät Myynti-näkymästä."
        : "Omat tämän päivän myyntisi, joista voi vielä palauttaa tuotteita.";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Reload()
    {
        var today = ReportsRepository.Today;
        IReadOnlyList<Sale> sales = [];
        if (!ViewErrors.Try(this, "Myyntejä ei voitu lukea.", () => sales = _sales.GetSales()))
        {
            return;
        }

        Sales.Clear();
        foreach (var sale in sales.Where(sale =>
            sale.CanBeRefunded
            && ReportsRepository.LocalDate(sale.SoldAt) == today
            && _user.CanRefund(sale.SoldBy, soldToday: true)))
        {
            Sales.Add(sale);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RefundSelected()
    {
        if (SaleGrid.SelectedItem is Sale sale && RefundWindow.Run(this, sale, _user, _sales, _settings))
        {
            Reload();
        }
    }

    private void Refund_Click(object sender, RoutedEventArgs e) => RefundSelected();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SaleGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RefundSelected();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
