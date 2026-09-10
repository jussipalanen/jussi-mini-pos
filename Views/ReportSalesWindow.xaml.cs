using System.Windows;
using JussiMiniPos.Models;

namespace JussiMiniPos.Views;

public partial class ReportSalesWindow : Window
{
    public ReportSalesWindow(DateTime day, IReadOnlyList<Sale> sales)
    {
        Heading = $"Myynnit {day:dd.MM.yyyy}";
        Sales = sales;
        InitializeComponent();
        DataContext = this;
    }

    public string Heading { get; }
    public IReadOnlyList<Sale> Sales { get; }
    public string Summary => Sales.Count == 0
        ? "Päivälle ei ole enää tallennettuja myyntejä."
        : $"{Sales.Count} myyntitapahtumaa · Kellonajat Suomen ajassa";

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Sale sale)
        {
            new SaleDetailsWindow(sale) { Owner = this }.ShowDialog();
        }
    }
}
