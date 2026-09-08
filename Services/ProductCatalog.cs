using System.Collections.Generic;
using System.Linq;
using JussiMiniPos.Models;

namespace JussiMiniPos.Services;

/// <summary>
/// Hard-coded demo catalogue. Replace with a real data source later.
/// </summary>
public static class ProductCatalog
{
    public static IReadOnlyList<Product> Products { get; } =
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

    /// <summary>Category names in catalogue order, for the filter chips.</summary>
    public static IReadOnlyList<string> Categories { get; } =
        Products.Select(p => p.Category).Distinct().ToList();
}
