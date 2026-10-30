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
/// Payment flow for one checkout: pick a method, wait for the terminal or
/// count the cash, then confirm. The sale is written to the database only
/// once it is paid, so a cancelled or failed payment leaves nothing behind.
/// </summary>
public partial class PaymentView : UserControl, INotifyPropertyChanged
{
    /// <summary>How long the demo terminal pretends to talk to the bank.</summary>
    private static readonly TimeSpan TerminalDelay = TimeSpan.FromSeconds(2.4);

    /// <summary>Notes a customer is likely to hand over, for the quick-amount buttons.</summary>
    private static readonly decimal[] Notes = [5m, 10m, 20m, 50m, 100m];

    private readonly IReadOnlyList<CartLine> _cart;
    private readonly SalesRepository _repository;

    /// <summary>The signed-in seller, recorded on the sale.</summary>
    private readonly string _soldBy;

    private readonly bool _roundCash;

    private PaymentState _state = PaymentState.ChoosingMethod;
    private PaymentMethod _method = PaymentMethod.Card;
    private string _errorMessage = string.Empty;
    private string _cashReceivedText = string.Empty;
    private Sale? _savedSale;

    /// <param name="roundCash">Round cash totals to five cents; see <see cref="TillSettings"/>.</param>
    public PaymentView(IReadOnlyList<CartLine> cart, SalesRepository repository, string soldBy, bool roundCash)
    {
        InitializeComponent();

        // Snapshot the cart: the checkout view keeps its own live copy.
        _cart = [.. cart];
        _repository = repository;
        _soldBy = soldBy;
        _roundCash = roundCash;

        QuickAmounts = [new QuickAmount(CashTotal, "Tasaraha"), .. Notes
            .Select(note => Math.Ceiling(CashTotal / note) * note)
            .Where(amount => amount > CashTotal)
            .Distinct()
            .Take(3)
            .Select(amount => new QuickAmount(amount, $"{amount:N0} €"))];

        DataContext = this;
    }

    private enum PaymentState
    {
        ChoosingMethod,
        EnteringCash,
        Processing,
        Completed,
        Failed,
    }

    /// <summary>Raised when the user backs out without paying.</summary>
    public event Action? Cancelled;

    /// <summary>Raised once a paid sale has been stored and acknowledged.</summary>
    public event Action? Finished;

    /// <summary>The cart's total, which is what a card is charged.</summary>
    public decimal Total => _cart.Sum(line => line.LineTotal);

    /// <summary>What a cash customer pays: <see cref="Total"/>, rounded when the shop rounds.</summary>
    public decimal CashTotal => Sale.CashTotal(Total, _roundCash);

    public bool IsCashRounded => CashTotal != Total;

    /// <summary>"Pyöristys −0,02 €", shown under the cash total when it differs.</summary>
    public string CashRoundingText => $"Pyöristetty 5 senttiin ({CashTotal - Total:+0.00;−0.00} €)";

    /// <summary>One of the buttons that fills in the cash received.</summary>
    public sealed record QuickAmount(decimal Amount, string Label);

    /// <summary>"Tasaraha" first, then the next notes up from the total.</summary>
    public IReadOnlyList<QuickAmount> QuickAmounts { get; }

    /// <summary>The waiting screen's heading: a card waits for the terminal, cash only for the save.</summary>
    public string ProcessingTitle => _method == PaymentMethod.Card ? "Odotetaan maksua…" : "Tallennetaan myyntiä…";

    public string ProcessingHint => _method == PaymentMethod.Card ? "Seuraa maksupäätteen ohjeita." : string.Empty;

    public int ItemCount => _cart.Sum(line => line.Quantity);

    public string ItemSummary =>
        ItemCount == 1 ? "1 tuote ostoskorissa" : $"{ItemCount} tuotetta ostoskorissa";

    /// <summary>What was charged, once paid: rounded for cash, exact for card.</summary>
    public decimal PaidTotal => _savedSale?.Total ?? Total;

    /// <summary>Line shown under the total once the sale has an id.</summary>
    public string ReceiptSummary => _savedSale is null
        ? string.Empty
        : $"Myynti #{_savedSale.Id} · {PaymentMethodNames.Finnish(_savedSale.PaymentMethod)} · " +
          $"{_savedSale.SoldAt:dd.MM.yyyy HH:mm}";

    /// <summary>The change to hand back, shown big once a cash sale is stored.</summary>
    public decimal? SavedChange => _savedSale?.Change;

    public bool HasSavedChange => _savedSale?.IsCash == true;

    /// <summary>The cash handed over, as typed.</summary>
    public string CashReceivedText
    {
        get => _cashReceivedText;
        set
        {
            if (_cashReceivedText == value)
            {
                return;
            }

            _cashReceivedText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Change));
            OnPropertyChanged(nameof(HasChange));
            OnPropertyChanged(nameof(CashHint));
            OnPropertyChanged(nameof(IsCashShort));
            OnPropertyChanged(nameof(CanConfirmCash));
        }
    }

    private decimal? CashReceived =>
        MoneyInput.TryParse(_cashReceivedText, out var amount) && amount >= 0 ? decimal.Round(amount, 2) : null;

    /// <summary>Change due for what has been typed so far, when it covers the total.</summary>
    public decimal? Change => CashReceived is { } received && received >= CashTotal ? received - CashTotal : null;

    public bool HasChange => Change is not null;

    public bool CanConfirmCash => HasChange;

    public bool IsCashShort => CashHint.Length > 0;

    /// <summary>Why the amount typed cannot be accepted yet, or empty.</summary>
    public string CashHint => _cashReceivedText.Trim().Length == 0
        ? string.Empty
        : CashReceived switch
        {
            null => "Anna summa numeroina, esimerkiksi 20 tai 20,50.",
            { } received when received < CashTotal => $"Puuttuu vielä {CashTotal - received:N2} €.",
            _ => string.Empty,
        };

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

    public bool IsEnteringCash => _state == PaymentState.EnteringCash;

    public bool IsProcessing => _state == PaymentState.Processing;

    public bool IsCompleted => _state == PaymentState.Completed;

    public bool IsFailed => _state == PaymentState.Failed;

    /// <summary>Backing out is fine until the terminal is engaged or the sale is stored.</summary>
    public bool CanCancel => _state is PaymentState.ChoosingMethod or PaymentState.EnteringCash or PaymentState.Failed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void PayWithCard_Click(object sender, RoutedEventArgs e) =>
        await PayAsync(PaymentMethod.Card);

    private void PayWithCash_Click(object sender, RoutedEventArgs e)
    {
        SetState(PaymentState.EnteringCash);
        Dispatcher.BeginInvoke(() =>
        {
            CashReceivedBox.Focus();
            CashReceivedBox.SelectAll();
        });
    }

    private void QuickAmount_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is QuickAmount { Amount: var amount })
        {
            CashReceivedText = amount.ToString("N2");
            CashReceivedBox.Focus();
            CashReceivedBox.CaretIndex = CashReceivedBox.Text.Length;
        }
    }

    private async void ConfirmCash_Click(object sender, RoutedEventArgs e)
    {
        if (CanConfirmCash)
        {
            await PayAsync(PaymentMethod.Cash);
        }
    }

    private void BackToMethods_Click(object sender, RoutedEventArgs e) => SetState(PaymentState.ChoosingMethod);

    /// <summary>Tries the same method again, with the cash already counted.</summary>
    private async void Retry_Click(object sender, RoutedEventArgs e) => await PayAsync(_method);

    private async Task PayAsync(PaymentMethod method)
    {
        _method = method;
        OnPropertyChanged(nameof(ProcessingTitle));
        OnPropertyChanged(nameof(ProcessingHint));
        SetState(PaymentState.Processing);

        try
        {
            Sale sale;
            if (method == PaymentMethod.Cash)
            {
                sale = Sale.FromCartForCash(_cart, _roundCash, CashReceived ?? 0m);
            }
            else
            {
                // Stand-in for the card terminal round trip.
                await Task.Delay(TerminalDelay);
                sale = Sale.FromCart(_cart, method);
            }

            sale = sale with { SoldBy = _soldBy };
            _savedSale = await Task.Run(() => _repository.Save(sale));

            OnPropertyChanged(nameof(PaidTotal));
            OnPropertyChanged(nameof(ReceiptSummary));
            OnPropertyChanged(nameof(SavedChange));
            OnPropertyChanged(nameof(HasSavedChange));
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
        OnPropertyChanged(nameof(IsEnteringCash));
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
