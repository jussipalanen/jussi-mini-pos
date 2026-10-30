using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Printing;
using System.Windows;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Admin → Kassa: how the till behaves for everyone on this database, and
/// what its receipts say. Reads the settings when opened and writes them,
/// with an audit entry, on save.
/// </summary>
public partial class TillSettingsWindow : Window
{
    /// <summary>The printer list's first entry: ask which printer each time.</summary>
    private const string AskEachTime = "Kysy joka kerta";

    private readonly OptionsRepository _options;
    private readonly TillSettings _loaded;

    public TillSettingsWindow(OptionsRepository options, TillSettings settings)
    {
        InitializeComponent();

        _options = options;
        _loaded = settings;

        ShopNameBox.Text = settings.Shop.Name;
        BusinessIdBox.Text = settings.Shop.BusinessId;
        AddressBox.Text = settings.Shop.Address;
        PhoneBox.Text = settings.Shop.Phone;
        FooterBox.Text = settings.Shop.Footer;
        RoundCashBox.IsChecked = settings.RoundCash;
        AutoPrintBox.IsChecked = settings.AutoPrint;

        WidthList.ItemsSource = TillSettings.ReceiptWidths.Select(mm => $"{mm} mm").ToList();
        WidthList.SelectedIndex = Math.Max(0, TillSettings.ReceiptWidths.ToList().IndexOf(settings.ReceiptWidthMm));

        // A printer saved on another till, or since removed, stays listed so
        // that opening and saving does not quietly drop it.
        var printers = new List<string> { AskEachTime };
        try
        {
            printers.AddRange(ReceiptPrinter.InstalledPrinters());
        }
        catch (Exception ex) when (ex is PrintSystemException or Win32Exception or InvalidOperationException)
        {
            ShowError($"Tulostimia ei voitu lukea: {ex.Message}");
        }

        if (settings.HasPrinter && !printers.Contains(settings.PrinterName))
        {
            printers.Add(settings.PrinterName);
        }

        PrinterList.ItemsSource = printers;
        PrinterList.SelectedItem = settings.HasPrinter ? settings.PrinterName : AskEachTime;

        ShopNameBox.Focus();
    }

    private TillSettings? Read()
    {
        var businessId = BusinessIdBox.Text.Trim();
        if (BusinessId.Validate(businessId) is { } invalid)
        {
            ShowError(invalid);
            return null;
        }

        var printer = PrinterList.SelectedItem as string;
        return _loaded with
        {
            RoundCash = RoundCashBox.IsChecked == true,
            Shop = new ShopDetails(
                ShopNameBox.Text.Trim(),
                businessId,
                Normalize(AddressBox.Text),
                PhoneBox.Text.Trim(),
                Normalize(FooterBox.Text)),
            PrinterName = printer is null or AskEachTime ? string.Empty : printer,
            ReceiptWidthMm = TillSettings.ReceiptWidths[Math.Max(0, WidthList.SelectedIndex)],
            AutoPrint = AutoPrintBox.IsChecked == true,
        };
    }

    /// <summary>Line breaks as "\n" and no trailing blanks, so an unchanged text compares equal.</summary>
    private static string Normalize(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd())).Trim();

    private void TestPrint_Click(object sender, RoutedEventArgs e)
    {
        if (Read() is not { } settings)
        {
            return;
        }

        var sample = new Sale(DateTimeOffset.Now, 6.85m, PaymentMethod.Card,
        [
            SaleItem.Taxed(1, "Kahvi", "Testi", 4.50m, 1, 1350),
            SaleItem.Taxed(2, "Pulla", "Testi", 2.35m, 1, 1350),
        ]);

        List<ReceiptLine> lines = [.. Receipts.ForSale(sample, settings.Shop, isCopy: false)];
        lines.Insert(lines.FindIndex(line => line.Kind == ReceiptLineKind.Separator) + 1,
            new ReceiptLine(ReceiptLineKind.Banner, "TESTITULOSTE"));

        ReceiptPrinting.TryPrint(this, lines, settings, "JussiMiniPos testikuitti");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Read() is not { } settings)
        {
            return;
        }

        if (ViewErrors.Try(this, "Asetuksia ei voitu tallentaa.", () => settings.Save(_options, _loaded)))
        {
            DialogResult = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
