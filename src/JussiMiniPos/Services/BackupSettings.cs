using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using JussiMiniPos.Models;

namespace JussiMiniPos.Services;

/// <summary>
/// Automatic backups, as set in Admin → Varmuuskopiot: on or off, where to,
/// and how many to keep. On by default, because a till that has never been
/// backed up is one lost disk from losing its sales history.
/// </summary>
/// <param name="Automatic">Take a backup when the till starts and the last one is a day old.</param>
/// <param name="Folder">Where automatic backups go; ideally another disk or a synced folder.</param>
/// <param name="Keep">How many automatic backups to keep; older ones are deleted.</param>
public sealed record BackupSettings(bool Automatic, string Folder, int Keep)
{
    public const string AutomaticOption = "Backup.Automatic";
    public const string FolderOption = "Backup.Folder";
    public const string KeepOption = "Backup.Keep";

    public const int MinKeep = 1;
    public const int MaxKeep = 365;

    /// <summary>Documents, rather than beside the database, so that losing one does not lose both.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "JussiMiniPos-varmuuskopiot");

    public static BackupSettings Default => new(Automatic: true, DefaultFolder, Keep: 14);

    public static BackupSettings Load(OptionsRepository options)
    {
        var keep = int.TryParse(options.Get(KeepOption), NumberStyles.Integer, CultureInfo.InvariantCulture, out var k)
            && k is >= MinKeep and <= MaxKeep
            ? k
            : Default.Keep;

        return new(
            options.GetBool(AutomaticOption, Default.Automatic),
            options.Get(FolderOption) is { Length: > 0 } folder ? folder : DefaultFolder,
            keep);
    }

    /// <summary>Stores the settings with an audit entry naming what changed, or nothing when nothing did.</summary>
    public void Save(OptionsRepository options, BackupSettings before)
    {
        var values = new Dictionary<string, string>();
        var changes = new List<string>();

        if (Automatic != before.Automatic)
        {
            values[AutomaticOption] = Automatic ? "1" : "0";
            changes.Add(Automatic ? "automaattinen varmuuskopio päällä" : "automaattinen varmuuskopio pois");
        }

        if (Folder != before.Folder)
        {
            values[FolderOption] = Folder;
            changes.Add($"varmuuskopiokansio {before.Folder} → {Folder}");
        }

        if (Keep != before.Keep)
        {
            values[KeepOption] = Keep.ToString(CultureInfo.InvariantCulture);
            changes.Add($"säilytettäviä varmuuskopioita {before.Keep} → {Keep}");
        }

        if (changes.Count > 0)
        {
            options.SetAudited(values, AuditActions.SettingsChanged, string.Join("; ", changes));
        }
    }
}
