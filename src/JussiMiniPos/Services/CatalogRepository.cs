using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JussiMiniPos.Models;
using System.Data.Common;

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
    /// <param name="publicOnly">Leaves out rows kept out of the till.</param>
    /// <param name="ids">
    /// When given, only these products are read — the AI assistant searches in
    /// SQL and reads the handful of rows it found back through here, so the
    /// mapping from row to <see cref="Product"/> exists in one place.
    /// </param>
    public IReadOnlyList<Product> GetProducts(bool publicOnly = true, IReadOnlyCollection<int>? ids = null)
    {
        if (ids is { Count: 0 })
        {
            return [];
        }

        using var connection = database.OpenConnection();

        var categoriesByProduct = ReadProductCategories(connection, publicOnly);
        var imagesByProduct = ReadProductImages(connection);
        var vatRates = ReadVatRates(connection, activeOnly: false).ToDictionary(rate => rate.Id);

        using var command = connection.CreateCommand();

        var conditions = new List<string>();
        if (publicOnly)
        {
            conditions.Add("IsPublic = 1");
        }

        if (ids is not null)
        {
            // Ids cannot be parameterised as a list, so one parameter per id.
            var placeholders = ids.Select((_, i) => $"@id{i}").ToList();
            conditions.Add($"Id IN ({string.Join(", ", placeholders)})");

            foreach (var (id, placeholder) in ids.Zip(placeholders))
            {
                Database.AddParameter(command, placeholder, id);
            }
        }

        // A product without a rate cannot be written through here, but a row
        // inserted by hand could lack one; it is charged the highest rate, the
        // same choice the migration makes, so VAT is never under-reported.
        command.CommandText =
            $"""
            SELECT Id, Title, Description, PriceCents, SalePriceCents, FeatureImage, IsPublic,
                   COALESCE(VatRateId, (SELECT Id FROM VatRates ORDER BY BasisPoints DESC, Id LIMIT 1))
            FROM Products
            {(conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions))}
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
                Vat = vatRates[reader.GetInt32(7)],
            });
        }

        return products;
    }

    // ---------- VAT rates ----------

    /// <summary>The managed VAT rates, highest first.</summary>
    /// <param name="activeOnly">Leaves out rates retired from new choices.</param>
    public IReadOnlyList<VatRate> GetVatRates(bool activeOnly = false)
    {
        using var connection = database.OpenConnection();
        return ReadVatRates(connection, activeOnly);
    }

    /// <summary>Adds a rate and returns its id.</summary>
    public int InsertVatRate(string name, int basisPoints, bool isActive)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO VatRates (Name, BasisPoints, IsActive) VALUES (@name, @basisPoints, @isActive)
            RETURNING Id;
            """;
        Database.AddParameter(command, "@name", name);
        Database.AddParameter(command, "@basisPoints", basisPoints);
        Database.AddParameter(command, "@isActive", isActive ? 1 : 0);
        var id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        database.Audit.Record(connection, transaction, AuditActions.VatRateCreated,
            $"{name} {Vat.FormatPercent(basisPoints)}{(isActive ? string.Empty : ", ei valittavissa")}");

        transaction.Commit();
        return id;
    }

    /// <summary>
    /// Changes a rate. Every product on it is charged the new rate from the
    /// next sale on; sales already made keep the rate they recorded.
    /// </summary>
    public void UpdateVatRate(int id, string name, int basisPoints, bool isActive)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var before = ReadVatRates(connection, activeOnly: false, transaction).FirstOrDefault(rate => rate.Id == id);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE VatRates SET Name = @name, BasisPoints = @basisPoints, IsActive = @isActive WHERE Id = @id;";
        Database.AddParameter(command, "@id", id);
        Database.AddParameter(command, "@name", name);
        Database.AddParameter(command, "@basisPoints", basisPoints);
        Database.AddParameter(command, "@isActive", isActive ? 1 : 0);
        command.ExecuteNonQuery();

        if (before is not null)
        {
            var changes = new List<string>();
            if (before.Name != name)
            {
                changes.Add($"nimi {before.Name} → {name}");
            }

            if (before.BasisPoints != basisPoints)
            {
                changes.Add($"{before.PercentText} → {Vat.FormatPercent(basisPoints)}");
            }

            if (before.IsActive != isActive)
            {
                changes.Add(isActive ? "valittavissa" : "ei valittavissa");
            }

            database.Audit.Record(connection, transaction, AuditActions.VatRateUpdated,
                $"{before.Display}: {(changes.Count == 0 ? "ei muutoksia" : string.Join("; ", changes))}");
        }

        transaction.Commit();
    }

    /// <summary>Products charged at a rate, for the warning before changing it.</summary>
    public int CountProductsWithVatRate(int id) =>
        Count("SELECT COUNT(*) FROM Products WHERE VatRateId = @id;", id);

    private static List<VatRate> ReadVatRates(DbConnection connection, bool activeOnly, DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT Id, Name, BasisPoints, IsActive
            FROM VatRates
            {(activeOnly ? "WHERE IsActive = 1" : string.Empty)}
            ORDER BY BasisPoints DESC, Id;
            """;

        using var reader = command.ExecuteReader();

        var rates = new List<VatRate>();
        while (reader.Read())
        {
            rates.Add(new VatRate(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3) != 0));
        }

        return rates;
    }

    /// <summary>The values a product edit or insert writes.</summary>
    public sealed record ProductDraft(
        string Title,
        string Description,
        decimal Price,
        decimal? SalePrice,
        int VatRateId,
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
            INSERT INTO Products (Title, Description, PriceCents, SalePriceCents, FeatureImage, IsPublic, VatRateId)
            VALUES (@title, @description, @priceCents, @salePriceCents, @featureImage, @isPublic, @vatRateId)
            RETURNING Id;
            """;
        AddProductParameters(insert, draft);

        var id = Convert.ToInt32(insert.ExecuteScalar(), CultureInfo.InvariantCulture);

        WriteCategories(connection, transaction, id, draft.CategoryIds);
        WriteImages(connection, transaction, id, draft.Images);

        var created = ReadAuditSnapshot(connection, transaction, id);
        database.Audit.Record(connection, transaction, AuditActions.ProductCreated,
            $"{created.Title} ({id}): {AuditLog.Money(created.Price)}"
            + (created.SalePrice is { } sale ? $", tarjous {AuditLog.Money(sale)}" : string.Empty)
            + $", ALV {created.VatText}");

        transaction.Commit();
        return id;
    }

    /// <summary>The fields an audit entry about a product mentions, as stored.</summary>
    private sealed record AuditSnapshot(string Title, decimal Price, decimal? SalePrice, int? VatBasisPoints, bool IsPublic)
    {
        public string VatText => VatBasisPoints is { } rate ? Vat.FormatPercent(rate) : "–";

        public string SaleText => SalePrice is { } sale ? AuditLog.Money(sale) : "–";
    }

    private static AuditSnapshot ReadAuditSnapshot(DbConnection connection, DbTransaction transaction, int id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT p.Title, p.PriceCents, p.SalePriceCents, v.BasisPoints, p.IsPublic
            FROM Products p LEFT JOIN VatRates v ON v.Id = p.VatRateId
            WHERE p.Id = @id;
            """;
        Database.AddParameter(command, "@id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Tuotetta {id} ei löydy.");
        }

        return new AuditSnapshot(
            reader.GetString(0),
            SalesRepository.FromCents(reader.GetInt64(1)),
            reader.IsDBNull(2) ? null : SalesRepository.FromCents(reader.GetInt64(2)),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.GetInt64(4) != 0);
    }

    /// <summary>"hinta 2,50 € → 2,80 €; ALV 13,5 % → 25,5 %", or what else changed.</summary>
    private static string DescribeChanges(AuditSnapshot before, AuditSnapshot after)
    {
        var changes = new List<string>();
        if (before.Title != after.Title)
        {
            changes.Add($"nimi {before.Title} → {after.Title}");
        }

        if (before.Price != after.Price)
        {
            changes.Add($"hinta {AuditLog.Money(before.Price)} → {AuditLog.Money(after.Price)}");
        }

        if (before.SalePrice != after.SalePrice)
        {
            changes.Add($"tarjoushinta {before.SaleText} → {after.SaleText}");
        }

        if (before.VatBasisPoints != after.VatBasisPoints)
        {
            changes.Add($"ALV {before.VatText} → {after.VatText}");
        }

        if (before.IsPublic != after.IsPublic)
        {
            changes.Add(after.IsPublic ? "näytetään kassassa" : "piilotettu kassasta");
        }

        return changes.Count == 0 ? "kuvaus, kuvat tai kategoriat" : string.Join("; ", changes);
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

        var before = ReadAuditSnapshot(connection, transaction, id);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE Products
                   SET Title = @title,
                       Description = @description,
                       PriceCents = @priceCents,
                       SalePriceCents = @salePriceCents,
                       FeatureImage = @featureImage,
                       IsPublic = @isPublic,
                       VatRateId = @vatRateId
                 WHERE Id = @id;
                """;
            Database.AddParameter(command, "@id", id);
            AddProductParameters(command, draft);
            command.ExecuteNonQuery();
        }

        Execute(connection, transaction, "DELETE FROM ProductCategories WHERE ProductId = @id;", id);
        Execute(connection, transaction, "DELETE FROM ProductImages WHERE ProductId = @id;", id);

        WriteCategories(connection, transaction, id, draft.CategoryIds);
        WriteImages(connection, transaction, id, draft.Images);

        var after = ReadAuditSnapshot(connection, transaction, id);
        database.Audit.Record(connection, transaction, AuditActions.ProductUpdated,
            $"{before.Title} ({id}): {DescribeChanges(before, after)}");

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
        using var transaction = connection.BeginTransaction();

        var before = ReadAuditSnapshot(connection, transaction, id);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Products WHERE Id = @id;";
            Database.AddParameter(command, "@id", id);
            command.ExecuteNonQuery();
        }

        database.Audit.Record(connection, transaction, AuditActions.ProductDeleted,
            $"{before.Title} ({id}), {AuditLog.Money(before.Price)}");

        transaction.Commit();
    }

    // ---------- Categories ----------

    /// <summary>Adds a category and returns its new id.</summary>
    public int InsertCategory(string title, int? parentId, bool isPublic)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Categories (Title, ParentId, IsPublic) VALUES (@title, @parentId, @isPublic)
            RETURNING Id;
            """;
        Database.AddParameter(command, "@title", title);
        Database.AddParameter(command, "@parentId", (object?)parentId ?? DBNull.Value);
        Database.AddParameter(command, "@isPublic", isPublic ? 1 : 0);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void UpdateCategory(int id, string title, int? parentId, bool isPublic)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Categories SET Title = @title, ParentId = @parentId, IsPublic = @isPublic
             WHERE Id = @id;
            """;
        Database.AddParameter(command, "@id", id);
        Database.AddParameter(command, "@title", title);
        Database.AddParameter(command, "@parentId", (object?)parentId ?? DBNull.Value);
        Database.AddParameter(command, "@isPublic", isPublic ? 1 : 0);
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
        command.CommandText = "DELETE FROM Categories WHERE Id = @id;";
        Database.AddParameter(command, "@id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Direct children of a category, for the delete guard.</summary>
    public int CountChildCategories(int id) =>
        Count("SELECT COUNT(*) FROM Categories WHERE ParentId = @id;", id);

    /// <summary>Products linked to a category, for the delete warning.</summary>
    public int CountProductsInCategory(int id) =>
        Count("SELECT COUNT(*) FROM ProductCategories WHERE CategoryId = @id;", id);

    /// <summary>Gallery paths per product, in SortOrder.</summary>
    private static Dictionary<int, List<string>> ReadProductImages(DbConnection connection)
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

    private static void AddProductParameters(DbCommand command, ProductDraft draft)
    {
        Database.AddParameter(command, "@title", draft.Title);
        Database.AddParameter(command, "@description", draft.Description);
        Database.AddParameter(command, "@priceCents", SalesRepository.ToCents(draft.Price));
        Database.AddParameter(command,
            "@salePriceCents",
            draft.SalePrice is { } sale ? SalesRepository.ToCents(sale) : DBNull.Value);
        Database.AddParameter(command, "@featureImage", (object?)draft.FeatureImage ?? DBNull.Value);
        Database.AddParameter(command, "@isPublic", draft.IsPublic ? 1 : 0);
        Database.AddParameter(command, "@vatRateId", draft.VatRateId);
    }

    private static void WriteCategories(
        DbConnection connection,
        DbTransaction transaction,
        int productId,
        IReadOnlyList<int> categoryIds)
    {
        // SortOrder keeps the order the caller gave, so the first category
        // written stays the product's primary one.
        var sortOrder = 0;
        foreach (var categoryId in categoryIds.Distinct())
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO ProductCategories (ProductId, CategoryId, SortOrder)
                VALUES (@id, @categoryId, @sortOrder);
                """;
            Database.AddParameter(insert, "@id", productId);
            Database.AddParameter(insert, "@categoryId", categoryId);
            Database.AddParameter(insert, "@sortOrder", sortOrder++);
            insert.ExecuteNonQuery();
        }
    }

    private static void WriteImages(
        DbConnection connection,
        DbTransaction transaction,
        int productId,
        IReadOnlyList<string> images)
    {
        for (var i = 0; i < images.Count; i++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO ProductImages (ProductId, Path, SortOrder) VALUES (@id, @path, @sortOrder);";
            Database.AddParameter(insert, "@id", productId);
            Database.AddParameter(insert, "@path", images[i]);
            Database.AddParameter(insert, "@sortOrder", i);
            insert.ExecuteNonQuery();
        }
    }

    private static void Execute(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        int id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Database.AddParameter(command, "@id", id);
        command.ExecuteNonQuery();
    }

    private int Count(string sql, int id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Database.AddParameter(command, "@id", id);
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
    /// after the catalogue price changes. Voided sales are not counted.
    /// </summary>
    public SalesSummary GetSalesSummary(int productId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CAST(COUNT(*) AS INT),
                   CAST(COALESCE(SUM(i.Quantity), 0) AS INT),
                   CAST(COALESCE(SUM(i.UnitPriceCents * i.Quantity), 0) AS BIGINT)
            FROM SaleItems i JOIN Sales s ON s.Id = i.SaleId
            WHERE i.ProductId = @id AND s.VoidedAt IS NULL;
            """;
        Database.AddParameter(command, "@id", productId);

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
        command.CommandText = "SELECT COUNT(*) FROM SaleItems WHERE ProductId = @id;";
        Database.AddParameter(command, "@id", productId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Every product's categories, keyed by product id. Ordered by the link
    /// table's SortOrder, which is insert order, so the main category the
    /// seeder wrote first stays first.
    /// </summary>
    private static Dictionary<int, List<Category>> ReadProductCategories(
        DbConnection connection,
        bool publicOnly)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT pc.ProductId, c.Id, c.Title, c.ParentId
            FROM ProductCategories pc
            JOIN Categories c ON c.Id = pc.CategoryId
            {(publicOnly ? "WHERE c.IsPublic = 1" : string.Empty)}
            ORDER BY pc.ProductId, pc.SortOrder;
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
