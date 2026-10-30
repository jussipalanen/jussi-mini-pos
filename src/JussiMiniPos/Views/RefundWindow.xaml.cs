using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Chooses what comes back from a sale, how the money goes back, and why.
/// Writing the refund is <see cref="Run"/>'s job once the dialog returns true;
/// the repository checks the quantities again inside its transaction.
/// </summary>
public partial class RefundWindow : Window, INotifyPropertyChanged
{
    private readonly Sale _sale;
    private readonly bool _roundCash;
    private PaymentMethod _method;
    private string _reason = string.Empty;

    public RefundWindow(Sale sale, User refundedBy, bool roundCash)
    {
        InitializeComponent();

        _sale = sale;
        _roundCash = roundCash;
        _method = sale.PaymentMethod;

        HeaderText = $"Palautus myynnistä #{sale.Id}";
        SaleText = $"{sale.SoldAt:dd.MM.yyyy HH:mm} · {sale.PaymentMethodName} · {sale.Total:N2} €";
        SignedInText = $"Palauttaja: {refundedBy.Display}";

        Lines = [.. sale.Items
            .Where(item => sale.RefundableQuantity(item) > 0)
            .Select(item => new LineChoice(item, sale.RefundableQuantity(item)))];

        foreach (var line in Lines)
        {
            line.PropertyChanged += (_, _) => Refresh();
        }

        DataContext = this;
    }

    public string HeaderText { get; }

    public string SaleText { get; }

    public string SignedInText { get; }

    public IReadOnlyList<LineChoice> Lines { get; }

    /// <summary>A way to give the money back, with its Finnish name for the chips.</summary>
    public sealed record MethodChoice(PaymentMethod Method)
    {
        public string Name => PaymentMethodNames.Finnish(Method);

        /// <summary>The chip's text is what screen readers and UI Automation read too.</summary>
        public override string ToString() => Name;
    }

    public IReadOnlyList<MethodChoice> Methods { get; } = [new(PaymentMethod.Card), new(PaymentMethod.Cash)];

    /// <summary>How the money goes back; the sale's own method to begin with.</summary>
    public PaymentMethod Method
    {
        get => _method;
        set
        {
            if (_method == value)
            {
                return;
            }

            _method = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    public string Reason
    {
        get => _reason;
        set
        {
            _reason = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRefund));
        }
    }

    public decimal Subtotal => Lines.Sum(line => line.LineTotal);

    /// <summary>What goes back: rounded to five cents for cash when the shop rounds.</summary>
    public decimal Total => Method == PaymentMethod.Cash ? Sale.CashTotal(Subtotal, _roundCash) : Subtotal;

    public bool IsRounded => Total != Subtotal;

    public string RoundingText => $"Pyöristetty 5 senttiin ({Total - Subtotal:+0.00;−0.00} €)";

    public string RefundButtonText => $"Palauta {Total:N2} €";

    public bool CanRefund => Subtotal > 0 && Reason.Trim().Length > 0;

    /// <summary>The lines and quantities chosen, once the dialog returns true.</summary>
    public IReadOnlyList<RefundRequestLine> Requested =>
        [.. Lines.Where(line => line.Quantity > 0).Select(line => new RefundRequestLine(line.Item.Id, line.Quantity))];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Opens the dialog for <paramref name="sale"/>, writes the refund, and
    /// tells the seller what to hand back. Returns true when a refund was
    /// stored. Who may is checked here, against the domain rule, not by
    /// whether a button was showing.
    /// </summary>
    public static bool Run(DependencyObject owner, Sale sale, User user, SalesRepository sales, TillSettings settings)
    {
        var window = Window.GetWindow(owner);
        if (!user.CanRefund(sale.SoldBy, ReportsRepository.LocalDate(sale.SoldAt) == ReportsRepository.Today))
        {
            MessageBox.Show(window,
                "Myyjä voi palauttaa vain omia tämän päivän myyntejään. Muut palautukset tekee esimies tai ylläpitäjä.",
                "JussiMiniPos", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (!sale.CanBeRefunded)
        {
            MessageBox.Show(window,
                sale.IsVoided ? "Mitätöidystä myynnistä ei voi tehdä palautusta." : "Myynnin kaikki tuotteet on jo palautettu.",
                "JussiMiniPos", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var dialog = new RefundWindow(sale, user, settings.RoundCash) { Owner = window };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        Refund? refund = null;
        if (!ViewErrors.Try(owner, "Palautusta ei voitu tallentaa.", () =>
            refund = sales.Refund(sale.Id, dialog.Requested, dialog.Method, settings.RoundCash, user.Username, dialog.Reason)))
        {
            return false;
        }

        MessageBox.Show(window,
            refund!.PaymentMethod == PaymentMethod.Cash
                ? $"Palautus tallennettu.\n\nAnna asiakkaalle {refund.Total:N2} € käteisenä."
                : $"Palautus tallennettu.\n\nHyvitä {refund.Total:N2} € asiakkaan maksukortille maksupäätteellä.",
            "Palautus", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(IsRounded));
        OnPropertyChanged(nameof(RoundingText));
        OnPropertyChanged(nameof(RefundButtonText));
        OnPropertyChanged(nameof(CanRefund));
    }

    private void All_Click(object sender, RoutedEventArgs e)
    {
        foreach (var line in Lines)
        {
            line.Quantity = line.Max;
        }
    }

    private void Increase_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is LineChoice line && line.Quantity < line.Max)
        {
            line.Quantity++;
        }
    }

    private void Decrease_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is LineChoice line && line.Quantity > 0)
        {
            line.Quantity--;
        }
    }

    private void Refund_Click(object sender, RoutedEventArgs e)
    {
        if (CanRefund)
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>One sold line and how many of it are being returned.</summary>
    public sealed class LineChoice(SaleItem item, int max) : INotifyPropertyChanged
    {
        private int _quantity;

        public SaleItem Item { get; } = item;

        /// <summary>What is left to return of this line.</summary>
        public int Max { get; } = max;

        public string MaxText => $"enintään {Max} kpl";

        public int Quantity
        {
            get => _quantity;
            set
            {
                var clamped = Math.Clamp(value, 0, Max);
                if (_quantity == clamped)
                {
                    return;
                }

                _quantity = clamped;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Quantity)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineTotal)));
            }
        }

        public decimal LineTotal => Item.UnitPrice * Quantity;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
