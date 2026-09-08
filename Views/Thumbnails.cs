using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Turns a stored image path into something WPF can show. Shared by the
/// product list, the editor and the details window so they decode a picture
/// once between them.
/// </summary>
public static class Thumbnails
{
    /// <summary>
    /// Keyed by relative path. Import names are fresh GUIDs, so a path always
    /// refers to the same bytes and a cached entry can never go stale.
    /// </summary>
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The decoded picture, or null when there is no path or the file is
    /// missing — callers show a placeholder for null rather than failing.
    /// </summary>
    public static ImageSource? Load(ImageStore store, string? relativePath, int decodeWidth = 200)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var key = $"{relativePath}|{decodeWidth}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var image = Decode(store.ResolveExisting(relativePath), decodeWidth);
        Cache[key] = image;
        return image;
    }

    /// <summary>Drops cached entries so deleted or replaced files are re-read.</summary>
    public static void Clear() => Cache.Clear();

    /// <summary>
    /// Decodes up front and closes the file. Without OnLoad the bitmap keeps
    /// the file open, and deleting the image later fails.
    /// </summary>
    private static ImageSource? Decode(string? absolutePath, int decodeWidth)
    {
        if (absolutePath is null)
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(absolutePath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // A corrupt or unreadable file shows the placeholder rather than
            // taking the view down.
            return null;
        }
    }
}
