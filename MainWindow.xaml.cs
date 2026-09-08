using System.Windows;

namespace JussiMiniPos;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Kassa_Click(object sender, RoutedEventArgs e) => ShowPlaceholder("Kassa");

    private void Tuotteet_Click(object sender, RoutedEventArgs e) => ShowPlaceholder("Tuotteet");

    private void Myynti_Click(object sender, RoutedEventArgs e) => ShowPlaceholder("Myynti");

    private void Raportit_Click(object sender, RoutedEventArgs e) => ShowPlaceholder("Raportit");

    // TODO: replace with real navigation once the views exist.
    private void ShowPlaceholder(string view) =>
        MessageBox.Show(this, $"{view} ei ole vielä käytettävissä.", "JussiMiniPos",
            MessageBoxButton.OK, MessageBoxImage.Information);
}
