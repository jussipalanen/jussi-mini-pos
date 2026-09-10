using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using JussiMiniPos.Models;
using JussiMiniPos.Services;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos.Views;

public partial class ReportsView : UserControl, INotifyPropertyChanged
{
    private readonly ReportsRepository _reports;
    private DateTime? _from;
    private DateTime? _through;
    private int _selectedTab;
    private int _request;
    private bool _isLoading;
    private bool _active;

    public ReportsView(ReportsRepository reports)
    {
        _reports = reports;
        var today = ReportsRepository.Today;
        _from = new DateTime(today.Year, today.Month, 1);
        _through = today.ToDateTime(TimeOnly.MinValue);
        InitializeComponent();
        DataContext = this;
        Loaded += async (_, _) =>
        {
            _active = true;
            await LoadAsync();
        };
        Unloaded += (_, _) =>
        {
            _active = false;
            _request++;
        };
    }

    public event Action? Back;
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<string> Tabs { get; } = ["Päivittäin", "Tuotteittain", "Kategorioittain"];
    public SalesReport? Report { get; private set; }
    public string Notice { get; private set; } = string.Empty;
    public bool HasReport => Report is not null;
    public bool CanLoad => !_isLoading;
    public bool IsDaily => SelectedTab == 0;
    public bool IsProducts => SelectedTab == 1;
    public bool IsCategories => SelectedTab == 2;
    public string TableNotice => SelectedTab switch
    {
        0 => "Vain päivät, joilla on myyntejä. Avaa päivän tapahtumat Näytä myynnit -painikkeesta.",
        1 => "Myyntihetken nimet. Eri nimillä myyty tuote näkyy erillisillä riveillä. Lajittele sarakeotsikosta.",
        _ => "Myyntihetkellä tallennettu kategoria. Tuotteen muut kategoriat eivät sisälly erittelyyn.",
    };

    public DateTime? From
    {
        get => _from;
        set
        {
            if (_from == value) return;
            _from = value;
            FiltersChanged();
            OnPropertyChanged();
        }
    }

    public DateTime? Through
    {
        get => _through;
        set
        {
            if (_through == value) return;
            _through = value;
            FiltersChanged();
            OnPropertyChanged();
        }
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            _selectedTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDaily));
            OnPropertyChanged(nameof(IsProducts));
            OnPropertyChanged(nameof(IsCategories));
            OnPropertyChanged(nameof(TableNotice));
        }
    }

    private void FiltersChanged()
    {
        Report = null;
        Notice = "Näytä raportti valitulta aikaväliltä painamalla Näytä raportti.";
        NotifyReport();
    }

    private async Task LoadAsync()
    {
        if (_isLoading) return;
        Report = null;
        if (From is not { } from || Through is not { } through)
        {
            Notice = "Valitse alku- ja loppupäivä.";
            NotifyReport();
            return;
        }
        if (from.Date > through.Date)
        {
            Notice = "Alkupäivä ei saa olla loppupäivän jälkeen.";
            NotifyReport();
            return;
        }

        var request = ++_request;
        _isLoading = true;
        Notice = "Ladataan raporttia…";
        OnPropertyChanged(nameof(CanLoad));
        NotifyReport();
        try
        {
            var report = await Task.Run(() => _reports.GetReport(DateOnly.FromDateTime(from), DateOnly.FromDateTime(through)));
            if (!_active || request != _request) return;
            Report = report;
            Notice = report.SaleCount == 0
                ? "Ei myyntejä valitulla aikavälillä."
                : $"{from:dd.MM.yyyy}–{through:dd.MM.yyyy} · Tallennetut myynnit";
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            if (_active && request == _request)
            {
                Notice = "Raporttia ei voitu ladata. Tarkista tietokannan käyttöoikeudet ja yritä uudelleen.";
            }
        }
        finally
        {
            _isLoading = false;
            OnPropertyChanged(nameof(CanLoad));
            NotifyReport();
        }
    }

    private async void Preset_Click(object sender, RoutedEventArgs e)
    {
        var today = ReportsRepository.Today.ToDateTime(TimeOnly.MinValue);
        From = (((FrameworkElement)sender).Tag as string) switch
        {
            "week" => today.AddDays(-((int)today.DayOfWeek + 6) % 7),
            "month" => new DateTime(today.Year, today.Month, 1),
            _ => today,
        };
        Through = today;
        await LoadAsync();
    }

    private async void Load_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Date_ValidationError(object? sender, DatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
        if (sender is DatePicker picker) picker.SelectedDate = null;
        Notice = "Anna kelvollinen päivämäärä muodossa pp.kk.vvvv.";
        Report = null;
        NotifyReport();
    }

    private async void Day_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading || ((FrameworkElement)sender).DataContext is not ReportDay day) return;
        var request = ++_request;
        _isLoading = true;
        OnPropertyChanged(nameof(CanLoad));
        try
        {
            var sales = await Task.Run(() => _reports.GetDaySales(DateOnly.FromDateTime(day.Date)));
            if (_active && request == _request)
            {
                new ReportSalesWindow(day.Date, sales) { Owner = Window.GetWindow(this) }.ShowDialog();
            }
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            if (_active && request == _request)
            {
                Notice = "Päivän myyntejä ei voitu ladata. Yritä uudelleen.";
                OnPropertyChanged(nameof(Notice));
            }
        }
        finally
        {
            _isLoading = false;
            OnPropertyChanged(nameof(CanLoad));
        }
    }

    private static bool IsReadFailure(Exception ex) => ex is SqliteException or IOException
        or UnauthorizedAccessException or InvalidOperationException or OverflowException;

    private void Back_Click(object sender, RoutedEventArgs e) => Back?.Invoke();

    private void NotifyReport()
    {
        OnPropertyChanged(nameof(Report));
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(Notice));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
