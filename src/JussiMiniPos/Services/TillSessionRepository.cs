using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using JussiMiniPos.Models;

namespace JussiMiniPos.Services;

/// <summary>
/// Opens and closes till sessions and adds up their end-of-day reports.
/// Each computer is its own till, so tills sharing a database keep their own
/// drawers and their own reports.
/// </summary>
/// <param name="till">This till's name; the computer's name unless a test says otherwise.</param>
public sealed class TillSessionRepository(Database database, string? till = null)
{
    /// <summary>The till this repository opens and closes sessions for.</summary>
    public string Till { get; } = till ?? Environment.MachineName;

    private const string Columns =
        "Id, Till, OpenedAt, OpenedBy, OpeningFloatCents, ClosedAt, ClosedBy, ExpectedCashCents, CountedCashCents, Note";

    /// <summary>This till's open session, or null when it has not been opened.</summary>
    public TillSession? GetOpen()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM TillSessions WHERE Till = @till AND ClosedAt IS NULL;";
        Database.AddParameter(command, "@till", Till);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    /// <summary>Recent sessions of every till, newest first, for the history list.</summary>
    public IReadOnlyList<TillSession> GetSessions(int limit = 200)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM TillSessions ORDER BY Id DESC LIMIT @limit;";
        Database.AddParameter(command, "@limit", limit);
        using var reader = command.ExecuteReader();

        var sessions = new List<TillSession>();
        while (reader.Read())
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    /// <summary>
    /// Opens this till with <paramref name="openingFloat"/> in the drawer.
    /// The unique index on open sessions refuses a second one, even from a
    /// second copy of the application on the same computer.
    /// </summary>
    /// <exception cref="InvalidOperationException">This till is already open.</exception>
    public TillSession Open(string openedBy, decimal openingFloat)
    {
        if (openingFloat < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(openingFloat), "Aloituskassa ei voi olla negatiivinen.");
        }

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var open = connection.CreateCommand())
        {
            open.Transaction = transaction;
            open.CommandText = "SELECT COUNT(*) FROM TillSessions WHERE Till = @till AND ClosedAt IS NULL;";
            Database.AddParameter(open, "@till", Till);
            if (Convert.ToInt64(open.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException($"Kassa {Till} on jo avattu.");
            }
        }

        var openedAt = DateTimeOffset.Now;
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO TillSessions (Till, OpenedAt, OpenedBy, OpeningFloatCents)
            VALUES (@till, @openedAt, @openedBy, @float)
            RETURNING Id;
            """;
        Database.AddParameter(insert, "@till", Till);
        Database.AddParameter(insert, "@openedAt", openedAt.ToString("o", CultureInfo.InvariantCulture));
        Database.AddParameter(insert, "@openedBy", openedBy);
        Database.AddParameter(insert, "@float", Money.ToCents(openingFloat));
        var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);

        database.Audit.Record(connection, transaction, AuditActions.TillOpened,
            $"Kassa {Till}, aloituskassa {AuditLog.Money(openingFloat)}");

        transaction.Commit();
        return new TillSession(id, Till, openedAt, openedBy, openingFloat);
    }

    /// <summary>
    /// Closes a session with the cash counted, records what was expected and
    /// the difference, and returns the final report. Expected cash is worked
    /// out inside the same transaction, so a sale stored a moment earlier is
    /// in it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is missing or already closed.</exception>
    public TillReport Close(long sessionId, string closedBy, decimal countedCash, string? note)
    {
        if (countedCash < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(countedCash), "Laskettu käteinen ei voi olla negatiivinen.");
        }

        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var before = Report(connection, transaction, sessionId);
        if (!before.Session.IsOpen)
        {
            throw new InvalidOperationException("Kassa on jo suljettu.");
        }

        var closedAt = DateTimeOffset.Now;
        var expected = before.ExpectedCash;

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE TillSessions
                   SET ClosedAt = @closedAt, ClosedBy = @closedBy,
                       ExpectedCashCents = @expected, CountedCashCents = @counted, Note = @note
                 WHERE Id = @id AND ClosedAt IS NULL;
                """;
            Database.AddParameter(update, "@id", sessionId);
            Database.AddParameter(update, "@closedAt", closedAt.ToString("o", CultureInfo.InvariantCulture));
            Database.AddParameter(update, "@closedBy", closedBy);
            Database.AddParameter(update, "@expected", Money.ToCents(expected));
            Database.AddParameter(update, "@counted", Money.ToCents(countedCash));
            Database.AddParameter(update, "@note", note, DbType.String);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("Kassa on jo suljettu.");
            }
        }

        var difference = countedCash - expected;
        database.Audit.Record(connection, transaction, AuditActions.TillClosed,
            $"Kassa {before.Session.Till}: myynti {AuditLog.Money(before.NetTotal)}, "
            + $"käteistä odotettu {AuditLog.Money(expected)}, laskettu {AuditLog.Money(countedCash)}, "
            + $"erotus {AuditLog.Money(difference)}" + (note is null ? string.Empty : $". {note}"));

        transaction.Commit();

        return before with
        {
            Session = before.Session with
            {
                ClosedAt = closedAt,
                ClosedBy = closedBy,
                ExpectedCash = expected,
                CountedCash = countedCash,
                Note = note,
            },
            GeneratedAt = closedAt,
        };
    }

    /// <summary>The report for a session, open (a mid-day X report) or closed.</summary>
    public TillReport GetReport(long sessionId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var report = Report(connection, transaction, sessionId);
        transaction.Commit();
        return report;
    }

    private static TillReport Report(DbConnection connection, DbTransaction transaction, long sessionId)
    {
        TillSession session;
        using (var command = Command(connection, transaction, sessionId, $"SELECT {Columns} FROM TillSessions WHERE Id = @id;"))
        using (var reader = command.ExecuteReader())
        {
            session = reader.Read()
                ? ReadSession(reader)
                : throw new InvalidOperationException($"Kassan jaksoa {sessionId} ei löydy.");
        }

        var sales = new Dictionary<PaymentMethod, (int Count, long Cents)>();
        long rounding = 0;
        using (var command = Command(connection, transaction, sessionId,
            """
            SELECT PaymentMethod, COUNT(*), CAST(SUM(TotalCents) AS BIGINT), CAST(SUM(RoundingCents) AS BIGINT)
            FROM Sales WHERE TillSessionId = @id AND VoidedAt IS NULL
            GROUP BY PaymentMethod;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                sales[SalesRepository.ParsePaymentMethod(reader.GetString(0))] = ((int)reader.GetInt64(1), reader.GetInt64(2));
                rounding += reader.GetInt64(3);
            }
        }

        var refunds = new Dictionary<PaymentMethod, (int Count, long Cents)>();
        using (var command = Command(connection, transaction, sessionId,
            """
            SELECT PaymentMethod, COUNT(*), CAST(SUM(TotalCents) AS BIGINT), CAST(SUM(RoundingCents) AS BIGINT)
            FROM Refunds WHERE TillSessionId = @id
            GROUP BY PaymentMethod;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                refunds[SalesRepository.ParsePaymentMethod(reader.GetString(0))] = ((int)reader.GetInt64(1), reader.GetInt64(2));
                rounding -= reader.GetInt64(3);
            }
        }

        var items = Scalar(connection, transaction, sessionId,
            """
            SELECT CAST(COALESCE(SUM(i.Quantity), 0) AS BIGINT)
            FROM SaleItems i JOIN Sales s ON s.Id = i.SaleId
            WHERE s.TillSessionId = @id AND s.VoidedAt IS NULL;
            """);

        int voidCount;
        long voided;
        using (var command = Command(connection, transaction, sessionId,
            """
            SELECT COUNT(*), CAST(COALESCE(SUM(TotalCents), 0) AS BIGINT)
            FROM Sales WHERE TillSessionId = @id AND VoidedAt IS NOT NULL;
            """))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            voidCount = (int)reader.GetInt64(0);
            voided = reader.GetInt64(1);
        }

        // Sold lines in, returned lines out, by the rate they were sold at.
        var vat = new List<VatLine>();
        using (var command = Command(connection, transaction, sessionId,
            """
            SELECT m.Rate, CAST(SUM(m.Amount) AS BIGINT), CAST(SUM(m.Vat) AS BIGINT)
            FROM (
                SELECT i.VatRateBasisPoints AS Rate, i.UnitPriceCents * i.Quantity AS Amount, i.VatCents AS Vat
                FROM SaleItems i JOIN Sales s ON s.Id = i.SaleId
                WHERE s.TillSessionId = @id AND s.VoidedAt IS NULL
                UNION ALL
                SELECT i.VatRateBasisPoints, -ri.AmountCents, -ri.VatCents
                FROM RefundItems ri
                JOIN Refunds r ON r.Id = ri.RefundId
                JOIN SaleItems i ON i.Id = ri.SaleItemId
                WHERE r.TillSessionId = @id
            ) AS m
            GROUP BY m.Rate;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                vat.Add(new VatLine(
                    reader.IsDBNull(0) ? null : reader.GetInt32(0),
                    Money.FromCents(reader.GetInt64(1)),
                    reader.IsDBNull(0) || reader.IsDBNull(2) ? null : Money.FromCents(reader.GetInt64(2))));
            }
        }

        // Card first, then cash, then anything else; a method with neither
        // sales nor refunds is left out, except cash, which the count needs.
        var payments = Enum.GetValues<PaymentMethod>()
            .Where(method => method == PaymentMethod.Cash || sales.ContainsKey(method) || refunds.ContainsKey(method))
            .Select(method =>
            {
                var sold = sales.GetValueOrDefault(method);
                var back = refunds.GetValueOrDefault(method);
                return new PaymentTotal(method, sold.Count, Money.FromCents(sold.Cents), back.Count, Money.FromCents(back.Cents));
            })
            .ToList();

        return new TillReport(
            session,
            payments,
            [.. vat.OrderByDescending(line => line.BasisPoints ?? -1)],
            sales.Values.Sum(sale => sale.Count),
            (int)items,
            voidCount,
            Money.FromCents(voided),
            Money.FromCents(rounding),
            DateTimeOffset.Now);
    }

    private static TillSession ReadSession(DbDataReader reader) =>
        new(reader.GetInt64(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(3),
            Money.FromCents(reader.GetInt64(4)))
        {
            ClosedAt = reader.IsDBNull(5)
                ? null
                : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ClosedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
            ExpectedCash = reader.IsDBNull(7) ? null : Money.FromCents(reader.GetInt64(7)),
            CountedCash = reader.IsDBNull(8) ? null : Money.FromCents(reader.GetInt64(8)),
            Note = reader.IsDBNull(9) ? null : reader.GetString(9),
        };

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, long sessionId, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Database.AddParameter(command, "@id", sessionId);
        return command;
    }

    private static long Scalar(DbConnection connection, DbTransaction transaction, long sessionId, string sql)
    {
        using var command = Command(connection, transaction, sessionId, sql);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
