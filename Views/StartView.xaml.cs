using System;
using System.Windows;
using System.Windows.Controls;

namespace JussiMiniPos.Views;

/// <summary>
/// Landing view with the four main navigation tiles.
/// </summary>
public partial class StartView : UserControl
{
    public StartView()
    {
        InitializeComponent();
    }

    /// <summary>Raised with the destination the user picked.</summary>
    public event Action<AppView>? Navigate;

    private void Checkout_Click(object sender, RoutedEventArgs e) => Navigate?.Invoke(AppView.Checkout);

    private void Products_Click(object sender, RoutedEventArgs e) => Navigate?.Invoke(AppView.Products);

    private void Sales_Click(object sender, RoutedEventArgs e) => Navigate?.Invoke(AppView.Sales);

    private void Reports_Click(object sender, RoutedEventArgs e) => Navigate?.Invoke(AppView.Reports);
}
