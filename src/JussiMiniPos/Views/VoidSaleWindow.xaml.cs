using System.Windows;
using System.Windows.Controls;
using JussiMiniPos.Models;

namespace JussiMiniPos.Views;

/// <summary>
/// Asks why a sale is being voided. The reason is required: it is what the
/// audit log and the receipt keep, for whoever later asks what happened.
/// </summary>
public partial class VoidSaleWindow : Window
{
    public VoidSaleWindow(Sale sale, User voidedBy)
    {
        InitializeComponent();

        HeaderText = $"Mitätöi myynti #{sale.Id} ({sale.SoldAt:dd.MM.yyyy HH:mm}, {sale.Total:N2} €)?";
        SignedInText = $"Mitätöijä: {voidedBy.Display}";
        DataContext = this;

        ReasonBox.Focus();
    }

    public string HeaderText { get; }

    public string SignedInText { get; }

    /// <summary>The reason, trimmed, once the dialog returns true.</summary>
    public string Reason => ReasonBox.Text.Trim();

    private void Reason_Changed(object sender, TextChangedEventArgs e) => VoidButton.IsEnabled = Reason.Length > 0;

    private void Void_Click(object sender, RoutedEventArgs e)
    {
        if (Reason.Length > 0)
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
