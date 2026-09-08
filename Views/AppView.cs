namespace JussiMiniPos.Views;

/// <summary>
/// The destinations reachable from the start screen. Code uses these English
/// names; the Finnish labels the user sees live in the XAML and in
/// <see cref="AppViewNames"/>.
/// </summary>
public enum AppView
{
    Checkout,
    Products,
    Sales,
    Reports,
    Admin,
}

/// <summary>Finnish display names for <see cref="AppView"/>.</summary>
public static class AppViewNames
{
    public static string Finnish(AppView view) => view switch
    {
        AppView.Checkout => "Kassa",
        AppView.Products => "Tuotteet",
        AppView.Sales => "Myynti",
        AppView.Reports => "Raportit",
        AppView.Admin => "Admin",
        _ => view.ToString(),
    };
}
