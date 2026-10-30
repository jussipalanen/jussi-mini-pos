using System.Collections.Generic;
using JussiMiniPos.Models;

namespace JussiMiniPos.Services;

/// <summary>
/// How the till itself behaves, as set in Admin → Kassa. Stored in Options,
/// so every till on a shared database follows the same settings — except the
/// printer, which is a name on this computer and so means something only
/// where it was chosen; a shared database still stores one, and a till that
/// does not have it asks which printer to use.
/// </summary>
/// <param name="RoundCash">
/// Round cash totals to the nearest five cents. On by default, as in Finnish
/// shops; card payments are never rounded.
/// </param>
/// <param name="Shop">Who the receipt says sold it.</param>
/// <param name="PrinterName">The Windows printer receipts go to; empty to ask each time.</param>
/// <param name="ReceiptWidthMm">Paper width: 80 mm or 58 mm thermal rolls.</param>
/// <param name="AutoPrint">Print the receipt as soon as a sale or refund is stored.</param>
public sealed record TillSettings(
    bool RoundCash,
    ShopDetails Shop,
    string PrinterName,
    int ReceiptWidthMm,
    bool AutoPrint)
{
    public const string RoundCashOption = "Till.RoundCash";
    public const string ShopNameOption = "Shop.Name";
    public const string BusinessIdOption = "Shop.BusinessId";
    public const string AddressOption = "Shop.Address";
    public const string PhoneOption = "Shop.Phone";
    public const string FooterOption = "Shop.ReceiptFooter";
    public const string PrinterOption = "Receipt.Printer";
    public const string WidthOption = "Receipt.WidthMm";
    public const string AutoPrintOption = "Receipt.AutoPrint";

    /// <summary>The paper widths offered; thermal rolls come in these two.</summary>
    public static IReadOnlyList<int> ReceiptWidths { get; } = [80, 58];

    /// <summary>What a till that has never been configured does.</summary>
    public static TillSettings Default { get; } = new(
        RoundCash: true,
        ShopDetails.Empty with { Footer = "Kiitos käynnistä!" },
        PrinterName: string.Empty,
        ReceiptWidthMm: 80,
        AutoPrint: false);

    public bool HasPrinter => PrinterName.Length > 0;

    public static TillSettings Load(OptionsRepository options)
    {
        var width = int.TryParse(options.Get(WidthOption), out var mm) && ReceiptWidths.Contains(mm)
            ? mm
            : Default.ReceiptWidthMm;

        return new(
            options.GetBool(RoundCashOption, Default.RoundCash),
            new ShopDetails(
                options.Get(ShopNameOption) ?? Default.Shop.Name,
                options.Get(BusinessIdOption) ?? Default.Shop.BusinessId,
                options.Get(AddressOption) ?? Default.Shop.Address,
                options.Get(PhoneOption) ?? Default.Shop.Phone,
                options.Get(FooterOption) ?? Default.Shop.Footer),
            options.Get(PrinterOption) ?? Default.PrinterName,
            width,
            options.GetBool(AutoPrintOption, Default.AutoPrint));
    }

    /// <summary>
    /// Stores these settings with an audit entry naming what changed, or does
    /// nothing when nothing did. Only the changed options are written.
    /// </summary>
    public void Save(OptionsRepository options, TillSettings before)
    {
        var values = new Dictionary<string, string>();
        var changes = new List<string>();

        void Text(string option, string now, string was, string description, bool showValues = true)
        {
            if (now == was)
            {
                return;
            }

            values[option] = now;
            changes.Add(showValues ? $"{description} {Shown(was)} → {Shown(now)}" : $"{description} muutettu");
        }

        void Flag(string option, bool now, bool was, string on, string off)
        {
            if (now == was)
            {
                return;
            }

            values[option] = now ? "1" : "0";
            changes.Add(now ? on : off);
        }

        Flag(RoundCashOption, RoundCash, before.RoundCash, "käteinen pyöristetään 5 senttiin", "käteistä ei pyöristetä");
        Text(ShopNameOption, Shop.Name, before.Shop.Name, "kaupan nimi");
        Text(BusinessIdOption, Shop.BusinessId, before.Shop.BusinessId, "Y-tunnus");
        Text(AddressOption, Shop.Address, before.Shop.Address, "osoite", showValues: false);
        Text(PhoneOption, Shop.Phone, before.Shop.Phone, "puhelin");
        Text(FooterOption, Shop.Footer, before.Shop.Footer, "kuitin alateksti", showValues: false);
        Text(PrinterOption, PrinterName, before.PrinterName, "kuittitulostin");
        Text(WidthOption, $"{ReceiptWidthMm}", $"{before.ReceiptWidthMm}", "kuitin leveys (mm)");
        Flag(AutoPrintOption, AutoPrint, before.AutoPrint, "kuitti tulostetaan automaattisesti", "kuittia ei tulosteta automaattisesti");

        if (changes.Count == 0)
        {
            return;
        }

        options.SetAudited(values, AuditActions.SettingsChanged, string.Join("; ", changes));
    }

    private static string Shown(string value) => value.Length == 0 ? "–" : value;
}
