using System.Collections.Generic;
using System.Linq;

namespace JussiMiniPos.Services;

/// <summary>
/// The hard-coded demo catalogue, used only as seed data. Once it has been
/// written to the database by <see cref="CatalogSeeder"/>, the application
/// reads products through <see cref="CatalogRepository"/> and never looks here
/// again — so editing a row below only affects a fresh or re-seeded database.
/// </summary>
public static class DemoCatalog
{
    /// <summary>One row of seed data. Not the runtime model; see Models/Product.</summary>
    public sealed record Row(int Id, string Name, string Category, decimal Price);

    public static IReadOnlyList<Row> Products { get; } =
    [
        new(1001, "Kahvi",               "Juomat",       2.50m),
        new(1002, "Espresso",            "Juomat",       2.80m),
        new(1003, "Cappuccino",          "Juomat",       3.60m),
        new(1004, "Tee",                 "Juomat",       2.20m),
        new(1005, "Appelsiinimehu",      "Juomat",       3.20m),
        new(1006, "Kivennäisvesi 0,5 l", "Juomat",       2.00m),
        new(1007, "Limonadi 0,5 l",      "Juomat",       2.90m),
        new(1008, "Kaurapuuro",          "Ruoat",        4.50m),
        new(1009, "Kanasalaatti",        "Ruoat",        8.90m),
        new(1010, "Lohikeitto",          "Ruoat",        9.80m),
        new(1011, "Juustohampurilainen", "Ruoat",        7.50m),
        new(1012, "Kasvispiirakka",      "Ruoat",        6.40m),
        new(1013, "Korvapuusti",         "Leivonnaiset", 3.20m),
        new(1014, "Croissant",           "Leivonnaiset", 2.80m),
        new(1015, "Mustikkapiirakka",    "Leivonnaiset", 4.10m),
        new(1016, "Suklaamuffinssi",     "Leivonnaiset", 3.50m),
        new(1017, "Ruisleipä 500 g",     "Ainekset",     2.60m),
        new(1018, "Voi 500 g",           "Ainekset",     4.90m),
        new(1019, "Kananmunat 10 kpl",   "Ainekset",     3.80m),
        new(1020, "Suklaapatukka",       "Makeiset",     1.90m),
    ];

    /// <summary>Root category names, in catalogue order.</summary>
    public static IReadOnlyList<string> Categories { get; } =
        Products.Select(p => p.Category).Distinct().ToList();
}
