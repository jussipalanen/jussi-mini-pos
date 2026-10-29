using System;
using System.Data;
using System.Data.Common;
using System.IO;

namespace JussiMiniPos.Services;

/// <summary>
/// Owns the database and its schema, whichever engine is configured.
/// Repositories sit on top of this and borrow connections from it.
/// </summary>
/// <remarks>
/// Which engine that is comes from <see cref="DatabaseSettings"/>, which is
/// read from a file and the environment rather than from the database — the
/// settings table cannot say how to open the database it lives in.
/// </remarks>
public sealed class Database
{
    private readonly DatabaseSettings _settings;
    private bool _isNew;

    /// <summary>Opens whatever the configuration points at.</summary>
    public Database()
        : this(DatabaseSettings.Load())
    {
    }

    /// <summary>Opens a specific configuration; the command line and tests use this.</summary>
    public Database(DatabaseSettings settings)
    {
        _settings = settings;
        Dialect = SqlDialect.For(settings.Provider);
        Audit = new AuditLog(this);

        Directory.CreateDirectory(settings.DataDirectory);

        if (settings.Provider == DatabaseProvider.Sqlite && settings.DatabasePath is { } path)
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Checked before the file is opened, because opening creates it.
            _isNew = !File.Exists(path);
        }
    }

    /// <summary>Which engine this is, and the SQL that differs because of it.</summary>
    public SqlDialect Dialect { get; }

    /// <summary>
    /// The audit log, here so every repository can write an entry inside its
    /// own transaction without each one being handed the log separately.
    /// </summary>
    public AuditLog Audit { get; }

    /// <summary>The configuration this was opened with.</summary>
    public DatabaseSettings Settings => _settings;

    /// <summary>
    /// The SQLite file, or the server and database under PostgreSQL. Shown to
    /// the user and never carries the password.
    /// </summary>
    public string Path => _settings.Display;

    /// <summary>
    /// Where images and the Gemini key live. Under SQLite this is the folder
    /// holding the database file; under PostgreSQL there is no such file, so
    /// it is configured in its own right and defaults to the same per-user
    /// application data folder.
    /// </summary>
    public string DataDirectory => _settings.DataDirectory;

    /// <summary>
    /// True when there was nothing there before — a first run. Seeding keys
    /// off this rather than "are the tables empty", so emptying the catalogue
    /// on purpose stays emptied across restarts. Only meaningful once
    /// <see cref="EnsureCreated"/> has run.
    /// </summary>
    public bool IsNew => _isNew;

    /// <summary>
    /// Per-user application data, so the app does not need write access to its
    /// own install directory.
    /// </summary>
    public static string DefaultPath => DatabaseSettings.DefaultDatabasePath;

    /// <summary>Opens a connection in the state the repositories expect.</summary>
    public DbConnection OpenConnection() => Dialect.Open(_settings.ConnectionString);

    /// <summary>Creates the schema if it is not there yet. Safe to call repeatedly.</summary>
    public void EnsureCreated()
    {
        using var connection = OpenConnection();

        // PostgreSQL has no file whose absence means "first run", so the
        // question is asked of the schema instead, before it is created.
        if (_settings.Provider != DatabaseProvider.Sqlite)
        {
            _isNew = !TableExists(connection, "Products");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = Dialect.Schema;
            command.ExecuteNonQuery();
        }

        Dialect.SchemaApplied(connection);
        ApplyMigrations(connection);
    }

    /// <summary>
    /// Brings an older database up to the current schema. CREATE TABLE IF NOT
    /// EXISTS only helps with tables that are missing entirely, so columns
    /// added to an existing table have to be handled here.
    /// </summary>
    /// <remarks>
    /// The steps before VAT only ever fire against a SQLite file old enough
    /// to predate them, since PostgreSQL support arrived later. VAT came after
    /// both, so its steps run on either engine.
    /// </remarks>
    private void ApplyMigrations(DbConnection connection)
    {
        var money = _settings.Provider == DatabaseProvider.Sqlite ? "INTEGER" : "BIGINT";

        // Added when products gained a price and an offer price.
        AddColumnIfMissing(connection, "Products", "PriceCents", $"{money} NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "Products", "SalePriceCents", $"{money} NULL");

        // Added when users gained a name of their own. Empty rather than NULL,
        // so "no name given" is one state and the profile form has something
        // to bind to; the seeded administrator starts without one.
        AddColumnIfMissing(connection, "Users", "FirstName", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "Users", "LastName", "TEXT NOT NULL DEFAULT ''");

        // Added when the category links stopped leaning on SQLite's implicit
        // rowid for their order, which PostgreSQL has nothing to match. An
        // existing file is backfilled from that very rowid, so the primary
        // category every product already had stays the one it had.
        if (AddColumnIfMissing(connection, "ProductCategories", "SortOrder", "INTEGER NOT NULL DEFAULT 0")
            && _settings.Provider == DatabaseProvider.Sqlite)
        {
            using var backfill = connection.CreateCommand();
            backfill.CommandText = "UPDATE ProductCategories SET SortOrder = rowid;";
            backfill.ExecuteNonQuery();
        }

        // Added with account lockout, and with every sale recording who sold it.
        AddColumnIfMissing(connection, "Users", "FailedSignIns", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "Users", "LockedUntil", "TEXT NULL");
        AddColumnIfMissing(connection, "Sales", "SoldBy", "TEXT NULL");

        // Added when deleting a sale became voiding it.
        AddColumnIfMissing(connection, "Sales", "VoidedAt", "TEXT NULL");
        AddColumnIfMissing(connection, "Sales", "VoidedBy", "TEXT NULL");
        AddColumnIfMissing(connection, "Sales", "VoidReason", "TEXT NULL");

        // Added with VAT. Sale lines from before keep NULL: their VAT was
        // never recorded, and inventing it now would rewrite history.
        AddColumnIfMissing(connection, "SaleItems", "VatRateBasisPoints", "INTEGER NULL");
        AddColumnIfMissing(connection, "SaleItems", "VatCents", $"{money} NULL");

        // Nullable: SQLite cannot add a NOT NULL foreign key to a table that
        // already has rows. Filled straight away below.
        AddColumnIfMissing(connection, "Products", "VatRateId", "INTEGER NULL REFERENCES VatRates(Id) ON DELETE RESTRICT");

        SeedVatRates(connection);

        // Products from before VAT get the general rate. Never under-reports
        // VAT; anything sold at a reduced rate is lowered in Tuotteet.
        using (var assign = connection.CreateCommand())
        {
            assign.CommandText =
                """
                UPDATE Products SET VatRateId = (
                    SELECT Id FROM VatRates ORDER BY BasisPoints DESC, Id LIMIT 1)
                 WHERE VatRateId IS NULL;
                """;
            assign.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The Finnish rates in force since 1 January 2026, written once into an
    /// empty table. Afterwards the list is the user's: a rate change is an
    /// edit in the application, not a new release.
    /// </summary>
    private static void SeedVatRates(DbConnection connection)
    {
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM VatRates;";
        if (Convert.ToInt64(count.ExecuteScalar()) > 0)
        {
            return;
        }

        foreach (var (name, basisPoints) in DefaultVatRates)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO VatRates (Name, BasisPoints, IsActive) VALUES (@name, @basisPoints, 1);";
            AddParameter(insert, "@name", name);
            AddParameter(insert, "@basisPoints", basisPoints);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Rate names are user-visible, so Finnish.</summary>
    private static readonly (string Name, int BasisPoints)[] DefaultVatRates =
    [
        ("Yleinen", 2550),
        ("Alennettu", 1350),
        ("Alennettu", 1000),
        ("Veroton", 0),
    ];

    private bool AddColumnIfMissing(
        DbConnection connection,
        string table,
        string column,
        string definition)
    {
        if (ColumnExists(connection, table, column))
        {
            return false;
        }

        using var command = connection.CreateCommand();

        // Identifiers cannot be parameterised; these are compile-time literals.
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        command.ExecuteNonQuery();
        return true;
    }

    private bool ColumnExists(DbConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Dialect.ColumnExistsSql(table);
        AddParameter(command, "@column", column);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static bool TableExists(DbConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM information_schema.tables
             WHERE table_schema = current_schema()
               AND lower(table_name) = lower(@table);
            """;
        AddParameter(command, "@table", table);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Adds a parameter without naming a driver's own parameter type, which
    /// is what lets the repositories be written once.
    /// </summary>
    public static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Adds a parameter whose type is stated rather than guessed from the
    /// value.
    /// </summary>
    /// <remarks>
    /// Needed wherever the value can be null: SQLite is happy to be told
    /// nothing, but PostgreSQL infers a parameter's type from what is sent
    /// and refuses an untyped NULL with "could not determine data type",
    /// which is a runtime failure in exactly the branch that is easiest to
    /// leave untested.
    /// </remarks>
    public static void AddParameter(DbCommand command, string name, object? value, DbType type)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
