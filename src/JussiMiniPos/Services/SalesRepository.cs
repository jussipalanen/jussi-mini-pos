using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
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
            INSERT INTO Sales ("DateTime", TotalCents, PaymentMethod, SoldBy, RoundingCents, CashReceivedCents)
            VALUES (@dateTime, @totalCents, @paymentMethod, @soldBy, @roundingCents, @cashReceivedCents)
            RETURNING Id;
            """;
        Database.AddParameter(insertSale, "@dateTime", sale.SoldAt.ToString("o", CultureInfo.InvariantCulture));
        Database.AddParameter(insertSale, "@totalCents", ToCents(sale.Total));
        Database.AddParameter(insertSale, "@paymentMethod", sale.PaymentMethod.ToString());
        Database.AddParameter(insertSale, "@soldBy", sale.SoldBy, DbType.String);
        Database.AddParameter(insertSale, "@roundingCents", ToCents(sale.Rounding));
        Database.AddParameter(insertSale, "@cashReceivedCents",
            sale.CashReceived is { } cash ? ToCents(cash) : null, DbType.Int64);

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
        var refundsBySale = ReadRefunds(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, "DateTime", TotalCents, PaymentMethod, VoidedAt, VoidedBy, VoidReason, SoldBy,
                   RoundingCents, CashReceivedCents
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
                SoldBy = reader.IsDBNull(7) ? null : reader.GetString(7),
                Rounding = FromCents(reader.GetInt64(8)),
                CashReceived = reader.IsDBNull(9) ? null : FromCents(reader.GetInt64(9)),
                Refunds = refundsBySale.TryGetValue(id, out var refunds) ? refunds : [],
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
    /// cannot overwrite who did it first; or money has been refunded against
    /// it, which a void would leave paying out for a sale that no longer counts.
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
               AND NOT EXISTS (SELECT 1 FROM Refunds r WHERE r.SaleId = Sales.Id)
            RETURNING TotalCents;
            """;
        Database.AddParameter(command, "@id", id);
        Database.AddParameter(command, "@voidedAt", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
        Database.AddParameter(command, "@voidedBy", voidedBy);
        Database.AddParameter(command, "@reason", reason);

        if (command.ExecuteScalar() is not { } total)
        {
            throw new InvalidOperationException(
                $"Myyntiä #{id} ei löydy, se on jo mitätöity tai siitä on tehty palautus.");
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
                   VatRateBasisPoints, VatCents, Id
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

            items.Add(ReadItem(reader, 1) with { Id = reader.GetInt64(8) });
        }

        return result;
    }

    /// <summary>Every refund with its lines, keyed by the sale it belongs to.</summary>
    private static Dictionary<long, List<Refund>> ReadRefunds(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.Id, r.SaleId, r."DateTime", r.RefundedBy, r.Reason, r.PaymentMethod,
                   r.TotalCents, r.RoundingCents,
                   ri.SaleItemId, i.Name, i.UnitPriceCents, ri.Quantity, i.VatRateBasisPoints, ri.VatCents
            FROM Refunds r
            JOIN RefundItems ri ON ri.RefundId = r.Id
            JOIN SaleItems i ON i.Id = ri.SaleItemId
            ORDER BY r.Id, ri.Id;
            """;

        using var reader = command.ExecuteReader();

        var result = new Dictionary<long, List<Refund>>();
        Refund? current = null;
        List<RefundLine> lines = [];
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (current?.Id != id)
            {
                lines = [];
                current = new Refund(
                    id,
                    reader.GetInt64(1),
                    ParseTimestamp(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetString(4),
                    ParsePaymentMethod(reader.GetString(5)),
                    FromCents(reader.GetInt64(6)),
                    FromCents(reader.GetInt64(7)),
                    lines);

                if (!result.TryGetValue(current.SaleId, out var refunds))
                {
                    refunds = [];
                    result[current.SaleId] = refunds;
                }

                refunds.Add(current);
            }

            lines.Add(new RefundLine(
                reader.GetInt64(8),
                reader.GetString(9),
                FromCents(reader.GetInt64(10)),
                reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12),
                reader.IsDBNull(13) ? null : FromCents(reader.GetInt64(13))));
        }

        return result;
    }

    /// <summary>A sold line as the refund check needs it: what was sold and what has come back.</summary>
    private sealed record RefundableLine(
        long Id, string Name, long UnitPriceCents, int Quantity, int? VatBasisPoints, long? VatCents,
        int RefundedQuantity, long RefundedVatCents)
    {
        public int Remaining => Quantity - RefundedQuantity;
    }

    /// <summary>
    /// Gives money back against a sale for the chosen lines, as a refund
    /// record of its own, and returns it. The sale is not edited.
    /// </summary>
    /// <remarks>
    /// Everything is checked and written in one transaction, against what has
    /// already been refunded, so two tills cannot both return the last item:
    /// the first statement touches the sale's row, which holds a row lock under
    /// PostgreSQL until the commit, and SQLite's write transaction is exclusive
    /// anyway. Returning a line's last unit gives back exactly the VAT the line
    /// recorded less what earlier refunds took, so a fully returned line nets
    /// to zero VAT; a partial return works its VAT out of its own amount.
    /// </remarks>
    /// <param name="roundCash">Round a cash refund to five cents, as the sale would have been.</param>
    /// <exception cref="ArgumentException">No lines, or no reason.</exception>
    /// <exception cref="InvalidOperationException">
    /// The sale is missing or voided, a line is not the sale's, or more is
    /// asked back than is left to return.
    /// </exception>
    public Refund Refund(
        long saleId,
        IReadOnlyList<RefundRequestLine> lines,
        PaymentMethod method,
        bool roundCash,
        string refundedBy,
        string reason)
    {
        reason = reason.Trim();
        if (reason.Length == 0)
        {
            throw new ArgumentException("Palautukselle on annettava syy.", nameof(reason));
        }

        var requested = lines
            .Where(line => line.Quantity > 0)
            .GroupBy(line => line.SaleItemId)
            .Select(group => (SaleItemId: group.Key, Quantity: group.Sum(line => line.Quantity)))
            .ToList();

        if (requested.Count == 0)
        {
            throw new ArgumentException("Valitse vähintään yksi palautettava tuote.", nameof(lines));
        }

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var lockSale = connection.CreateCommand())
        {
            lockSale.Transaction = transaction;
            lockSale.CommandText =
                """
                UPDATE Sales SET VoidedAt = VoidedAt
                 WHERE Id = @id AND VoidedAt IS NULL
                RETURNING Id;
                """;
            Database.AddParameter(lockSale, "@id", saleId);
            if (lockSale.ExecuteScalar() is null)
            {
                throw new InvalidOperationException($"Myyntiä #{saleId} ei löydy tai se on mitätöity.");
            }
        }

        var sold = ReadRefundableLines(connection, transaction, saleId);
        var refundLines = new List<RefundLine>();
        var lineCents = new List<(long Amount, long? Vat)>();

        foreach (var (saleItemId, quantity) in requested)
        {
            if (!sold.TryGetValue(saleItemId, out var line))
            {
                throw new InvalidOperationException($"Rivi {saleItemId} ei kuulu myyntiin #{saleId}.");
            }

            if (quantity > line.Remaining)
            {
                throw new InvalidOperationException(
                    $"{line.Name}: palautettavissa on enää {line.Remaining} kpl.");
            }

            var amount = line.UnitPriceCents * quantity;
            long? vat = line.VatBasisPoints is not { } rate
                ? null
                : quantity == line.Remaining && line.VatCents is { } recorded
                    ? recorded - line.RefundedVatCents
                    : ToCents(Vat.Included(FromCents(amount), rate));

            lineCents.Add((amount, vat));
            refundLines.Add(new RefundLine(
                saleItemId, line.Name, FromCents(line.UnitPriceCents), quantity, line.VatBasisPoints,
                vat is { } cents ? FromCents(cents) : null));
        }

        var subtotal = FromCents(lineCents.Sum(line => line.Amount));
        var total = method == PaymentMethod.Cash ? Sale.CashTotal(subtotal, roundCash) : subtotal;
        var refundedAt = DateTimeOffset.Now;

        using var insertRefund = connection.CreateCommand();
        insertRefund.Transaction = transaction;
        insertRefund.CommandText =
            """
            INSERT INTO Refunds (SaleId, "DateTime", RefundedBy, Reason, PaymentMethod, TotalCents, RoundingCents)
            VALUES (@saleId, @dateTime, @refundedBy, @reason, @paymentMethod, @totalCents, @roundingCents)
            RETURNING Id;
            """;
        Database.AddParameter(insertRefund, "@saleId", saleId);
        Database.AddParameter(insertRefund, "@dateTime", refundedAt.ToString("o", CultureInfo.InvariantCulture));
        Database.AddParameter(insertRefund, "@refundedBy", refundedBy);
        Database.AddParameter(insertRefund, "@reason", reason);
        Database.AddParameter(insertRefund, "@paymentMethod", method.ToString());
        Database.AddParameter(insertRefund, "@totalCents", ToCents(total));
        Database.AddParameter(insertRefund, "@roundingCents", ToCents(total - subtotal));
        var refundId = Convert.ToInt64(insertRefund.ExecuteScalar(), CultureInfo.InvariantCulture);

        for (var i = 0; i < refundLines.Count; i++)
        {
            using var insertLine = connection.CreateCommand();
            insertLine.Transaction = transaction;
            insertLine.CommandText =
                """
                INSERT INTO RefundItems (RefundId, SaleItemId, Quantity, AmountCents, VatCents)
                VALUES (@refundId, @saleItemId, @quantity, @amountCents, @vatCents);
                """;
            Database.AddParameter(insertLine, "@refundId", refundId);
            Database.AddParameter(insertLine, "@saleItemId", refundLines[i].SaleItemId);
            Database.AddParameter(insertLine, "@quantity", refundLines[i].Quantity);
            Database.AddParameter(insertLine, "@amountCents", lineCents[i].Amount);
            Database.AddParameter(insertLine, "@vatCents", lineCents[i].Vat, DbType.Int64);
            insertLine.ExecuteNonQuery();
        }

        var described = string.Join(", ", refundLines.Select(line => $"{line.Quantity} × {line.Name}"));
        database.Audit.Record(connection, transaction, AuditActions.RefundCreated,
            $"Myynti #{saleId}: {described}, {PaymentMethodNames.Finnish(method)} {AuditLog.Money(total)}. Syy: {reason}");

        transaction.Commit();

        return new Refund(refundId, saleId, refundedAt, refundedBy, reason, method, total, total - subtotal, refundLines);
    }

    private static Dictionary<long, RefundableLine> ReadRefundableLines(
        DbConnection connection, DbTransaction transaction, long saleId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT i.Id, i.Name, i.UnitPriceCents, i.Quantity, i.VatRateBasisPoints, i.VatCents,
                   CAST(COALESCE((SELECT SUM(ri.Quantity) FROM RefundItems ri WHERE ri.SaleItemId = i.Id), 0) AS BIGINT),
                   CAST(COALESCE((SELECT SUM(ri.VatCents) FROM RefundItems ri WHERE ri.SaleItemId = i.Id), 0) AS BIGINT)
            FROM SaleItems i
            WHERE i.SaleId = @saleId;
            """;
        Database.AddParameter(command, "@saleId", saleId);

        using var reader = command.ExecuteReader();

        var lines = new Dictionary<long, RefundableLine>();
        while (reader.Read())
        {
            var line = new RefundableLine(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                (int)reader.GetInt64(6),
                reader.GetInt64(7));
            lines[line.Id] = line;
        }

        return lines;
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
    internal static PaymentMethod ParsePaymentMethod(string value) =>
        Enum.TryParse<PaymentMethod>(value, out var parsed) ? parsed : PaymentMethod.Card;

    public static long ToCents(decimal amount) =>
        (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>Kept next to <see cref="ToCents"/> so the two stay in step when reading rows back.</summary>
    public static decimal FromCents(long cents) => cents / 100m;
}
