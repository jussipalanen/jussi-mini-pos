using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using JussiMiniPos.Models;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos.Services;

/// <summary>
/// Backs up and restores a SQLite till: the database and its product images
/// in one zip file. A PostgreSQL database is backed up on the server with
/// pg_dump instead; see the README.
/// </summary>
/// <remarks>
/// The database is copied with SQLite's online backup API, never by copying
/// the file: a file copied while the till writes can be torn, and the backup
/// API gives a consistent snapshot even mid-sale. Restoring goes back through
/// the same API into the open database, so nothing has to replace a file the
/// application may still hold open. The Gemini key is left out on purpose:
/// it is a secret, and DPAPI ties it to this Windows account anyway.
/// </remarks>
public sealed class BackupService(Database database)
{
    /// <summary>The database's name inside the zip.</summary>
    public const string DatabaseEntry = "jussiminipos.db";

    /// <summary>What the backup is, for <see cref="Inspect"/> and for people opening the zip.</summary>
    public const string ManifestEntry = "backup.json";

    private const string ImagesFolder = "images";

    /// <summary>File names of automatic backups, oldest-first when sorted.</summary>
    private const string AutomaticPrefix = "JussiMiniPos-varmuuskopio-";

    /// <summary>The tables a JussiMiniPos database always has; a zip without them is refused.</summary>
    private static readonly string[] RequiredTables = ["Products", "Sales", "SaleItems", "Users", "Options"];

    /// <summary>Backups are for SQLite; a server database has its own tools.</summary>
    public bool IsSupported => database.Settings.Provider == DatabaseProvider.Sqlite;

    /// <summary>What a backup holds, read from its manifest and its database.</summary>
    public sealed record BackupInfo(DateTimeOffset CreatedAt, string AppVersion, long Products, long Sales, int Images);

    /// <summary>A file name for a backup taken now: "JussiMiniPos-varmuuskopio-2026-10-30-1705.zip".</summary>
    public static string SuggestedFileName(DateTimeOffset now) =>
        $"{AutomaticPrefix}{now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.zip";

    /// <summary>
    /// Writes a backup to <paramref name="zipPath"/>, replacing a file there,
    /// and records it in the audit log. The zip is written beside the target
    /// first and moved into place, so a failure never leaves half a backup
    /// under the real name.
    /// </summary>
    public BackupInfo Create(string zipPath)
    {
        RequireSupported();

        var work = TemporaryFolder();
        try
        {
            var copy = Path.Combine(work, DatabaseEntry);
            using (var source = (SqliteConnection)database.OpenConnection())
            using (var target = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                target.Open();
                source.BackupDatabase(target);
            }

            var info = Read(copy, DateTimeOffset.Now, AppInfo.Version, CountImages(ImagesRoot));

            var partial = zipPath + ".tmp";
            File.Delete(partial);
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(copy, DatabaseEntry, CompressionLevel.Optimal);

                if (Directory.Exists(ImagesRoot))
                {
                    foreach (var file in Directory.EnumerateFiles(ImagesRoot))
                    {
                        zip.CreateEntryFromFile(file, $"{ImagesFolder}/{Path.GetFileName(file)}", CompressionLevel.NoCompression);
                    }
                }

                using var manifest = new StreamWriter(zip.CreateEntry(ManifestEntry).Open());
                manifest.Write(JsonSerializer.Serialize(new Manifest(
                    info.CreatedAt.ToString("o", CultureInfo.InvariantCulture), info.AppVersion, info.Products, info.Sales, info.Images)));
            }

            File.Move(partial, zipPath, overwrite: true);

            database.Audit.Record(AuditActions.BackupCreated,
                $"{Path.GetFileName(zipPath)}: {info.Products} tuotetta, {info.Sales} myyntiä, {info.Images} kuvaa");
            return info;
        }
        finally
        {
            DeleteQuietly(work);
        }
    }

    /// <summary>
    /// Checks that <paramref name="zipPath"/> is a JussiMiniPos backup and says
    /// what it holds, without changing anything.
    /// </summary>
    /// <exception cref="NotABackupException">It is not one, with the Finnish reason.</exception>
    public BackupInfo Inspect(string zipPath)
    {
        var work = TemporaryFolder();
        try
        {
            return Extract(zipPath, work);
        }
        finally
        {
            DeleteQuietly(work);
        }
    }

    /// <summary>
    /// Replaces this till's database and images with a backup's. The current
    /// state is backed up into <paramref name="safetyFolder"/> first, so a
    /// restore can itself be undone, and the restored database is brought up
    /// to the current schema. The application must be restarted afterwards:
    /// everything it has read is from before.
    /// </summary>
    /// <returns>The safety backup's path.</returns>
    /// <exception cref="NotABackupException">The file is not a JussiMiniPos backup; nothing was changed.</exception>
    public string Restore(string zipPath, string safetyFolder)
    {
        RequireSupported();

        var work = TemporaryFolder();
        try
        {
            // Checked before anything is touched.
            var info = Extract(zipPath, work);

            Directory.CreateDirectory(safetyFolder);
            var safety = Path.Combine(safetyFolder, "ennen-palautusta-" + SuggestedFileName(DateTimeOffset.Now));
            Create(safety);

            using (var source = new SqliteConnection($"Data Source={Path.Combine(work, DatabaseEntry)};Pooling=False"))
            using (var target = (SqliteConnection)database.OpenConnection())
            {
                source.Open();
                source.BackupDatabase(target);
            }

            // Images are added, never deleted: a file the restored database
            // does not mention is harmless, a missing one is a lost picture.
            var images = Path.Combine(work, ImagesFolder);
            if (Directory.Exists(images))
            {
                Directory.CreateDirectory(ImagesRoot);
                foreach (var file in Directory.EnumerateFiles(images))
                {
                    File.Copy(file, Path.Combine(ImagesRoot, Path.GetFileName(file)), overwrite: true);
                }
            }

            database.EnsureCreated();
            database.Audit.Record(AuditActions.BackupRestored,
                $"{Path.GetFileName(zipPath)} ({info.CreatedAt:dd.MM.yyyy HH.mm}); edellinen tila: {Path.GetFileName(safety)}");
            return safety;
        }
        finally
        {
            DeleteQuietly(work);
        }
    }

    /// <summary>
    /// Takes the automatic backup when the last one is a day old or more, and
    /// keeps only the newest <paramref name="keep"/> in the folder. Returns
    /// the new file, or null when none was due.
    /// </summary>
    public string? RunScheduled(string folder, int keep, DateTimeOffset now)
    {
        if (!IsSupported)
        {
            return null;
        }

        Directory.CreateDirectory(folder);
        var existing = AutomaticBackups(folder);
        if (existing.Count > 0 && now - File.GetLastWriteTimeUtc(existing[^1]) < TimeSpan.FromHours(24))
        {
            return null;
        }

        var path = Path.Combine(folder, SuggestedFileName(now));
        Create(path);

        foreach (var old in AutomaticBackups(folder).SkipLast(Math.Max(1, keep)))
        {
            File.Delete(old);
        }

        return path;
    }

    /// <summary>The automatic backups in a folder, oldest first by their dated names.</summary>
    public static IReadOnlyList<string> AutomaticBackups(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, AutomaticPrefix + "*.zip").Order(StringComparer.Ordinal)]
            : [];

    private string ImagesRoot => Path.Combine(database.DataDirectory, ImagesFolder);

    private void RequireSupported()
    {
        if (!IsSupported)
        {
            throw new InvalidOperationException(
                "Varmuuskopiointi sovelluksesta koskee SQLite-tietokantaa. PostgreSQL varmuuskopioidaan palvelimella pg_dump-työkalulla.");
        }
    }

    /// <summary>Unpacks a backup into <paramref name="work"/> and checks it, returning what it holds.</summary>
    private static BackupInfo Extract(string zipPath, string work)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(DatabaseEntry)
                ?? throw new NotABackupException("Tiedostossa ei ole JussiMiniPos-tietokantaa.");

            entry.ExtractToFile(Path.Combine(work, DatabaseEntry));

            var images = 0;
            foreach (var image in zip.Entries.Where(e => e.FullName.StartsWith(ImagesFolder + "/", StringComparison.Ordinal)))
            {
                // Only plain file names: a name with a path in it could write outside the folder.
                var name = image.FullName[(ImagesFolder.Length + 1)..];
                if (name.Length == 0 || name != Path.GetFileName(name))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.Combine(work, ImagesFolder));
                image.ExtractToFile(Path.Combine(work, ImagesFolder, name));
                images++;
            }

            var manifest = zip.GetEntry(ManifestEntry) is { } manifestEntry
                ? JsonSerializer.Deserialize<Manifest>(new StreamReader(manifestEntry.Open()).ReadToEnd())
                : null;

            var createdAt = DateTimeOffset.TryParse(manifest?.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : entry.LastWriteTime;

            return Read(Path.Combine(work, DatabaseEntry), createdAt, manifest?.AppVersion ?? "?", images);
        }
        catch (Exception ex) when (ex is InvalidDataException or SqliteException or JsonException)
        {
            // A damaged zip or database says so in English; the user gets Finnish.
            throw new NotABackupException("Tiedosto ei ole kelvollinen JussiMiniPos-varmuuskopio tai se on vioittunut.", ex);
        }
    }

    /// <summary>Opens a copied database read-only and checks it is a JussiMiniPos one.</summary>
    private static BackupInfo Read(string path, DateTimeOffset createdAt, string version, int images)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();

        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA quick_check;";
            if (integrity.ExecuteScalar() as string != "ok")
            {
                throw new NotABackupException("Varmuuskopion tietokanta on vioittunut.");
            }
        }

        foreach (var table in RequiredTables)
        {
            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
            exists.Parameters.AddWithValue("@name", table);
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                throw new NotABackupException("Tiedosto ei ole JussiMiniPos-tietokanta: taulu " + table + " puuttuu.");
            }
        }

        return new BackupInfo(createdAt, version, Count(connection, "Products"), Count(connection, "Sales"), images);
    }

    /// <summary>Table names here are the compile-time constants above, never input.</summary>
    private static long Count(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int CountImages(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Count() : 0;

    private static string TemporaryFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "JussiMiniPos.Backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteQuietly(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A file that is not a usable backup, with the Finnish reason as its message.</summary>
    public sealed class NotABackupException(string message, Exception? inner = null)
        : IOException(message, inner);

    private sealed record Manifest(string CreatedAt, string AppVersion, long Products, long Sales, int Images);
}
