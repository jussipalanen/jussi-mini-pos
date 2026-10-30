using System.Windows;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Admin → Kassa: how the till behaves for everyone on this database. Reads
/// the settings when opened and writes them, with an audit entry, on save.
/// </summary>
public partial class TillSettingsWindow : Window
{
    private readonly OptionsRepository _options;
    private readonly TillSettings _loaded;

    public TillSettingsWindow(OptionsRepository options, TillSettings settings)
    {
        InitializeComponent();

        _options = options;
        _loaded = settings;

        RoundCashBox.IsChecked = settings.RoundCash;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = _loaded with { RoundCash = RoundCashBox.IsChecked == true };

        if (ViewErrors.Try(this, "Asetuksia ei voitu tallentaa.", () => settings.Save(_options, _loaded)))
        {
            DialogResult = true;
        }
    }
}
