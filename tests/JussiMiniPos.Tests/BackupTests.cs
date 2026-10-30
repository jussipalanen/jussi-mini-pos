using System.IO.Compression;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Tests;

/// <summary>
/// Backups are for SQLite: a PostgreSQL server is backed up with its own
/// tools. Every file here lives in the test's own temporary folder.
/// </summary>
public sealed class BackupTests
{
    [Fact]
    public void A_backup_holds_the_database_images_and_a_manifest()
    {
        using var test = TestDatabase.Create(TestDatabase.Sqlite);
        Seed(test);
        var zip = Path.Combine(test.Database.DataDirectory, "kopio.zip");

        var created = new BackupService(test.Database).Create(zip);

        Assert.Equal((1L, 1L, 1), (created.Products, created.Sales, created.Images));
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Equal(
                [BackupService.ManifestEntry, "images/kahvi.jpg", BackupService.DatabaseEntry],
                archive.Entries.Select(e => e.FullName).Order());
        }

        var inspected = new BackupService(test.Database).Inspect(zip);
        Assert.Equal((1L, 1L, 1, AppInfo.Version), (inspected.Products, inspected.Sales, inspected.Images, inspected.AppVersion));
        Assert.Contains(new AuditLog(test.Database).GetEntries(), e => e.Action == AuditActions.BackupCreated);
    }

    /// <summary>
    /// Restoring brings back exactly what was backed up, keeps the state it
    /// replaced in a safety backup, and records that it happened.
    /// </summary>
    [Fact]
    public void A_restore_brings_back_the_backup_and_keeps_the_state_it_replaced()
    {
        using var test = TestDatabase.Create(TestDatabase.Sqlite);
        Seed(test);
        var backups = new BackupService(test.Database);
        var zip = Path.Combine(test.Database.DataDirectory, "kopio.zip");
        backups.Create(zip);

        // Afterwards: another sale, and the picture is lost.
        new SalesRepository(test.Database).Save(new Sale(DateTimeOffset.Now, 1.00m, PaymentMethod.Card,
            [SaleItem.Taxed(1, "Pulla", "Testi", 1.00m, 1, 1350)]));
        File.Delete(Path.Combine(test.Database.DataDirectory, "images", "kahvi.jpg"));

        var safetyFolder = Path.Combine(test.Database.DataDirectory, "turva");
        var safety = backups.Restore(zip, safetyFolder);

        Assert.Single(new SalesRepository(test.Database).GetSales());
        Assert.True(File.Exists(Path.Combine(test.Database.DataDirectory, "images", "kahvi.jpg")));
        Assert.Equal(2L, backups.Inspect(safety).Sales);
        Assert.Contains(new AuditLog(test.Database).GetEntries(),
            e => e.Action == AuditActions.BackupRestored && e.Details.Contains("kopio.zip"));
    }

    [Fact]
    public void A_file_that_is_not_a_backup_is_refused_and_nothing_changes()
    {
        using var test = TestDatabase.Create(TestDatabase.Sqlite);
        Seed(test);
        var backups = new BackupService(test.Database);
        var folder = test.Database.DataDirectory;

        var text = Path.Combine(folder, "teksti.zip");
        File.WriteAllText(text, "ei zip");

        var empty = Path.Combine(folder, "tyhja.zip");
        using (var archive = ZipFile.Open(empty, ZipArchiveMode.Create))
        {
            archive.CreateEntry("muu.txt");
        }

        var foreign = Path.Combine(folder, "vieras.zip");
        var foreignDb = Path.Combine(folder, "vieras.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={foreignDb};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE Muu (Id INTEGER);";
            create.ExecuteNonQuery();
        }

        using (var archive = ZipFile.Open(foreign, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(foreignDb, BackupService.DatabaseEntry);
        }

        foreach (var file in new[] { text, empty, foreign })
        {
            Assert.Throws<BackupService.NotABackupException>(() => backups.Restore(file, Path.Combine(folder, "turva")));
        }

        Assert.Single(new SalesRepository(test.Database).GetSales());
        Assert.False(Directory.Exists(Path.Combine(folder, "turva")));
    }

    [Fact]
    public void The_automatic_backup_runs_once_a_day_and_keeps_only_the_newest()
    {
        using var test = TestDatabase.Create(TestDatabase.Sqlite);
        Seed(test);
        var backups = new BackupService(test.Database);
        var folder = Path.Combine(test.Database.DataDirectory, "automaattiset");
        Directory.CreateDirectory(folder);

        // Three older ones, a week back and more.
        foreach (var day in new[] { 1, 2, 3 })
        {
            var old = Path.Combine(folder, BackupService.SuggestedFileName(new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero)));
            File.WriteAllText(old, "vanha");
            File.SetLastWriteTimeUtc(old, new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Utc));
        }

        var now = DateTimeOffset.Now;
        var first = backups.RunScheduled(folder, keep: 2, now);
        Assert.NotNull(first);
        Assert.Null(backups.RunScheduled(folder, keep: 2, now.AddHours(1)));

        var kept = BackupService.AutomaticBackups(folder);
        Assert.Equal(2, kept.Count);
        Assert.Equal(first, kept[^1]);
        Assert.EndsWith("2026-10-03-1200.zip", kept[0]);
    }

    [Fact]
    public void Backup_settings_default_to_daily_into_documents_and_are_audited()
    {
        using var test = TestDatabase.Create(TestDatabase.Sqlite);
        var options = new OptionsRepository(test.Database);

        var before = BackupSettings.Load(options);
        Assert.Equal(BackupSettings.Default, before);
        Assert.True(before.Automatic);
        Assert.Equal(14, before.Keep);

        var after = new BackupSettings(false, @"D:\Kopiot", 30);
        after.Save(options, before);

        Assert.Equal(after, BackupSettings.Load(options));
        Assert.Equal(
            $"automaattinen varmuuskopio pois; varmuuskopiokansio {before.Folder} → D:\\Kopiot; säilytettäviä varmuuskopioita 14 → 30",
            Assert.Single(new AuditLog(test.Database).GetEntries()).Details);
    }

    [Fact]
    public void A_server_database_is_not_backed_up_from_the_till()
    {
        using var test = TestDatabase.Create(TestDatabase.PostgreSql);
        var backups = new BackupService(test.Database);

        Assert.False(backups.IsSupported);
        Assert.Throws<InvalidOperationException>(() => backups.Create(Path.Combine(test.Database.DataDirectory, "x.zip")));
        Assert.Null(backups.RunScheduled(Path.Combine(test.Database.DataDirectory, "auto"), 14, DateTimeOffset.Now));
    }

    /// <summary>One product with a picture, and one sale.</summary>
    private static void Seed(TestDatabase test)
    {
        var catalogue = new CatalogRepository(test.Database);
        var category = catalogue.InsertCategory("Juomat", null, isPublic: true);
        Directory.CreateDirectory(Path.Combine(test.Database.DataDirectory, "images"));
        File.WriteAllBytes(Path.Combine(test.Database.DataDirectory, "images", "kahvi.jpg"), [1, 2, 3]);
        catalogue.InsertProduct(new CatalogRepository.ProductDraft(
            "Kahvi", "", 2.50m, null, test.VatRateId(), true, "images/kahvi.jpg", [category], []));

        new SalesRepository(test.Database).Save(new Sale(DateTimeOffset.Now, 2.50m, PaymentMethod.Card,
            [SaleItem.Taxed(1, "Kahvi", "Juomat", 2.50m, 1, 2550)]));
    }
}
