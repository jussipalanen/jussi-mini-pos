using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos.Services;

/// <summary>
/// Fills the Categories / Products / ProductImages / ProductCategories tables
/// with the same demo catalogue the checkout view shows, so the tables have
/// something realistic in them. Driven from the command line: see
/// <see cref="CommandLine"/>.
/// </summary>
public static class CatalogSeeder
{
    /// <summary>Root categories, in the order the chips show them.</summary>
    private static readonly string[] RootCategories =
        ["Juomat", "Ruoat", "Leivonnaiset", "Ainekset", "Makeiset"];

    /// <summary>Subcategories, to exercise Categories.ParentId.</summary>
    private static readonly (string Title, string Parent)[] SubCategories =
    [
        ("Kuumat juomat", "Juomat"),
        ("Kylmät juomat", "Juomat"),
    ];

    /// <summary>
    /// Products that belong to a subcategory as well as their main one, so the
    /// many-to-many link table has rows with more than one category.
    /// </summary>
    private static readonly Dictionary<int, string> ExtraCategory = new()
    {
        [1001] = "Kuumat juomat",
        [1002] = "Kuumat juomat",
        [1003] = "Kuumat juomat",
        [1004] = "Kuumat juomat",
        [1005] = "Kylmät juomat",
        [1006] = "Kylmät juomat",
        [1007] = "Kylmät juomat",
    };

    /// <summary>
    /// Offer prices for a handful of products, so SalePriceCents is not NULL
    /// everywhere. The normal price comes from <see cref="DemoCatalog"/>.
    /// </summary>
    private static readonly Dictionary<int, decimal> SalePrices = new()
    {
        [1003] = 2.90m,
        [1011] = 5.90m,
        [1015] = 3.20m,
        [1020] = 1.50m,
    };

    private static readonly Dictionary<int, string> Descriptions = new()
    {
        [1001] = "Tumma paahto, suodatettu.",
        [1002] = "Yksi annos, väkevä.",
        [1003] = "Espresso ja vaahdotettu maito.",
        [1004] = "Musta tee, haudutettu.",
        [1005] = "Täysmehu, ei lisättyä sokeria.",
        [1006] = "Hiilihapotettu lähdevesi.",
        [1007] = "Virvoitusjuoma, jääkylmänä.",
        [1008] = "Kaurahiutaleista, tarjoillaan lämpimänä.",
        [1009] = "Grillattua kanaa ja tuoreita salaatteja.",
        [1010] = "Kermainen keitto, mukana ruisleipä.",
        [1011] = "Naudanlihapihvi, juusto ja talon kastike.",
        [1012] = "Kauden kasviksia voitaikinassa.",
        [1013] = "Kanelilla ja sokerilla, uunituore.",
        [1014] = "Voitaikinasarvi, paistettu aamulla.",
        [1015] = "Mustikoita ja murutaikinaa.",
        [1016] = "Tumma suklaa ja pehmeä sisus.",
        [1017] = "Hapanjuurella leivottu ruisleipä.",
        [1018] = "Suomalainen meijerivoi.",
        [1019] = "Vapaan kanan munia, kokoluokka M.",
        [1020] = "Maitosuklaata, 45 g.",
    };

    /// <summary>What a seed run did.</summary>
    public sealed record Result(int Categories, int Products, int Links);

    /// <summary>True when the catalogue tables already hold rows.</summary>
    public static bool HasData(Database database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM Products) + (SELECT COUNT(*) FROM Categories);";
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>Deletes every catalogue row. Sales are left alone.</summary>
    public static void Clear(Database database)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction,
            """
            DELETE FROM ProductCategories;
            DELETE FROM ProductImages;
            DELETE FROM Products;
            """);

        // Categories.ParentId is ON DELETE RESTRICT, and SQLite checks that per
        // row as the delete runs, so wiping the table in one statement trips
        // over a parent whose children are still there. Peel off the leaves
        // instead, which works at any nesting depth.
        while (DeleteLeafCategories(connection, transaction) > 0)
        {
        }

        Execute(connection, transaction,
            "DELETE FROM sqlite_sequence WHERE name IN ('Products', 'Categories', 'ProductImages');");

        transaction.Commit();
    }

    private static int DeleteLeafCategories(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM Categories
            WHERE Id NOT IN (SELECT ParentId FROM Categories WHERE ParentId IS NOT NULL);
            """;
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Writes the demo catalogue in one transaction. Product ids match
    /// <see cref="DemoCatalog"/> so the two stay in step.
    /// </summary>
    public static Result Seed(Database database)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var categoryIds = new Dictionary<string, long>();

        foreach (var title in RootCategories)
        {
            categoryIds[title] = InsertCategory(connection, transaction, title, parentId: null);
        }

        foreach (var (title, parent) in SubCategories)
        {
            categoryIds[title] = InsertCategory(connection, transaction, title, categoryIds[parent]);
        }

        var links = 0;

        foreach (var product in DemoCatalog.Products)
        {
            var salePrice = SalePrices.TryGetValue(product.Id, out var offer)
                ? SalesRepository.ToCents(offer)
                : (object)DBNull.Value;

            Execute(connection, transaction,
                """
                INSERT INTO Products (Id, Title, Description, FeatureImage, PriceCents, SalePriceCents, IsPublic)
                VALUES ($id, $title, $description, $featureImage, $priceCents, $salePriceCents, 1);
                """,
                ("$id", product.Id),
                ("$title", product.Name),
                ("$description", Descriptions.GetValueOrDefault(product.Id, string.Empty)),
                ("$featureImage", DBNull.Value),
                ("$priceCents", SalesRepository.ToCents(product.Price)),
                ("$salePriceCents", salePrice));

            var categories = new List<string> { product.Category };
            if (ExtraCategory.TryGetValue(product.Id, out var extra))
            {
                categories.Add(extra);
            }

            foreach (var category in categories)
            {
                Execute(connection, transaction,
                    """
                    INSERT INTO ProductCategories (ProductId, CategoryId)
                    VALUES ($productId, $categoryId);
                    """,
                    ("$productId", product.Id),
                    ("$categoryId", categoryIds[category]));
                links++;
            }
        }

        transaction.Commit();

        return new Result(categoryIds.Count, DemoCatalog.Products.Count, links);
    }

    private static long InsertCategory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string title,
        long? parentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO Categories (Title, ParentId, IsPublic) VALUES ($title, $parentId, 1);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$parentId", (object?)parentId ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

}
