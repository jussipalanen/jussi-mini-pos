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
            SELECT Id, Title, Description, PriceCents, SalePriceCents, FeatureImage
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
                categoriesByProduct.TryGetValue(id, out var categories) ? categories : []));
        }

        return products;
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
