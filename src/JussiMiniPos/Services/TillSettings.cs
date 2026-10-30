using System.Collections.Generic;
using JussiMiniPos.Models;

namespace JussiMiniPos.Services;

/// <summary>
/// How the till itself behaves, as set in Admin → Kassa. Stored in Options,
/// so every till on a shared database follows the same settings.
/// </summary>
/// <param name="RoundCash">
/// Round cash totals to the nearest five cents. On by default, as in Finnish
/// shops; card payments are never rounded.
/// </param>
public sealed record TillSettings(bool RoundCash)
{
    public const string RoundCashOption = "Till.RoundCash";

    /// <summary>What a till that has never been configured does.</summary>
    public static TillSettings Default { get; } = new(RoundCash: true);

    public static TillSettings Load(OptionsRepository options) =>
        new(options.GetBool(RoundCashOption, Default.RoundCash));

    /// <summary>
    /// Stores these settings with an audit entry naming what changed, or does
    /// nothing when nothing did.
    /// </summary>
    public void Save(OptionsRepository options, TillSettings before)
    {
        var changes = new List<string>();
        if (RoundCash != before.RoundCash)
        {
            changes.Add(RoundCash ? "käteinen pyöristetään 5 senttiin" : "käteistä ei pyöristetä");
        }

        if (changes.Count == 0)
        {
            return;
        }

        options.SetAudited(
            new Dictionary<string, string> { [RoundCashOption] = RoundCash ? "1" : "0" },
            AuditActions.SettingsChanged,
            string.Join("; ", changes));
    }
}
