using System;
using System.Collections.Generic;
using System.Data;
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
                INSERT INTO SaleItems (SaleId, ProductId, Name, Category, UnitPriceCents, Quantity,
                                       VatRateBasisPoints, VatCents)
                VALUES (@saleId, @productId, @name, @category, @unitPriceCents, @quantity,
                        @vatBasisPoints, @vatCents);
                """;
            Database.AddParameter(insertItem, "@saleId", saleId);
            Database.AddParameter(insertItem, "@productId", item.ProductId);
            Database.AddParameter(insertItem, "@name", item.Name);
            Database.AddParameter(insertItem, "@category", item.Category);
            Database.AddParameter(insertItem, "@unitPriceCents", ToCents(item.UnitPrice));
            Database.AddParameter(insertItem, "@quantity", item.Quantity);
            Database.AddParameter(insertItem, "@vatBasisPoints", item.VatBasisPoints, DbType.Int32);
            Database.AddParameter(insertItem, "@vatCents",
                item.VatAmount is { } vat ? ToCents(vat) : null, DbType.Int64);
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
            SELECT Id, "DateTime", TotalCents, PaymentMethod, VoidedAt, VoidedBy, VoidReason
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
                Void = ReadVoid(reader, 4),
            });
        }

        return sales;
    }

    /// <summary>
    /// Voids a sale: it stays in the database with its lines, marked with
    /// when, by whom and why, and drops out of totals and reports. The audit
    /// entry is written in the same transaction, so a void cannot happen
    /// without its trace. There is deliberately no way to delete a sale.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The sale does not exist or has been voided already, so voiding twice
    /// cannot overwrite who did it first.
    /// </exception>
    public void VoidSale(long id, string voidedBy, string reason)
    {
        reason = reason.Trim();
        if (reason.Length == 0)
        {
            throw new ArgumentException("Mitätöinnille on annettava syy.", nameof(reason));
        }

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE Sales SET VoidedAt = @voidedAt, VoidedBy = @voidedBy, VoidReason = @reason
             WHERE Id = @id AND VoidedAt IS NULL
            RETURNING TotalCents;
            """;
        Database.AddParameter(command, "@id", id);
        Database.AddParameter(command, "@voidedAt", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
        Database.AddParameter(command, "@voidedBy", voidedBy);
        Database.AddParameter(command, "@reason", reason);

        if (command.ExecuteScalar() is not { } total)
        {
            throw new InvalidOperationException($"Myyntiä #{id} ei löydy tai se on jo mitätöity.");
        }

        database.Audit.Record(connection, transaction, AuditActions.SaleVoided,
            $"Myynti #{id} ({AuditLog.Money(FromCents(Convert.ToInt64(total, CultureInfo.InvariantCulture)))}). Syy: {reason}");

        transaction.Commit();
    }

    /// <summary>The void columns VoidedAt, VoidedBy and VoidReason, starting at <paramref name="first"/>.</summary>
    internal static SaleVoid? ReadVoid(DbDataReader reader, int first) =>
        reader.IsDBNull(first)
            ? null
            : new SaleVoid(
                ParseTimestamp(reader.GetString(first)),
                reader.IsDBNull(first + 1) ? string.Empty : reader.GetString(first + 1),
                reader.IsDBNull(first + 2) ? string.Empty : reader.GetString(first + 2));

    private static Dictionary<long, List<SaleItem>> ReadSaleItems(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SaleId, ProductId, Name, Category, UnitPriceCents, Quantity,
                   VatRateBasisPoints, VatCents
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

            items.Add(ReadItem(reader, 1));
        }

        return result;
    }

    /// <summary>
    /// One sale line from a reader whose columns, starting at
    /// <paramref name="first"/>, are ProductId, Name, Category,
    /// UnitPriceCents, Quantity, VatRateBasisPoints and VatCents. Shared with
    /// the reports so the two cannot read a line differently.
    /// </summary>
    internal static SaleItem ReadItem(DbDataReader reader, int first) =>
        new(reader.GetInt32(first),
            reader.GetString(first + 1),
            reader.GetString(first + 2),
            FromCents(reader.GetInt64(first + 3)),
            reader.GetInt32(first + 4),
            reader.IsDBNull(first + 5) ? null : reader.GetInt32(first + 5),
            reader.IsDBNull(first + 6) ? null : FromCents(reader.GetInt64(first + 6)));

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
