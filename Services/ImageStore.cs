using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JussiMiniPos.Services;

/// <summary>
/// Keeps product images on disk next to the database, under an "images"
/// folder. The database stores relative paths like "images/ab12cd34.jpg", so
/// the whole JussiMiniPos folder can be copied or backed up as one unit.
/// </summary>
public sealed class ImageStore
{
    /// <summary>Extensions the file picker offers and the store accepts.</summary>
    public static readonly string[] SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    private const string FolderName = "images";

    private readonly string _rootPath;

    public ImageStore(Database database)
    {
        var directory = Path.GetDirectoryName(database.Path)
            ?? throw new InvalidOperationException("The database path has no directory.");

        _rootPath = Path.Combine(directory, FolderName);
    }

    /// <summary>Filter string for <c>OpenFileDialog</c>.</summary>
    public static string FileFilter =>
        "Kuvat|" + string.Join(";", SupportedExtensions.Select(e => "*" + e)) + "|Kaikki tiedostot|*.*";

    /// <summary>Absolute path of the images folder.</summary>
    public string RootPath => _rootPath;

    /// <summary>
    /// Copies a file into the store and returns the relative path to save in
    /// the database. The name is a fresh GUID, so importing the same picture
    /// twice never overwrites an image another product is using.
    /// </summary>
    public string Import(string sourceFile)
    {
        Directory.CreateDirectory(_rootPath);

        var extension = Path.GetExtension(sourceFile).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException($"Kuvamuotoa {extension} ei tueta.");
        }

        var fileName = Guid.NewGuid().ToString("N")[..12] + extension;
        File.Copy(sourceFile, Path.Combine(_rootPath, fileName), overwrite: false);

        return $"{FolderName}/{fileName}";
    }

    /// <summary>
    /// Absolute path for a stored image, or null when the row points at a file
    /// that is not there. Seeded demo rows and hand-edited paths can both do
    /// that, so callers must handle null rather than assume the file exists.
    /// </summary>
    public string? ResolveExisting(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var absolute = Path.Combine(
            Path.GetDirectoryName(_rootPath)!,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(absolute) ? absolute : null;
    }

    /// <summary>
    /// Deletes stored files no row points at any more, and reports how many
    /// went. Clearing or re-seeding the catalogue drops the rows but not the
    /// files, so without this the folder grows forever.
    /// </summary>
    public int DeleteUnreferenced(IEnumerable<string> referenced)
    {
        if (!Directory.Exists(_rootPath))
        {
            return 0;
        }

        var keep = referenced
            .Select(path => Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = Directory.EnumerateFiles(_rootPath)
            .Where(file => !keep.Contains(Path.GetFileName(file)))
            .ToList();

        Delete(orphans.Select(file => $"{FolderName}/{Path.GetFileName(file)}"));

        return orphans.Count;
    }

    /// <summary>
    /// Deletes stored files. Paths outside the images folder are ignored, so a
    /// hand-edited row cannot make the app delete something elsewhere on disk.
    /// </summary>
    public void Delete(IEnumerable<string?> relativePaths)
    {
        foreach (var path in relativePaths)
        {
            var absolute = ResolveExisting(path);
            if (absolute is null)
            {
                continue;
            }

            var inStore = Path.GetFullPath(absolute)
                .StartsWith(Path.GetFullPath(_rootPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            if (!inStore)
            {
                continue;
            }

            try
            {
                File.Delete(absolute);
            }
            catch (IOException)
            {
                // The picture is still open somewhere. Losing a stray file is
                // not worth failing the save the user asked for.
            }
        }
    }
}
