using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using JussiMiniPos.Services;
using Microsoft.Win32;

namespace JussiMiniPos.Views;

/// <summary>
/// Admin → Varmuuskopiot: back up now, restore, and the automatic daily
/// backup. Restoring replaces everything the till holds, so it asks first,
/// keeps a copy of the current state, and restarts the application.
/// </summary>
public partial class BackupWindow : Window
{
    private readonly BackupService _backups;
    private readonly OptionsRepository _options;
    private readonly Database _database;
    private BackupSettings _loaded;

    public BackupWindow(Database database, OptionsRepository options)
    {
        InitializeComponent();

        _database = database;
        _options = options;
        _backups = new BackupService(database);
        _loaded = BackupSettings.Default;

        DatabaseText = $"Tietokanta: {database.Path}";
        IsSupported = _backups.IsSupported;

        if (ViewErrors.Try(this, "Asetuksia ei voitu lukea.", () => _loaded = BackupSettings.Load(options)))
        {
            AutomaticBox.IsChecked = _loaded.Automatic;
            FolderBox.Text = _loaded.Folder;
            KeepBox.Text = _loaded.Keep.ToString(CultureInfo.CurrentCulture);
        }

        ListBackups();
        DataContext = this;
    }

    public string DatabaseText { get; }

    public bool IsSupported { get; }

    public bool IsServer => !IsSupported;

    /// <summary>"30.10.2026 17.05 · 1,2 Mt", newest first.</summary>
    public ObservableCollection<string> Backups { get; } = [];

    public bool HasNoBackups => Backups.Count == 0;

    private void ListBackups()
    {
        Backups.Clear();
        try
        {
            foreach (var path in BackupService.AutomaticBackups(FolderBox.Text.Trim()))
            {
                var file = new FileInfo(path);
                Backups.Insert(0, $"{file.LastWriteTime:dd.MM.yyyy HH.mm} · {file.Length / 1024.0 / 1024.0:N1} Mt · {file.Name}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreachable folder just lists nothing; saving says what is wrong.
        }
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Tallenna varmuuskopio",
            Filter = "Varmuuskopio (*.zip)|*.zip",
            FileName = BackupService.SuggestedFileName(DateTimeOffset.Now),
            InitialDirectory = Directory.Exists(FolderBox.Text.Trim()) ? FolderBox.Text.Trim() : string.Empty,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunAsync("Varmuuskopioidaan…", () =>
        {
            var info = _backups.Create(dialog.FileName);
            return $"Varmuuskopio tallennettu: {dialog.FileName}\n{info.Products} tuotetta, {info.Sales} myyntiä, {info.Images} kuvaa.";
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Valitse palautettava varmuuskopio",
            Filter = "Varmuuskopio (*.zip)|*.zip",
            InitialDirectory = Directory.Exists(FolderBox.Text.Trim()) ? FolderBox.Text.Trim() : string.Empty,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BackupService.BackupInfo? info = null;
        if (!await RunAsync("Tarkistetaan varmuuskopiota…", () =>
            {
                info = _backups.Inspect(dialog.FileName);
                return null;
            }))
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Palautetaanko varmuuskopio {Path.GetFileName(dialog.FileName)}?\n\n"
            + $"Otettu {info!.CreatedAt.ToLocalTime():dd.MM.yyyy HH.mm} (versio {info.AppVersion}): "
            + $"{info.Products} tuotetta, {info.Sales} myyntiä, {info.Images} kuvaa.\n\n"
            + "Kaikki tämän kassan nykyiset tuotteet, myynnit, käyttäjät ja asetukset korvataan varmuuskopion tiedoilla. "
            + "Nykyisestä tilasta otetaan ensin varmuuskopio. Sovellus käynnistyy palautuksen jälkeen uudelleen.",
            "Palauta varmuuskopio", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var safetyFolder = Directory.Exists(FolderBox.Text.Trim()) ? FolderBox.Text.Trim() : _database.DataDirectory;
        string? safety = null;
        if (!await RunAsync("Palautetaan…", () =>
            {
                safety = _backups.Restore(dialog.FileName, safetyFolder);
                return null;
            }))
        {
            return;
        }

        MessageBox.Show(this,
            $"Varmuuskopio palautettu. Aiempi tila on tallessa tiedostossa\n{safety}\n\nSovellus käynnistyy nyt uudelleen.",
            "Palauta varmuuskopio", MessageBoxButton.OK, MessageBoxImage.Information);

        // Every view, cached cart and signed-in user is from before the restore.
        if (Environment.ProcessPath is { } exe)
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        }

        Application.Current.Shutdown();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Valitse varmuuskopiokansio",
            InitialDirectory = Directory.Exists(FolderBox.Text.Trim()) ? FolderBox.Text.Trim() : string.Empty,
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            ListBackups();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            ShowStatus("Anna kansion koko polku, esimerkiksi D:\\Varmuuskopiot.", isError: true);
            return;
        }

        if (!int.TryParse(KeepBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var keep)
            || keep is < BackupSettings.MinKeep or > BackupSettings.MaxKeep)
        {
            ShowStatus($"Säilytettävien kopioiden määrän on oltava {BackupSettings.MinKeep}–{BackupSettings.MaxKeep}.", isError: true);
            return;
        }

        var settings = new BackupSettings(AutomaticBox.IsChecked == true, folder, keep);
        if (ViewErrors.Try(this, "Asetuksia ei voitu tallentaa.", () => settings.Save(_options, _loaded)))
        {
            _loaded = settings;
            ListBackups();
            ShowStatus("Asetukset tallennettu.", isError: false);
        }
    }

    /// <summary>
    /// Runs backup work off the UI thread, with the window disabled meanwhile,
    /// and shows what came of it. Returns false when it failed.
    /// </summary>
    private async Task<bool> RunAsync(string working, Func<string?> work)
    {
        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        ShowStatus(working, isError: false);
        try
        {
            var message = await Task.Run(work);
            if (message is not null)
            {
                ShowStatus(message, isError: false);
            }

            ListBackups();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Data.Common.DbException)
        {
            ShowStatus(ex is BackupService.NotABackupException ? ex.Message : $"Ei onnistunut: {ex.Message}", isError: true);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "Brush.Danger" : "Brush.TextMuted");
        StatusText.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
