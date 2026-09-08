using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

using JussiMiniPos.Models;
using JussiMiniPos.Services;
using Microsoft.Win32;

namespace JussiMiniPos.Views;

/// <summary>
/// Modal editor for one product, used both for editing an existing row and for
/// creating a new one. It validates and exposes the entered values as a draft;
/// writing that to the database is the caller's job.
///
/// Picked images are copied into the <see cref="ImageStore"/> straight away so
/// a thumbnail can be shown, and the ones that end up unused are cleaned up
/// when the dialog closes.
/// </summary>
public partial class ProductEditWindow : Window, INotifyPropertyChanged
{
    private readonly ImageStore _images;
    private readonly Product? _product;

    /// <summary>Files copied in during this session, to undo on cancel.</summary>
    private readonly List<string> _imported = [];

    /// <summary>Images dropped from the product, to delete once the save sticks.</summary>
    private readonly List<string> _removed = [];

    private ImageEntry? _featureImage;

    public ProductEditWindow(Product? product, IReadOnlyList<Category> categories, ImageStore images)
    {
        InitializeComponent();

        _product = product;
        _images = images;

        IsNew = product is null;
        WindowTitle = IsNew ? "Uusi tuote" : "Muokkaa tuotetta";
        HeaderText = IsNew ? "Uusi tuote" : $"{product!.Name} ({product.Id})";

        DataContext = this;

        TitleBox.Text = product?.Name ?? string.Empty;
        DescriptionBox.Text = product?.Description ?? string.Empty;
        PriceBox.Text = product?.Price.ToString("N2", CultureInfo.CurrentCulture) ?? string.Empty;
        SalePriceBox.Text = product?.SalePrice?.ToString("N2", CultureInfo.CurrentCulture) ?? string.Empty;
        PublicBox.IsChecked = product?.IsPublic ?? true;

        FeatureImage = product?.FeatureImage is { } feature ? Entry(feature) : null;

        foreach (var path in product?.Images ?? [])
        {
            GalleryImages.Add(Entry(path));
        }

        CategoryList.ItemsSource = categories;
        foreach (var category in categories.Where(c => product?.Categories.Any(pc => pc.Id == c.Id) == true))
        {
            CategoryList.SelectedItems.Add(category);
        }

        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    public bool IsNew { get; }

    public string WindowTitle { get; }

    public string HeaderText { get; }

    public ObservableCollection<ImageEntry> GalleryImages { get; } = [];

    public ImageEntry? FeatureImage
    {
        get => _featureImage;
        private set
        {
            _featureImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFeatureImage));
            OnPropertyChanged(nameof(HasNoFeatureImage));
            OnPropertyChanged(nameof(FeatureImageText));
        }
    }

    public bool HasFeatureImage => _featureImage is not null;

    public bool HasNoFeatureImage => _featureImage is null;

    public string FeatureImageText => _featureImage switch
    {
        null => "Ei pääkuvaa valittuna.",
        { Thumbnail: null } entry => $"{entry.RelativePath} (tiedostoa ei löydy)",
        var entry => entry.RelativePath,
    };

    public bool HasNoGalleryImages => GalleryImages.Count == 0;

    /// <summary>The values to save, filled in once <see cref="Validate"/> passes.</summary>
    public CatalogRepository.ProductDraft? Draft { get; private set; }

    /// <summary>
    /// Image files the saved draft no longer refers to. The caller deletes
    /// these after writing the draft, so a failed write leaves them in place.
    /// </summary>
    public IReadOnlyList<string> DiscardedImages { get; private set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A stored image plus a thumbnail, or null when the file is missing.</summary>
    public sealed class ImageEntry(string relativePath, ImageSource? thumbnail)
    {
        public string RelativePath { get; } = relativePath;

        public ImageSource? Thumbnail { get; } = thumbnail;
    }

    private ImageEntry Entry(string relativePath) =>
        new(relativePath, Thumbnails.Load(_images, relativePath));

    private string[] PickFiles(bool multiple)
    {
        var dialog = new OpenFileDialog
        {
            Title = multiple ? "Valitse kuvat" : "Valitse pääkuva",
            Filter = ImageStore.FileFilter,
            Multiselect = multiple,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
    }

    /// <summary>Copies a picked file into the store, reporting failures inline.</summary>
    private ImageEntry? ImportFile(string sourceFile)
    {
        try
        {
            var relative = _images.Import(sourceFile);
            _imported.Add(relative);
            return Entry(relative);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            ShowError($"Kuvaa {Path.GetFileName(sourceFile)} ei voitu lisätä: {ex.Message}");
            return null;
        }
    }

    private void PickFeatureImage_Click(object sender, RoutedEventArgs e)
    {
        if (PickFiles(multiple: false) is [var file] && ImportFile(file) is { } entry)
        {
            ReplaceFeature(entry);
        }
    }

    private void ClearFeatureImage_Click(object sender, RoutedEventArgs e) => ReplaceFeature(null);

    /// <summary>
    /// Swaps the feature image, marking the old file for deletion unless the
    /// gallery still shows it.
    /// </summary>
    private void ReplaceFeature(ImageEntry? entry)
    {
        if (_featureImage is { } old && GalleryImages.All(i => i.RelativePath != old.RelativePath))
        {
            _removed.Add(old.RelativePath);
        }

        FeatureImage = entry;
    }

    private void AddGalleryImages_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in PickFiles(multiple: true))
        {
            if (ImportFile(file) is { } entry)
            {
                GalleryImages.Add(entry);
            }
        }

        OnPropertyChanged(nameof(HasNoGalleryImages));
    }

    private void RemoveGalleryImage_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ImageEntry entry)
        {
            return;
        }

        GalleryImages.Remove(entry);

        if (_featureImage?.RelativePath != entry.RelativePath)
        {
            _removed.Add(entry.RelativePath);
        }

        OnPropertyChanged(nameof(HasNoGalleryImages));
    }

    /// <summary>Makes a gallery picture the feature image without re-importing it.</summary>
    private void PromoteToFeature_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ImageEntry entry)
        {
            ReplaceFeature(entry);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var error))
        {
            ShowError(error);
            return;
        }

        // Files the product no longer refers to. They are not deleted here:
        // the caller still has to write the draft, and a failed write would
        // leave the surviving rows pointing at files that were already gone.
        // The caller deletes these once the write succeeds.
        var keeping = GalleryImages.Select(i => i.RelativePath)
            .Append(_featureImage?.RelativePath ?? string.Empty)
            .ToHashSet();

        DiscardedImages = [.. _removed.Where(path => !keeping.Contains(path))];

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosed(EventArgs e)
    {
        // Nothing was saved, so every file copied in during this session is an
        // orphan. Keep the ones the product already had.
        if (DialogResult != true)
        {
            var existing = (_product?.Images ?? [])
                .Append(_product?.FeatureImage ?? string.Empty)
                .ToHashSet();

            _images.Delete(_imported.Where(path => !existing.Contains(path)));
        }

        base.OnClosed(e);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private bool Validate(out string error)
    {
        error = string.Empty;

        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            error = "Nimi ei voi olla tyhjä.";
            return false;
        }

        if (!TryParsePrice(PriceBox.Text, out var price) || price < 0)
        {
            error = "Hinta ei kelpaa. Anna luku, esimerkiksi 2,50.";
            return false;
        }

        decimal? salePrice = null;
        var saleText = SalePriceBox.Text.Trim();
        if (saleText.Length > 0)
        {
            if (!TryParsePrice(saleText, out var parsed) || parsed < 0)
            {
                error = "Tarjoushinta ei kelpaa. Jätä tyhjäksi, jos tuote ei ole tarjouksessa.";
                return false;
            }

            if (parsed >= price)
            {
                error = "Tarjoushinnan on oltava normaalihintaa pienempi.";
                return false;
            }

            salePrice = parsed;
        }

        var categoryIds = CategoryList.SelectedItems
            .Cast<Category>()
            .OrderBy(c => CategoryList.Items.IndexOf(c))
            .Select(c => c.Id)
            .ToList();

        if (categoryIds.Count == 0)
        {
            error = "Valitse vähintään yksi kategoria.";
            return false;
        }

        Draft = new CatalogRepository.ProductDraft(
            title,
            DescriptionBox.Text.Trim(),
            price,
            salePrice,
            PublicBox.IsChecked == true,
            _featureImage?.RelativePath,
            categoryIds,
            [.. GalleryImages.Select(i => i.RelativePath)]);

        return true;
    }

    /// <summary>
    /// Accepts both "2,50" and "2.50": the app runs in fi-FI, but a numeric
    /// keypad types a full stop.
    /// </summary>
    private static bool TryParsePrice(string text, out decimal value)
    {
        text = text.Trim().Replace('.', ',');
        return decimal.TryParse(
            text,
            NumberStyles.Number,
            CultureInfo.GetCultureInfo("fi-FI"),
            out value);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
