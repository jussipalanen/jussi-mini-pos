using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Read-only view of one product: its pictures, prices, categories and how it
/// has sold so far.
/// </summary>
public partial class ProductDetailsWindow : Window
{
    public ProductDetailsWindow(Product product, ImageStore images, CatalogRepository catalog)
    {
        InitializeComponent();

        Product = product;
        WindowTitle = $"{product.Name} ({product.Id})";
        FeatureThumbnail = Thumbnails.Load(images, product.FeatureImage, decodeWidth: 320);

        Gallery = [.. product.Images.Select(path =>
            new GalleryItem(path, Thumbnails.Load(images, path)))];

        Sales = catalog.GetSalesSummary(product.Id);

        DataContext = this;
    }

    public Product Product { get; }

    public string WindowTitle { get; }

    public ImageSource? FeatureThumbnail { get; }

    public IReadOnlyList<GalleryItem> Gallery { get; }

    public CatalogRepository.SalesSummary Sales { get; }

    public bool HasNoGallery => Gallery.Count == 0;

    /// <summary>Falls back to a note rather than leaving the section blank.</summary>
    public string DescriptionText =>
        string.IsNullOrWhiteSpace(Product.Description) ? "Ei kuvausta." : Product.Description;

    /// <summary>One gallery picture, with its thumbnail resolved.</summary>
    public sealed record GalleryItem(string RelativePath, ImageSource? Thumbnail);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
