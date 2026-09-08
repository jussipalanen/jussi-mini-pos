using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using JussiMiniPos.Services;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos;

/// <summary>
/// Command line entry points for working with the catalogue tables without
/// starting the UI:
///
///   JussiMiniPos.exe --seed [--reset]   fill Categories/Products with demo data
///   JussiMiniPos.exe --dump             print what is in those tables
///   JussiMiniPos.exe --clear            delete the catalogue rows
///
/// This is a WinExe, so it owns no console. We attach to the terminal that
/// launched it; if there is none, the output goes to a message box instead.
/// </summary>
public static class CommandLine
{
    private const int AttachParentProcess = -1;

    private static readonly StringBuilder Buffer = new();
    private static bool _hasConsole;

    /// <summary>
    /// Runs a command if the arguments name one.
    /// </summary>
    /// <returns>
    /// The process exit code, or <c>null</c> when the app should start normally.
    /// </returns>
    public static int? TryRun(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        var flags = args.Select(a => a.TrimStart('-', '/').ToLowerInvariant()).ToHashSet();

        if (flags.Overlaps(["help", "h", "?"]))
        {
            return Run(_ => WriteUsage());
        }

        if (flags.Contains("seed"))
        {
            return Run(database => Seed(database, reset: flags.Contains("reset")));
        }

        if (flags.Contains("dump"))
        {
            return Run(Dump);
        }

        if (flags.Contains("clear"))
        {
            return Run(database =>
            {
                CatalogSeeder.Clear(database);
                Write("Catalogue tables cleared. Sales were left untouched.");
                SweepImages(database);
            });
        }

        return Run(_ =>
        {
            Write($"Unknown option: {string.Join(' ', args)}");
            WriteUsage();
        }, exitCode: 2);
    }

    private static int Run(Action<Database> action, int exitCode = 0)
    {
        AttachToTerminal();

        try
        {
            var database = new Database();
            database.EnsureCreated();
            Write($"Database: {database.Path}");
            Write(string.Empty);
            action(database);
        }
        catch (Exception ex)
        {
            Write($"Failed: {ex.Message}");
            exitCode = 1;
        }

        Flush();
        return exitCode;
    }

    private static void Seed(Database database, bool reset)
    {
        if (CatalogSeeder.HasData(database))
        {
            if (!reset)
            {
                Write("The catalogue tables already hold rows. Nothing was changed.");
                Write("Re-run with --seed --reset to replace them.");
                return;
            }

            CatalogSeeder.Clear(database);
            Write("Existing catalogue rows removed.");
        }

        var result = CatalogSeeder.Seed(database);
        Write($"Seeded {result.Categories} categories, {result.Products} products and " +
              $"{result.Links} product/category links.");

        SweepImages(database);
    }

    /// <summary>
    /// Removes image files nothing points at any more. Dropping catalogue rows
    /// leaves their pictures on disk, so every command that deletes rows sweeps
    /// afterwards.
    /// </summary>
    private static void SweepImages(Database database)
    {
        var store = new ImageStore(database);
        var removed = store.DeleteUnreferenced(new CatalogRepository(database).GetAllImagePaths());

        if (removed > 0)
        {
            Write($"Removed {removed} unreferenced image file(s) from {store.RootPath}.");
        }
    }

    private static void Dump(Database database)
    {
        using var connection = database.OpenConnection();

        Write("Categories");
        WriteTable(connection,
            """
            SELECT c.Id, c.Title, COALESCE(p.Title, '-') AS Parent, c.IsPublic AS Public,
                   (SELECT COUNT(*) FROM ProductCategories pc WHERE pc.CategoryId = c.Id) AS Products
            FROM Categories c
            LEFT JOIN Categories p ON p.Id = c.ParentId
            ORDER BY COALESCE(c.ParentId, c.Id), c.Id;
            """);

        Write("Products");

        // Columns named *Cents are rendered as euros by WriteTable.
        WriteTable(connection,
            """
            SELECT p.Id, p.Title,
                   p.PriceCents,
                   p.SalePriceCents,
                   p.IsPublic AS Public,
                   (SELECT GROUP_CONCAT(c.Id || '=' || c.Title, ', ')
                      FROM ProductCategories pc
                      JOIN Categories c ON c.Id = pc.CategoryId
                     WHERE pc.ProductId = p.Id) AS Categories,
                   (SELECT COUNT(*) FROM ProductImages i WHERE i.ProductId = p.Id) AS Images
            FROM Products p
            ORDER BY p.Id;
            """);
    }

    /// <summary>Prints a query as an aligned text table.</summary>
    private static void WriteTable(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();

        // Money is stored as cents; show it as euros and drop the suffix from
        // the heading, so PriceCents reads as "Price  2,50 €".
        var isMoney = names.Select(n => n.EndsWith("Cents", StringComparison.Ordinal)).ToArray();
        var headers = names
            .Select((n, i) => isMoney[i] ? n[..^"Cents".Length] : n)
            .ToArray();

        var rows = new List<string[]>();

        while (reader.Read())
        {
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i =>
                reader.IsDBNull(i)
                    ? isMoney[i] ? "-" : string.Empty
                    : isMoney[i]
                        ? SalesRepository.FromCents(reader.GetInt64(i)).ToString("C", CultureInfo.CurrentCulture)
                        : reader.GetValue(i).ToString() ?? string.Empty)]);
        }

        if (rows.Count == 0)
        {
            Write("  (empty - run --seed first)");
            Write(string.Empty);
            return;
        }

        var widths = headers
            .Select((header, i) => rows.Select(r => r[i].Length).Append(header.Length).Max())
            .ToArray();

        string Line(IReadOnlyList<string> cells) =>
            "  " + string.Join("  ", cells.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd();

        Write(Line(headers));
        Write("  " + string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
        {
            Write(Line(row));
        }

        Write(string.Empty);
        Write($"  {rows.Count} row(s)");
        Write(string.Empty);
    }

    private static void WriteUsage()
    {
        Write("JussiMiniPos - catalogue tools");
        Write(string.Empty);
        Write("  JussiMiniPos.exe                 start the application");
        Write("  JussiMiniPos.exe --seed          fill Categories/Products with demo data");
        Write("  JussiMiniPos.exe --seed --reset  replace any existing catalogue rows");
        Write("  JussiMiniPos.exe --dump          print the catalogue tables");
        Write("  JussiMiniPos.exe --clear         delete the catalogue rows (keeps sales)");
        Write("  JussiMiniPos.exe --help          this text");
    }

    private static void Write(string line)
    {
        if (_hasConsole)
        {
            Console.WriteLine(line);
        }
        else
        {
            Buffer.AppendLine(line);
        }
    }

    /// <summary>Shows the buffered output when there was no terminal to print to.</summary>
    private static void Flush()
    {
        if (_hasConsole || Buffer.Length == 0)
        {
            return;
        }

        System.Windows.MessageBox.Show(Buffer.ToString(), "JussiMiniPos");
        Buffer.Clear();
    }

    private static void AttachToTerminal()
    {
        _hasConsole = AttachConsole(AttachParentProcess);
        if (_hasConsole)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
