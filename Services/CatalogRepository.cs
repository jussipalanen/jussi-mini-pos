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
            SELECT Id, Title, ParentId
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
                reader.IsDBNull(2) ? null : reader.GetInt32(2)));
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
                categoriesByProduct.TryGetValue(id, out var categories) ? categories : []));
        }

        return products;
    }

    /// <summary>
    /// Saves an edited product and replaces its category links, in one
    /// transaction. <paramref name="categoryIds"/> is written in the order
    /// given, and the first one becomes the product's primary category.
    /// </summary>
    public void UpdateProduct(
        int id,
        string title,
        string description,
        decimal price,
        decimal? salePrice,
        bool isPublic,
        IReadOnlyList<int> categoryIds)
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
                       IsPublic = $isPublic
                 WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$description", description);
            command.Parameters.AddWithValue("$priceCents", SalesRepository.ToCents(price));
            command.Parameters.AddWithValue(
                "$salePriceCents",
                salePrice is { } sale ? SalesRepository.ToCents(sale) : DBNull.Value);
            command.Parameters.AddWithValue("$isPublic", isPublic ? 1 : 0);
            command.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM ProductCategories WHERE ProductId = $id;";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        foreach (var categoryId in categoryIds.Distinct())
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO ProductCategories (ProductId, CategoryId) VALUES ($id, $categoryId);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$categoryId", categoryId);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Removes a product. Its images and category links go with it through ON
    /// DELETE CASCADE. Past sales are untouched: SaleItems keeps its own copy
    /// of the name and price and has no foreign key back to Products.
    /// </summary>
    public void DeleteProduct(int id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Products WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
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
