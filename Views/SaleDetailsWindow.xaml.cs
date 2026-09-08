using System.Windows;
using JussiMiniPos.Models;

namespace JussiMiniPos.Views;

/// <summary>
/// Read-only receipt for one past sale: what was bought, at what price, how it
/// was paid and the total.
/// </summary>
public partial class SaleDetailsWindow : Window
{
    public SaleDetailsWindow(Sale sale)
    {
        InitializeComponent();

        Sale = sale;
        WindowTitle = $"Myynti #{sale.Id}";

        DataContext = this;
    }

    public Sale Sale { get; }

    public string WindowTitle { get; }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
