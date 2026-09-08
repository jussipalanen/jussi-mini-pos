using System.Windows;
using JussiMiniPos.Views;

namespace JussiMiniPos;

/// <summary>
/// Application shell. Hosts one view at a time and handles navigation between
/// the start screen and the feature views.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ShowStartView();
    }

    private void ShowStartView()
    {
        var view = new StartView();
        view.Navigate += OnNavigate;
        ViewHost.Content = view;
    }

    private void ShowCheckoutView()
    {
        var view = new CheckoutView();
        view.Back += ShowStartView;
        ViewHost.Content = view;
    }

    private void OnNavigate(AppView destination)
    {
        if (destination == AppView.Checkout)
        {
            ShowCheckoutView();
            return;
        }

        // TODO: Products, Sales and Reports still need their own views.
        MessageBox.Show(
            this,
            $"{AppViewNames.Finnish(destination)} ei ole vielä käytettävissä.",
            "JussiMiniPos",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
