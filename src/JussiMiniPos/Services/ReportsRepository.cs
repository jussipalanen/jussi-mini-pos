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
            SELECT {ReportDate}, COUNT(*), CAST(SUM(s.TotalCents) AS BIGINT),
                   CAST(SUM(COALESCE((SELECT SUM(i.Quantity) FROM SaleItems i WHERE i.SaleId = s.Id), 0)) AS BIGINT)
            FROM Sales s
            WHERE {DateFilter}
            GROUP BY {ReportDate}
            ORDER BY {ReportDate};
            """);
        var days = new List<ReportDay>();
        using (var reader = daily.ExecuteReader())
        {
            while (reader.Read())
            {
                days.Add(new ReportDay(
                    DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue),
                    reader.GetInt64(1), reader.GetInt64(3), Money.FromCents(reader.GetInt64(2))));
            }
        }

        using var productsCommand = CreateCommand(connection, transaction, from, through,
            $"""
            SELECT i.ProductId, i.Name, CAST(SUM(i.Quantity) AS BIGINT), CAST(SUM(i.UnitPriceCents * i.Quantity) AS BIGINT)
            FROM SaleItems i JOIN Sales s ON s.Id = i.SaleId
            WHERE {DateFilter}
            GROUP BY i.ProductId, i.Name
            ORDER BY SUM(i.UnitPriceCents * i.Quantity) DESC, i.Name, i.ProductId;
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
            SELECT i.Category, CAST(SUM(i.Quantity) AS BIGINT), CAST(SUM(i.UnitPriceCents * i.Quantity) AS BIGINT)
            FROM SaleItems i JOIN Sales s ON s.Id = i.SaleId
            WHERE {DateFilter}
            GROUP BY i.Category
            ORDER BY SUM(i.UnitPriceCents * i.Quantity) DESC, i.Category;
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
            SELECT i.VatRateBasisPoints, CAST(SUM(i.UnitPriceCents * i.Quantity) AS BIGINT), CAST(SUM(i.VatCents) AS BIGINT)
            FROM SaleItems i JOIN Sales s ON s.Id = i.SaleId
            WHERE {DateFilter}
            GROUP BY i.VatRateBasisPoints;
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
            [.. vat.OrderByDescending(line => line.BasisPoints ?? -1)]);
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
