using System;
using System.Globalization;
using JussiMiniPos.Models;

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
            VALUES ($dateTime, $totalCents, $paymentMethod);
            SELECT last_insert_rowid();
            """;
        insertSale.Parameters.AddWithValue("$dateTime", sale.SoldAt.ToString("o", CultureInfo.InvariantCulture));
        insertSale.Parameters.AddWithValue("$totalCents", ToCents(sale.Total));
        insertSale.Parameters.AddWithValue("$paymentMethod", sale.PaymentMethod.ToString());

        var saleId = (long)insertSale.ExecuteScalar()!;

        foreach (var item in sale.Items)
        {
            using var insertItem = connection.CreateCommand();
            insertItem.Transaction = transaction;
            insertItem.CommandText =
                """
                INSERT INTO SaleItems (SaleId, ProductId, Name, Category, UnitPriceCents, Quantity)
                VALUES ($saleId, $productId, $name, $category, $unitPriceCents, $quantity);
                """;
            insertItem.Parameters.AddWithValue("$saleId", saleId);
            insertItem.Parameters.AddWithValue("$productId", item.ProductId);
            insertItem.Parameters.AddWithValue("$name", item.Name);
            insertItem.Parameters.AddWithValue("$category", item.Category);
            insertItem.Parameters.AddWithValue("$unitPriceCents", ToCents(item.UnitPrice));
            insertItem.Parameters.AddWithValue("$quantity", item.Quantity);
            insertItem.ExecuteNonQuery();
        }

        transaction.Commit();

        return sale with { Id = saleId };
    }

    public static long ToCents(decimal amount) =>
        (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>Kept next to <see cref="ToCents"/> so the two stay in step when reading rows back.</summary>
    public static decimal FromCents(long cents) => cents / 100m;
}
