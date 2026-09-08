using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace JussiMiniPos;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>Prices are in euros and formatted the Finnish way: "12,50 €".</summary>
    private static readonly CultureInfo AppCulture = new("fi-FI");

    protected override void OnStartup(StartupEventArgs e)
    {
        CultureInfo.DefaultThreadCurrentCulture = AppCulture;
        CultureInfo.DefaultThreadCurrentUICulture = AppCulture;
        CultureInfo.CurrentCulture = AppCulture;
        CultureInfo.CurrentUICulture = AppCulture;

        // WPF bindings ignore CurrentCulture and use the element's Language,
        // which defaults to en-US. Without this, StringFormat would render
        // "12.50 €" regardless of the thread culture.
        //
        // This only reaches FrameworkElements. FrameworkContentElement already
        // registers its own metadata for Language and throws if you override it
        // too, so Run/Span keep the en-US separator: format currency on a
        // TextBlock rather than splitting it across Runs.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(AppCulture.IetfLanguageTag)));

        base.OnStartup(e);
    }
}
