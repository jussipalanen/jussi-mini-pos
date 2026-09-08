using System;
using System.Collections.Generic;
using System.Linq;
using JussiMiniPos.Models;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos.Services;

/// <summary>
/// Reads the catalogue out of the database. This is what the till uses;
/// <see cref="DemoCatalog"/> only ever feeds the seeder.
/// </summary>
public sealed class CatalogRepository(Database database)
{
    /// <summary>
    /// Categories in tree order — each root followed by its children — so the
    /// filter chips read top-down.
    /// </summary>
    public IReadOnlyList<Category> GetCategories(bool publicOnly = true)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT Id, Title, ParentId, IsPublic
            FROM Categories
            {(publicOnly ? "WHERE IsPublic = 1" : string.Empty)}
            ORDER BY COALESCE(ParentId, Id), Id;
            """;

        using var reader = command.ExecuteReader();

        var categories = new List<Category>();
        while (reader.Read())
        {
            categories.Add(new Category(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetInt64(3) != 0));
        }

        return categories;
    }

    /// <summary>
    /// Products with their categories attached. Two queries rather than one
    /// join, so a product with several categories stays a single row.
    /// </summary>
    public IReadOnlyList<Product> GetProducts(bool publicOnly = true)
    {
        using var connection = database.OpenConnection();

        var categoriesByProduct = ReadProductCategories(connection, publicOnly);
        var imagesByProduct = ReadProductImages(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT Id, Title, Description, PriceCents, SalePriceCents, FeatureImage, IsPublic
            FROM Products
            {(publicOnly ? "WHERE IsPublic = 1" : string.Empty)}
            ORDER BY Id;
            """;

        using var reader = command.ExecuteReader();

        var products = new List<Product>();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);

            products.Add(new Product(
                id,
                reader.GetString(1),
                reader.GetString(2),
                SalesRepository.FromCents(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : SalesRepository.FromCents(reader.GetInt64(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6) != 0,
                categoriesByProduct.TryGetValue(id, out var categories) ? categories : [])
            {
                Images = imagesByProduct.TryGetValue(id, out var images) ? images : [],
            });
        }

        return products;
    }

    /// <summary>The values a product edit or insert writes.</summary>
    public sealed record ProductDraft(
        string Title,
        string Description,
        decimal Price,
        decimal? SalePrice,
        bool IsPublic,
        string? FeatureImage,
        IReadOnlyList<int> CategoryIds,
        IReadOnlyList<string> Images);

    /// <summary>Adds a product and returns the id SQLite assigned.</summary>
    public int InsertProduct(ProductDraft draft)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO Products (Title, Description, PriceCents, SalePriceCents, FeatureImage, IsPublic)
            VALUES ($title, $description, $priceCents, $salePriceCents, $featureImage, $isPublic);
            SELECT last_insert_rowid();
            """;
        AddProductParameters(insert, draft);

        var id = (int)(long)insert.ExecuteScalar()!;

        WriteCategories(connection, transaction, id, draft.CategoryIds);
        WriteImages(connection, transaction, id, draft.Images);

        transaction.Commit();
        return id;
    }

    /// <summary>
    /// Saves an edited product and replaces its category links and gallery, in
    /// one transaction. The category ids are written in the order given, so the
    /// first one stays the product's primary category.
    /// </summary>
    public void UpdateProduct(int id, ProductDraft draft)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE Products
                   SET Title = $title,
                       Description = $description,
                       PriceCents = $priceCents,
                       SalePriceCents = $salePriceCents,
                       FeatureImage = $featureImage,
                       IsPublic = $isPublic
                 WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            AddProductParameters(command, draft);
            command.ExecuteNonQuery();
        }

        Execute(connection, transaction, "DELETE FROM ProductCategories WHERE ProductId = $id;", id);
        Execute(connection, transaction, "DELETE FROM ProductImages WHERE ProductId = $id;", id);

        WriteCategories(connection, transaction, id, draft.CategoryIds);
        WriteImages(connection, transaction, id, draft.Images);

        transaction.Commit();
    }

    /// <summary>
    /// Removes a product. Its image rows and category links go with it through
    /// ON DELETE CASCADE. Past sales are untouched: SaleItems keeps its own
    /// copy of the name and price and has no foreign key back to Products.
    /// The image *files* are the caller's to clean up.
    /// </summary>
    public void DeleteProduct(int id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Products WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // ---------- Categories ----------

    /// <summary>Adds a category and returns its new id.</summary>
    public int InsertCategory(string title, int? parentId, bool isPublic)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Categories (Title, ParentId, IsPublic) VALUES ($title, $parentId, $isPublic);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$parentId", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$isPublic", isPublic ? 1 : 0);
        return (int)(long)command.ExecuteScalar()!;
    }

    public void UpdateCategory(int id, string title, int? parentId, bool isPublic)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Categories SET Title = $title, ParentId = $parentId, IsPublic = $isPublic
             WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$parentId", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$isPublic", isPublic ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes a category. Product links go with it, leaving the products
    /// themselves alone. A category with children cannot be deleted: ParentId
    /// is ON DELETE RESTRICT, so SQLite refuses.
    /// </summary>
    public void DeleteCategory(int id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Categories WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Direct children of a category, for the delete guard.</summary>
    public int CountChildCategories(int id) =>
        Count("SELECT COUNT(*) FROM Categories WHERE ParentId = $id;", id);

    /// <summary>Products linked to a category, for the delete warning.</summary>
    public int CountProductsInCategory(int id) =>
        Count("SELECT COUNT(*) FROM ProductCategories WHERE CategoryId = $id;", id);

    /// <summary>Gallery paths per product, in SortOrder.</summary>
    private static Dictionary<int, List<string>> ReadProductImages(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ProductId, Path FROM ProductImages ORDER BY ProductId, SortOrder, Id;";

        using var reader = command.ExecuteReader();

        var result = new Dictionary<int, List<string>>();
        while (reader.Read())
        {
            var productId = reader.GetInt32(0);
            if (!result.TryGetValue(productId, out var list))
            {
                list = [];
                result[productId] = list;
            }

            list.Add(reader.GetString(1));
        }

        return result;
    }

    // ---------- Shared plumbing ----------

    private static void AddProductParameters(SqliteCommand command, ProductDraft draft)
    {
        command.Parameters.AddWithValue("$title", draft.Title);
        command.Parameters.AddWithValue("$description", draft.Description);
        command.Parameters.AddWithValue("$priceCents", SalesRepository.ToCents(draft.Price));
        command.Parameters.AddWithValue(
            "$salePriceCents",
            draft.SalePrice is { } sale ? SalesRepository.ToCents(sale) : DBNull.Value);
        command.Parameters.AddWithValue("$featureImage", (object?)draft.FeatureImage ?? DBNull.Value);
        command.Parameters.AddWithValue("$isPublic", draft.IsPublic ? 1 : 0);
    }

    private static void WriteCategories(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int productId,
        IReadOnlyList<int> categoryIds)
    {
        foreach (var categoryId in categoryIds.Distinct())
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO ProductCategories (ProductId, CategoryId) VALUES ($id, $categoryId);";
            insert.Parameters.AddWithValue("$id", productId);
            insert.Parameters.AddWithValue("$categoryId", categoryId);
            insert.ExecuteNonQuery();
        }
    }

    private static void WriteImages(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int productId,
        IReadOnlyList<string> images)
    {
        for (var i = 0; i < images.Count; i++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO ProductImages (ProductId, Path, SortOrder) VALUES ($id, $path, $sortOrder);";
            insert.Parameters.AddWithValue("$id", productId);
            insert.Parameters.AddWithValue("$path", images[i]);
            insert.Parameters.AddWithValue("$sortOrder", i);
            insert.ExecuteNonQuery();
        }
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        int id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private int Count(string sql, int id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Every image path the catalogue still refers to, feature images included.</summary>
    public IReadOnlyList<string> GetAllImagePaths()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Path FROM ProductImages
            UNION
            SELECT FeatureImage FROM Products WHERE FeatureImage IS NOT NULL;
            """;

        using var reader = command.ExecuteReader();

        var paths = new List<string>();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    /// <summary>What a product has sold so far, across every past receipt.</summary>
    public sealed record SalesSummary(int Lines, int Quantity, decimal Revenue);

    /// <summary>
    /// Totals from the receipt lines. These read SaleItems, which keeps its own
    /// copy of the price, so the figures stay what was actually charged even
    /// after the catalogue price changes.
    /// </summary>
    public SalesSummary GetSalesSummary(int productId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*), COALESCE(SUM(Quantity), 0), COALESCE(SUM(UnitPriceCents * Quantity), 0)
            FROM SaleItems WHERE ProductId = $id;
            """;
        command.Parameters.AddWithValue("$id", productId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new SalesSummary(0, 0, 0m);
        }

        return new SalesSummary(
            reader.GetInt32(0),
            reader.GetInt32(1),
            SalesRepository.FromCents(reader.GetInt64(2)));
    }

    /// <summary>How many times a product appears on past receipts.</summary>
    public int CountSoldLines(int productId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SaleItems WHERE ProductId = $id;";
        command.Parameters.AddWithValue("$id", productId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Every product's categories, keyed by product id. Ordered by the link
    /// table's rowid, which is insert order, so the main category the seeder
    /// wrote first stays first.
    /// </summary>
    private static Dictionary<int, List<Category>> ReadProductCategories(
        SqliteConnection connection,
        bool publicOnly)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT pc.ProductId, c.Id, c.Title, c.ParentId
            FROM ProductCategories pc
            JOIN Categories c ON c.Id = pc.CategoryId
            {(publicOnly ? "WHERE c.IsPublic = 1" : string.Empty)}
            ORDER BY pc.ProductId, pc.rowid;
            """;

        using var reader = command.ExecuteReader();

        var result = new Dictionary<int, List<Category>>();
        while (reader.Read())
        {
            var productId = reader.GetInt32(0);
            var category = new Category(
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3));

            if (!result.TryGetValue(productId, out var list))
            {
                list = [];
                result[productId] = list;
            }

            list.Add(category);
        }

        return result;
    }
}
