using System.Linq;
using System.Windows;
using System.Windows.Controls;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// The managed list of VAT rates: add one, rename it, change its percentage,
/// or retire it from new choices. Rates are never deleted, so every product
/// keeps pointing at a rate that exists.
/// </summary>
public partial class VatRatesWindow : Window
{
    private readonly CatalogRepository _catalog;

    /// <summary>The rate being edited, or null while a new one is being entered.</summary>
    private VatRate? _editing;

    public VatRatesWindow(CatalogRepository catalog)
    {
        InitializeComponent();
        _catalog = catalog;
        Reload(selectId: null);
    }

    /// <summary>A rate as listed, with whether it can still be chosen.</summary>
    public sealed record RateRow(VatRate Rate, int ProductCount)
    {
        public string StatusText => (Rate.IsActive ? "Käytössä" : "Ei valittavissa")
            + $" · {ProductCount} tuotetta";
    }

    private void Reload(int? selectId)
    {
        RateRow[] rows = [];
        if (!ViewErrors.Try(this, "ALV-kantoja ei voitu lukea.", () =>
                rows = [.. _catalog.GetVatRates().Select(rate => new RateRow(rate, _catalog.CountProductsWithVatRate(rate.Id)))]))
        {
            return;
        }

        RateList.ItemsSource = rows;
        RateList.SelectedItem = rows.FirstOrDefault(row => row.Rate.Id == selectId);
        if (RateList.SelectedItem is null)
        {
            StartNew();
        }
    }

    private void RateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RateList.SelectedItem is not RateRow { Rate: var rate })
        {
            return;
        }

        _editing = rate;
        FormHeader.Text = $"Muokkaa: {rate.Display}";
        NameBox.Text = rate.Name;
        PercentBox.Text = rate.Percent.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("fi-FI"));
        ActiveBox.IsChecked = rate.IsActive;
        HideError();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        RateList.SelectedItem = null;
        StartNew();
        NameBox.Focus();
    }

    private void StartNew()
    {
        _editing = null;
        FormHeader.Text = "Uusi kanta";
        NameBox.Text = string.Empty;
        PercentBox.Text = string.Empty;
        ActiveBox.IsChecked = true;
        HideError();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Nimi ei voi olla tyhjä.");
            return;
        }

        if (!Vat.TryParsePercent(PercentBox.Text, out var basisPoints))
        {
            ShowError("Verokanta ei kelpaa. Anna prosentti väliltä 0–100, enintään kahdella desimaalilla, esimerkiksi 25,5.");
            return;
        }

        var isActive = ActiveBox.IsChecked == true;

        if (_editing is { } rate)
        {
            if (rate.BasisPoints != basisPoints && !ConfirmRateChange(rate, basisPoints))
            {
                return;
            }

            if (ViewErrors.Try(this, "ALV-kantaa ei voitu tallentaa.",
                    () => _catalog.UpdateVatRate(rate.Id, name, basisPoints, isActive)))
            {
                Reload(rate.Id);
            }

            return;
        }

        var id = 0;
        if (ViewErrors.Try(this, "ALV-kantaa ei voitu tallentaa.",
                () => id = _catalog.InsertVatRate(name, basisPoints, isActive)))
        {
            Reload(id);
        }
    }

    /// <summary>
    /// A changed percentage reaches every product on the rate at the next sale,
    /// which is the point of a rate change but easy to do by accident.
    /// </summary>
    private bool ConfirmRateChange(VatRate rate, int basisPoints)
    {
        var products = (RateList.SelectedItem as RateRow)?.ProductCount ?? 0;
        if (products == 0)
        {
            return true;
        }

        return MessageBox.Show(
            this,
            $"Kanta: {rate.Display}\nUusi verokanta: {Vat.FormatPercent(basisPoints)}\n\n" +
            $"Muutos koskee {products} tuotetta seuraavasta myynnistä alkaen. " +
            "Aiemmat myynnit säilyttävät tallennetun verokantansa.\n\nJatketaanko?",
            "Muuta ALV-kantaa",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
