using System.Globalization;
using JussiMiniPos.Models;
using System.Data.Common;

namespace JussiMiniPos.Services;

/// <summary>Read-only reports over sale snapshots, independent of today's catalogue.</summary>
public sealed class ReportsRepository(Database database)
{
    private static readonly TimeZoneInfo ReportTimeZone = TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");

    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.Now, ReportTimeZone).DateTime);

    public static DateOnly LocalDate(DateTimeOffset timestamp) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, ReportTimeZone).DateTime);

    /// <summary>The sale's local calendar date, in whichever engine is configured.</summary>
    private string ReportDate => database.Dialect.ReportDate("s.\"DateTime\"");

    /// <summary>
    /// Sales in the range that still count. Voided sales are left out of
    /// every figure and of the day listing; they stay visible, marked, in
    /// Myynti.
    /// </summary>
    private string DateFilter => $"{ReportDate} BETWEEN @from AND @through AND s.VoidedAt IS NULL";

    /// <summary>The Finnish calendar date a refund was made on.</summary>
    private string RefundDate => database.Dialect.ReportDate("r.\"DateTime\"");

    /// <summary>
    /// Refunds count on the day they were made, not the day of the sale, so a
    /// closed day's figures never change afterwards. A voided sale cannot
    /// have refunds, so there is nothing to leave out here.
    /// </summary>
    private string RefundFilter => $"{RefundDate} BETWEEN @from AND @through";

    /// <summary>
    /// Every sold line in the range, plus every returned line in the range as
    /// a negative one, with the product, category and rate it was sold under.
    /// The breakdowns sum this, so a refund takes its amount, quantity and
    /// VAT out of exactly the rows it was sold into.
    /// </summary>
    private string Movements =>
        $"""
        SELECT i.ProductId, i.Name, i.Category, i.VatRateBasisPoints AS Rate,
               i.Quantity AS Quantity, i.UnitPriceCents * i.Quantity AS Amount, i.VatCents AS Vat
        FROM SaleItems i JOIN Sales s ON s.Id = i.SaleId
        WHERE {DateFilter}
        UNION ALL
        SELECT i.ProductId, i.Name, i.Category, i.VatRateBasisPoints,
               -ri.Quantity, -ri.AmountCents, -ri.VatCents
        FROM RefundItems ri
        JOIN Refunds r ON r.Id = ri.RefundId
        JOIN SaleItems i ON i.Id = ri.SaleItemId
        WHERE {RefundFilter}
        """;

    public SalesReport GetReport(DateOnly from, DateOnly through)
    {
        if (from > through)
        {
            throw new ArgumentException("Alkupäivä ei saa olla loppupäivän jälkeen.");
        }

        using var connection = OpenConnection();
        // All sections must describe the same database snapshot, even if a sale
        // is added or deleted while the report is being read.
        using var transaction = connection.BeginTransaction();
        using var daily = CreateCommand(connection, transaction, from, through,
            $"""
            SELECT Day, CAST(SUM(Sales) AS BIGINT), CAST(SUM(Total) AS BIGINT), CAST(SUM(Items) AS BIGINT),
                   CAST(SUM(Refunded) AS BIGINT)
            FROM (
                SELECT {ReportDate} AS Day, 1 AS Sales, s.TotalCents AS Total,
                       COALESCE((SELECT SUM(i.Quantity) FROM SaleItems i WHERE i.SaleId = s.Id), 0) AS Items,
                       0 AS Refunded
                FROM Sales s
                WHERE {DateFilter}
                UNION ALL
                SELECT {RefundDate}, 0, -r.TotalCents,
                       -COALESCE((SELECT SUM(ri.Quantity) FROM RefundItems ri WHERE ri.RefundId = r.Id), 0),
                       r.TotalCents
                FROM Refunds r
                WHERE {RefundFilter}
            ) AS Days
            GROUP BY Day
            ORDER BY Day;
            """);
        var days = new List<ReportDay>();
        using (var reader = daily.ExecuteReader())
        {
            while (reader.Read())
            {
                days.Add(new ReportDay(
                    DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue),
                    reader.GetInt64(1), reader.GetInt64(3), Money.FromCents(reader.GetInt64(2)))
                {
                    Refunded = Money.FromCents(reader.GetInt64(4)),
                });
            }
        }

        using var refundCount = CreateCommand(connection, transaction, from, through,
            $"SELECT COUNT(*) FROM Refunds r WHERE {RefundFilter};");
        var refunds = Convert.ToInt64(refundCount.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var productsCommand = CreateCommand(connection, transaction, from, through,
            $"""
            SELECT m.ProductId, m.Name, CAST(SUM(m.Quantity) AS BIGINT), CAST(SUM(m.Amount) AS BIGINT)
            FROM ({Movements}) AS m
            GROUP BY m.ProductId, m.Name
            ORDER BY SUM(m.Amount) DESC, m.Name, m.ProductId;
            """);
        var products = new List<ReportProduct>();
        using (var reader = productsCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                products.Add(new ReportProduct(reader.GetInt32(0), reader.GetString(1),
                    reader.GetInt64(2), Money.FromCents(reader.GetInt64(3))));
            }
        }

        using var categoriesCommand = CreateCommand(connection, transaction, from, through,
            $"""
            SELECT m.Category, CAST(SUM(m.Quantity) AS BIGINT), CAST(SUM(m.Amount) AS BIGINT)
            FROM ({Movements}) AS m
            GROUP BY m.Category
            ORDER BY SUM(m.Amount) DESC, m.Category;
            """);
        var categories = new List<ReportCategory>();
        using (var reader = categoriesCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(0);
                categories.Add(new ReportCategory(name.Length == 0 ? "Ei kategoriaa" : name,
                    reader.GetInt64(1), Money.FromCents(reader.GetInt64(2))));
            }
        }

        // The VAT recorded on each line at the time of sale, summed per rate.
        // Lines from before VAT was recorded form a group of their own with
        // no VAT figure, rather than being guessed at.
        using var vatCommand = CreateCommand(connection, transaction, from, through,
            $"""
            SELECT m.Rate, CAST(SUM(m.Amount) AS BIGINT), CAST(SUM(m.Vat) AS BIGINT)
            FROM ({Movements}) AS m
            GROUP BY m.Rate;
            """);
        var vat = new List<VatLine>();
        using (var reader = vatCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                vat.Add(new VatLine(
                    reader.IsDBNull(0) ? null : reader.GetInt32(0),
                    Money.FromCents(reader.GetInt64(1)),
                    reader.IsDBNull(0) || reader.IsDBNull(2) ? null : Money.FromCents(reader.GetInt64(2))));
            }
        }

        transaction.Commit();
        var maximum = days.Count == 0 ? 0 : days.Max(day => day.Total);
        return new SalesReport(from, through, days.Sum(day => day.SaleCount), days.Sum(day => day.ItemCount),
            days.Sum(day => day.Total),
            days.Select(day => day with { BarWidth = maximum <= 0 ? 0 : (double)(day.Total / maximum) * 120 }).ToList(),
            products, categories,
            // Highest rate first, unrecorded last; sorted here because the two
            // engines disagree on where NULL sorts.
            [.. vat.OrderByDescending(line => line.BasisPoints ?? -1)])
        {
            RefundCount = refunds,
            RefundTotal = days.Sum(day => day.Refunded),
        };
    }

    public IReadOnlyList<Sale> GetDaySales(DateOnly day)
    {
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, day, day,
            $"""
            SELECT s.Id, s."DateTime", s.TotalCents, s.PaymentMethod, s.RoundingCents, s.CashReceivedCents,
                   i.ProductId, i.Name, i.Category, i.UnitPriceCents, i.Quantity,
                   i.VatRateBasisPoints, i.VatCents
            FROM Sales s LEFT JOIN SaleItems i ON i.SaleId = s.Id
            WHERE {DateFilter}
            ORDER BY s.Id DESC, i.Id;
            """);
        using var reader = command.ExecuteReader();
        var sales = new List<Sale>();
        List<SaleItem> items = [];
        long? previousId = null;
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (id != previousId)
            {
                items = [];
                var timestamp = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                sales.Add(new Sale(TimeZoneInfo.ConvertTime(timestamp, ReportTimeZone),
                    Money.FromCents(reader.GetInt64(2)),
                    SalesRepository.ParsePaymentMethod(reader.GetString(3)),
                    items)
                {
                    Id = id,
                    Rounding = Money.FromCents(reader.GetInt64(4)),
                    CashReceived = reader.IsDBNull(5) ? null : Money.FromCents(reader.GetInt64(5)),
                });
                previousId = id;
            }

            if (!reader.IsDBNull(6))
            {
                items.Add(SalesRepository.ReadItem(reader, 6));
            }
        }

        return sales;
    }

    private DbConnection OpenConnection()
    {
        var connection = database.OpenConnection();

        // ISO timestamps contain their original offset. Comparing their text or
        // applying a fixed +02 offset would misclassify midnight and summer time.
        // SQLite needs a .NET callback to work that out and PostgreSQL does it
        // in SQL, so which of the two happens is the dialect's business.
        database.Dialect.PrepareReporting(connection);
        return connection;
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction,
        DateOnly from, DateOnly through, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Database.AddParameter(command, "@from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Database.AddParameter(command, "@through", through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return command;
    }
}
