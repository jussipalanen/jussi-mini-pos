using System.Windows;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Read-only receipt for one past sale: what was bought, at what price, how it
/// was paid and the total. With till settings it can also print a copy.
/// </summary>
public partial class SaleDetailsWindow : Window
{
    private readonly TillSettings? _settings;

    /// <param name="settings">How to print a copy; null leaves the print button out.</param>
    public SaleDetailsWindow(Sale sale, TillSettings? settings = null)
    {
        InitializeComponent();

        Sale = sale;
        _settings = settings;
        WindowTitle = $"Myynti #{sale.Id}";

        DataContext = this;
    }

    public Sale Sale { get; }

    public string WindowTitle { get; }

    public bool CanPrint => _settings is not null;

    /// <summary>A reprint is marked as a copy, so it cannot pass for a second sale.</summary>
    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is { } settings)
        {
            ReceiptPrinting.TryPrint(this, Receipts.ForSale(Sale, settings.Shop, isCopy: true), settings, $"Kuitin kopio #{Sale.Id}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
