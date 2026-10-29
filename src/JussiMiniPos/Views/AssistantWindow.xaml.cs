using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// The till's AI assistant: a question in Finnish, and the products from the
/// catalogue that answer it. Each suggestion can be dropped straight into the
/// cart the checkout view is holding — the window stays open while it happens,
/// so a customer's whole "and what goes with that?" fits in one visit.
/// </summary>
public partial class AssistantWindow : Window, INotifyPropertyChanged
{
    private readonly ShoppingAssistant _assistant;
    private readonly ImageStore _images;

    /// <summary>Cancels the request in flight when the window is closed.</summary>
    private CancellationTokenSource? _request;

    private string _questionText = string.Empty;
    private State _state = State.Idle;
    private string _reply = string.Empty;
    private string? _notice;
    private int _addedCount;

    public AssistantWindow(ShoppingAssistant assistant, ImageStore images)
    {
        InitializeComponent();

        _assistant = assistant;
        _images = images;

        DataContext = this;

        QuestionBox.Focus();
    }

    private enum State
    {
        Idle,
        Asking,
        Answered,
    }

    /// <summary>
    /// Raised for each product the user adds. The checkout view owns the cart,
    /// so it does the adding and this window never touches it.
    /// </summary>
    public event Action<Product>? AddToCartRequested;

    /// <summary>Ready-made questions, to save typing one out.</summary>
    public IReadOnlyList<Example> Examples { get; } =
    [
        new("Mitä sopii kahvin kanssa?"),
        new("Etsi halpa välipala"),
        new("Etsi alle 10 euron juomia"),
    ];

    public ObservableCollection<SuggestionRow> Suggestions { get; } = [];

    public string QuestionText
    {
        get => _questionText;
        set
        {
            if (_questionText == value)
            {
                return;
            }

            _questionText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAsk));
        }
    }

    /// <summary>An empty question has nothing to search for.</summary>
    public bool CanAsk => _state != State.Asking && _questionText.Trim().Length > 0;

    public bool IsIdle => _state == State.Idle;

    public bool IsAsking => _state == State.Asking;

    public bool HasAnswer => _state == State.Answered;

    /// <summary>The assistant's own words, above the products.</summary>
    public string Reply
    {
        get => _reply;
        private set
        {
            _reply = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReply));
        }
    }

    public bool HasReply => _reply.Length > 0;

    /// <summary>Set when the answer came without the AI: no key, or a failed call.</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            _notice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    /// <summary>
    /// Whether "add everything" is worth offering. For one suggestion it would
    /// just be that row's own button in a second place.
    /// </summary>
    public bool HasSeveralSuggestions => Suggestions.Count > 1;

    public string AddAllText => $"Lisää kaikki ostoskoriin ({Suggestions.Count})";

    public bool HasAdded => _addedCount > 0;

    public string AddedText => _addedCount == 1
        ? "Ostoskoriin lisättiin 1 tuote"
        : $"Ostoskoriin lisättiin {_addedCount} tuotetta";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>One of the example questions. A record so the chip style can bind its name.</summary>
    /// <param name="Title">The question, shown on the chip and typed into the box.</param>
    public sealed record Example(string Title);

    /// <summary>
    /// One suggested product in the output list: the catalogue row, the
    /// model's reason for it, its thumbnail, and whether it has been added to
    /// the cart yet.
    /// </summary>
    public sealed class SuggestionRow(ShoppingAssistant.Suggestion suggestion, ImageSource? thumbnail)
        : INotifyPropertyChanged
    {
        public Product Product { get; } = suggestion.Product;

        /// <summary>Empty when the suggestion is a plain search result.</summary>
        public string Reason { get; } = suggestion.Reason;

        public bool HasReason => Reason.Length > 0;

        /// <summary>Null when the product has no picture; the row shows a placeholder.</summary>
        public ImageSource? Thumbnail { get; } = thumbnail;

        /// <summary>
        /// How many of this product have been added from this answer. The
        /// button stays live after the first click, so this can climb.
        /// </summary>
        public int AddedCount { get; private set; }

        public bool HasAdded => AddedCount > 0;

        /// <summary>Empty until something has been added, so the row reads blank.</summary>
        public string AddedText => AddedCount == 0 ? string.Empty : $"Lisätty {AddedCount} kpl";

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Counts one more of this product into the cart.</summary>
        public void Add()
        {
            AddedCount++;
            OnPropertyChanged(nameof(AddedCount));
            OnPropertyChanged(nameof(HasAdded));
            OnPropertyChanged(nameof(AddedText));
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>Nothing left to answer to once the window is gone.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _request?.Cancel();
        base.OnClosed(e);
    }

    private async void Ask_Click(object sender, RoutedEventArgs e) => await AskAsync();

    /// <summary>
    /// Enter sends the question, as in any chat box. The box accepts returns so
    /// Shift+Enter can still break a line.
    /// </summary>
    private async void QuestionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return;
        }

        e.Handled = true;
        await AskAsync();
    }

    private async Task AskAsync()
    {
        if (!CanAsk)
        {
            return;
        }

        var question = QuestionText.Trim();

        using var request = new CancellationTokenSource();
        _request = request;

        SetState(State.Asking);

        try
        {
            var answer = await _assistant.AskAsync(question, request.Token);
            Show(answer);
        }
        catch (OperationCanceledException)
        {
            // The window was closed while the request was out.
        }
        catch (Exception ex)
        {
            // An exception out of an async void handler is unhandled and would
            // take the application — and the cashier's cart — down with it.
            // A failed question is not worth that.
            Show(new ShoppingAssistant.Answer(
                string.Empty,
                [],
                $"Avustaja ei vastannut: {ex.Message}"));
        }
        finally
        {
            _request = null;
        }
    }

    private void Show(ShoppingAssistant.Answer answer)
    {
        Suggestions.Clear();

        foreach (var suggestion in answer.Suggestions)
        {
            Suggestions.Add(new SuggestionRow(
                suggestion,
                Thumbnails.Load(_images, suggestion.Product.FeatureImage, decodeWidth: 96)));
        }

        Reply = answer.Reply;
        Notice = answer.Notice;

        OnPropertyChanged(nameof(HasSeveralSuggestions));
        OnPropertyChanged(nameof(AddAllText));

        SetState(State.Answered);
    }

    private void SetState(State state)
    {
        _state = state;
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsAsking));
        OnPropertyChanged(nameof(HasAnswer));
        OnPropertyChanged(nameof(CanAsk));
    }

    /// <summary>
    /// Picking an example types it into the box rather than asking it straight
    /// away, so it can be edited first. The selection is dropped again: the
    /// chips are shortcuts, not a filter that stays on.
    /// </summary>
    private void Example_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExampleList.SelectedItem is not Example example)
        {
            return;
        }

        QuestionText = example.Title;
        ExampleList.SelectedItem = null;

        QuestionBox.Focus();
        QuestionBox.CaretIndex = QuestionBox.Text.Length;
    }

    private void AddToCart_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SuggestionRow row)
        {
            Add(row);
        }
    }

    /// <summary>
    /// Puts one of everything suggested into the cart. Clicking it twice adds
    /// a second of each, the same as clicking each row's own button twice —
    /// there is no "already added" state to get in the way.
    /// </summary>
    private void AddAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Suggestions)
        {
            Add(row);
        }
    }

    private void Add(SuggestionRow row)
    {
        AddToCartRequested?.Invoke(row.Product);

        row.Add();
        _addedCount++;
        OnPropertyChanged(nameof(HasAdded));
        OnPropertyChanged(nameof(AddedText));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
