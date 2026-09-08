using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Payment flow for one checkout: pick a method, wait for the terminal, then
/// confirm. The sale is written to the database once the terminal reports
/// success, so a cancelled or failed payment leaves nothing behind.
/// </summary>
public partial class PaymentView : UserControl, INotifyPropertyChanged
{
    /// <summary>How long the demo terminal pretends to talk to the bank.</summary>
    private static readonly TimeSpan TerminalDelay = TimeSpan.FromSeconds(2.4);

    private readonly IReadOnlyList<CartLine> _cart;
    private readonly SalesRepository _repository;

    private PaymentState _state = PaymentState.ChoosingMethod;
    private string _errorMessage = string.Empty;
    private Sale? _savedSale;

    public PaymentView(IReadOnlyList<CartLine> cart, SalesRepository repository)
    {
        InitializeComponent();

        // Snapshot the cart: the checkout view keeps its own live copy.
        _cart = [.. cart];
        _repository = repository;

        DataContext = this;
    }

    private enum PaymentState
    {
        ChoosingMethod,
        Processing,
        Completed,
        Failed,
    }

    /// <summary>Raised when the user backs out without paying.</summary>
    public event Action? Cancelled;

    /// <summary>Raised once a paid sale has been stored and acknowledged.</summary>
    public event Action? Finished;

    public decimal Total => _cart.Sum(line => line.LineTotal);

    public int ItemCount => _cart.Sum(line => line.Quantity);

    public string ItemSummary =>
        ItemCount == 1 ? "1 tuote ostoskorissa" : $"{ItemCount} tuotetta ostoskorissa";

    /// <summary>Line shown under the total once the sale has an id.</summary>
    public string ReceiptSummary => _savedSale is null
        ? string.Empty
        : $"Myynti #{_savedSale.Id} · {PaymentMethodNames.Finnish(_savedSale.PaymentMethod)} · " +
          $"{_savedSale.SoldAt:dd.MM.yyyy HH:mm}";

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value)
            {
                return;
            }

            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsChoosingMethod => _state == PaymentState.ChoosingMethod;

    public bool IsProcessing => _state == PaymentState.Processing;

    public bool IsCompleted => _state == PaymentState.Completed;

    public bool IsFailed => _state == PaymentState.Failed;

    /// <summary>Backing out is fine until the terminal is engaged.</summary>
    public bool CanCancel => _state is PaymentState.ChoosingMethod or PaymentState.Failed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void PayWithCard_Click(object sender, RoutedEventArgs e) =>
        await PayAsync(PaymentMethod.Card);

    private async Task PayAsync(PaymentMethod method)
    {
        SetState(PaymentState.Processing);

        try
        {
            // Stand-in for the card terminal round trip.
            await Task.Delay(TerminalDelay);

            var sale = Sale.FromCart(_cart, method);
            _savedSale = await Task.Run(() => _repository.Save(sale));

            OnPropertyChanged(nameof(ReceiptSummary));
            SetState(PaymentState.Completed);
        }
        catch (Exception ex)
        {
            // The payment itself succeeded in the demo, so the only thing that
            // can fail here is storing the sale. Say so rather than implying
            // the card was declined.
            ErrorMessage = $"Myyntiä ei voitu tallentaa: {ex.Message}";
            SetState(PaymentState.Failed);
        }
    }

    private void SetState(PaymentState state)
    {
        _state = state;
        OnPropertyChanged(nameof(IsChoosingMethod));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(CanCancel));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private void Finish_Click(object sender, RoutedEventArgs e) => Finished?.Invoke();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
