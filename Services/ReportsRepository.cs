using System.Globalization;
using JussiMiniPos.Models;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos.Services;

/// <summary>Read-only reports over sale snapshots, independent of today's catalogue.</summary>
public sealed class ReportsRepository(Database database)
{
    private static readonly TimeZoneInfo ReportTimeZone = TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");

    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.Now, ReportTimeZone).DateTime);

    public static DateOnly LocalDate(DateTimeOffset timestamp) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, ReportTimeZone).DateTime);

    private const string DateFilter = "report_date(s.\"DateTime\") BETWEEN $from AND $through";

    public SalesReport GetReport(DateOnly from, DateOnly through)
    {
        if (from > through)
        {
            throw new ArgumentException("Alkupäivä ei saa olla loppupäivän jälkeen.");
        }

        using var connection = OpenConnection();
        // All sections must describe the same database snapshot, even if a sale
        // is added or deleted while the report is being read.
        using var transaction = connection.BeginTransaction(deferred: true);
        using var daily = CreateCommand(connection, transaction, from, through,
            $"""
            SELECT report_date(s."DateTime"), COUNT(*), SUM(s.TotalCents),
                   SUM(COALESCE((SELECT SUM(i.Quantity) FROM SaleItems i WHERE i.SaleId = s.Id), 0))
            FROM Sales s
            WHERE {DateFilter}
            GROUP BY report_date(s."DateTime")
            ORDER BY report_date(s."DateTime");
            """);
        var days = new List<ReportDay>();
        using (var reader = daily.ExecuteReader())
        {
            while (reader.Read())
            {
                days.Add(new ReportDay(
                    DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue),
                    reader.GetInt64(1), reader.GetInt64(3), SalesRepository.FromCents(reader.GetInt64(2))));
            }
        }

        using var productsCommand = CreateCommand(connection, transaction, from, through,
            $"""
            SELECT i.ProductId, i.Name, SUM(i.Quantity), SUM(i.UnitPriceCents * i.Quantity)
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
                    reader.GetInt64(2), SalesRepository.FromCents(reader.GetInt64(3))));
            }
        }

        using var categoriesCommand = CreateCommand(connection, transaction, from, through,
            $"""
            SELECT i.Category, SUM(i.Quantity), SUM(i.UnitPriceCents * i.Quantity)
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
                    reader.GetInt64(1), SalesRepository.FromCents(reader.GetInt64(2))));
            }
        }

        transaction.Commit();
        var maximum = days.Count == 0 ? 0 : days.Max(day => day.Total);
        return new SalesReport(from, through, days.Sum(day => day.SaleCount), days.Sum(day => day.ItemCount),
            days.Sum(day => day.Total),
            days.Select(day => day with { BarWidth = maximum <= 0 ? 0 : (double)(day.Total / maximum) * 120 }).ToList(),
            products, categories);
    }

    public IReadOnlyList<Sale> GetDaySales(DateOnly day)
    {
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, day, day,
            $"""
            SELECT s.Id, s."DateTime", s.TotalCents, s.PaymentMethod,
                   i.ProductId, i.Name, i.Category, i.UnitPriceCents, i.Quantity
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
                    SalesRepository.FromCents(reader.GetInt64(2)),
                    Enum.TryParse<PaymentMethod>(reader.GetString(3), out var method) ? method : PaymentMethod.Card,
                    items)
                { Id = id });
                previousId = id;
            }

            if (!reader.IsDBNull(4))
            {
                items.Add(new SaleItem(reader.GetInt32(4), reader.GetString(5), reader.GetString(6),
                    SalesRepository.FromCents(reader.GetInt64(7)), reader.GetInt32(8)));
            }
        }

        return sales;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = database.OpenConnection();
        // ISO timestamps contain their original offset. Comparing their text or
        // applying a fixed +02 offset would misclassify midnight and summer time.
        // Invalid legacy timestamps are excluded rather than assigned a false date.
        connection.CreateFunction("report_date", (string value) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
                ? LocalDate(timestamp).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null, isDeterministic: true);
        return connection;
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction,
        DateOnly from, DateOnly through, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return command;
    }
}
