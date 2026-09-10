using System;
using System.Collections.Generic;
using System.Globalization;
using JussiMiniPos.Models;
using System.Data.Common;

namespace JussiMiniPos.Services;

/// <summary>
/// Reads and writes completed sales.
///
/// Money is kept as an integer number of cents rather than REAL. SQLite has no
/// decimal type, and binary floating point cannot represent values like 0.10
/// exactly, so a column of REAL totals drifts once you start summing it for
/// reports. Cents stay exact and SUM() over them is still correct.
/// </summary>
public sealed class SalesRepository(Database database)
{
    /// <summary>
    /// Writes the sale and its lines in one transaction and returns the sale
    /// with the id the database assigned.
    /// </summary>
    public Sale Save(Sale sale)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var insertSale = connection.CreateCommand();
        insertSale.Transaction = transaction;
        insertSale.CommandText =
            """
            INSERT INTO Sales ("DateTime", TotalCents, PaymentMethod)
            VALUES (@dateTime, @totalCents, @paymentMethod)
            RETURNING Id;
            """;
        Database.AddParameter(insertSale, "@dateTime", sale.SoldAt.ToString("o", CultureInfo.InvariantCulture));
        Database.AddParameter(insertSale, "@totalCents", ToCents(sale.Total));
        Database.AddParameter(insertSale, "@paymentMethod", sale.PaymentMethod.ToString());

        var saleId = Convert.ToInt64(insertSale.ExecuteScalar(), CultureInfo.InvariantCulture);

        foreach (var item in sale.Items)
        {
            using var insertItem = connection.CreateCommand();
            insertItem.Transaction = transaction;
            insertItem.CommandText =
                """
                INSERT INTO SaleItems (SaleId, ProductId, Name, Category, UnitPriceCents, Quantity)
                VALUES (@saleId, @productId, @name, @category, @unitPriceCents, @quantity);
                """;
            Database.AddParameter(insertItem, "@saleId", saleId);
            Database.AddParameter(insertItem, "@productId", item.ProductId);
            Database.AddParameter(insertItem, "@name", item.Name);
            Database.AddParameter(insertItem, "@category", item.Category);
            Database.AddParameter(insertItem, "@unitPriceCents", ToCents(item.UnitPrice));
            Database.AddParameter(insertItem, "@quantity", item.Quantity);
            insertItem.ExecuteNonQuery();
        }

        transaction.Commit();

        return sale with { Id = saleId };
    }

    /// <summary>
    /// Every sale, newest first, with its lines attached. Two queries rather
    /// than one join, so a sale with several lines stays a single row.
    /// </summary>
    public IReadOnlyList<Sale> GetSales()
    {
        using var connection = database.OpenConnection();

        var itemsBySale = ReadSaleItems(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, "DateTime", TotalCents, PaymentMethod
            FROM Sales
            ORDER BY Id DESC;
            """;

        using var reader = command.ExecuteReader();

        var sales = new List<Sale>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);

            sales.Add(new Sale(
                ParseTimestamp(reader.GetString(1)),
                FromCents(reader.GetInt64(2)),
                ParsePaymentMethod(reader.GetString(3)),
                itemsBySale.TryGetValue(id, out var items) ? items : [])
            {
                Id = id,
            });
        }

        return sales;
    }

    /// <summary>
    /// Removes a sale. Its lines go with it through ON DELETE CASCADE.
    /// </summary>
    public void DeleteSale(long id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Sales WHERE Id = @id;";
        Database.AddParameter(command, "@id", id);
        command.ExecuteNonQuery();
    }

    private static Dictionary<long, List<SaleItem>> ReadSaleItems(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SaleId, ProductId, Name, Category, UnitPriceCents, Quantity
            FROM SaleItems
            ORDER BY SaleId, Id;
            """;

        using var reader = command.ExecuteReader();

        var result = new Dictionary<long, List<SaleItem>>();
        while (reader.Read())
        {
            var saleId = reader.GetInt64(0);
            if (!result.TryGetValue(saleId, out var items))
            {
                items = [];
                result[saleId] = items;
            }

            items.Add(new SaleItem(
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                FromCents(reader.GetInt64(4)),
                reader.GetInt32(5)));
        }

        return result;
    }

    /// <summary>
    /// Timestamps are written with "o". A row edited by hand might not be, so
    /// fall back to whatever parses rather than throwing on the whole list.
    /// </summary>
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    /// <summary>
    /// Stored as the enum name. An unknown one means a method this build does
    /// not have; show it as Card rather than failing to list the sale.
    /// </summary>
    private static PaymentMethod ParsePaymentMethod(string value) =>
        Enum.TryParse<PaymentMethod>(value, out var parsed) ? parsed : PaymentMethod.Card;

    public static long ToCents(decimal amount) =>
        (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>Kept next to <see cref="ToCents"/> so the two stay in step when reading rows back.</summary>
    public static decimal FromCents(long cents) => cents / 100m;
}
