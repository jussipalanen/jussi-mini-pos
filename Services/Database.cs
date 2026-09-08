using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos.Services;

/// <summary>
/// Owns the SQLite file and its schema. Repositories sit on top of this and
/// borrow connections from it.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string? databasePath = null)
    {
        Path = databasePath ?? DefaultPath;

        // Checked before the file is opened, because opening creates it.
        IsNew = !File.Exists(Path);

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <summary>Where the database file lives on disk.</summary>
    public string Path { get; }

    /// <summary>
    /// True when there was no database file before this instance was made — a
    /// first run. Seeding keys off this rather than "are the tables empty", so
    /// emptying the catalogue on purpose stays emptied across restarts.
    /// </summary>
    public bool IsNew { get; }

    /// <summary>
    /// Per-user application data, so the app does not need write access to its
    /// own install directory.
    /// </summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JussiMiniPos",
        "jussiminipos.db");

    /// <summary>Opens a connection with foreign key enforcement turned on.</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // SQLite defaults foreign keys to off, per connection.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>Creates the schema if it is not there yet. Safe to call repeatedly.</summary>
    public void EnsureCreated()
    {
        using var connection = OpenConnection();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }

        ApplyMigrations(connection);
    }

    /// <summary>
    /// Brings an older database up to the current schema. CREATE TABLE IF NOT
    /// EXISTS only helps with tables that are missing entirely, so columns
    /// added to an existing table have to be handled here.
    /// </summary>
    private static void ApplyMigrations(SqliteConnection connection)
    {
        // Added when products gained a price and an offer price.
        AddColumnIfMissing(connection, "Products", "PriceCents", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "Products", "SalePriceCents", "INTEGER NULL");

        // Added when users gained a name of their own. Empty rather than NULL,
        // so "no name given" is one state and the profile form has something
        // to bind to; the seeded administrator starts without one.
        AddColumnIfMissing(connection, "Users", "FirstName", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "Users", "LastName", "TEXT NOT NULL DEFAULT ''");
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        if (ColumnExists(connection, table, column))
        {
            return;
        }

        using var command = connection.CreateCommand();

        // Identifiers cannot be parameterised; these are compile-time literals.
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        command.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Column names are PascalCase throughout so the whole database reads the
    /// same way. Booleans are INTEGER 0/1 with a CHECK, because SQLite has no
    /// boolean type. Money is an integer number of cents; see
    /// <see cref="SalesRepository"/> for why.
    /// </summary>
    private const string Schema =
        """
        -- ---------- Application options ----------

        -- Name/value settings the user changes in Admin, so they survive a
        -- restart. OptionName is UNIQUE because the name is what callers look
        -- an option up by; the Id is there to keep the table shaped like the
        -- rest of the schema. Absent means "use the default": a row is only
        -- written once something is actually chosen.
        --
        -- The Gemini API key deliberately does not live here. It is a secret,
        -- and this file is the thing that gets copied around as a backup.
        CREATE TABLE IF NOT EXISTS Options (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            OptionName  TEXT    NOT NULL UNIQUE,
            OptionValue TEXT    NOT NULL
        );

        -- ---------- Users ----------

        -- Who may open Admin. PasswordHash is named for what it holds: a
        -- PBKDF2 digest, not a recoverable password. Nothing here can turn
        -- back into what the user typed, which is the point — a stolen
        -- database must not hand over anybody's password.
        --
        -- Role is a CHECKed string rather than a number, so --dump reads
        -- without a lookup table and an invalid role cannot be stored.
        CREATE TABLE IF NOT EXISTS Users (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            Username     TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            Email        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            FirstName    TEXT    NOT NULL DEFAULT '',
            LastName     TEXT    NOT NULL DEFAULT '',
            PasswordHash TEXT    NOT NULL,
            Role         TEXT    NOT NULL CHECK (Role IN ('admin', 'manager', 'seller'))
        );

        -- ---------- Catalogue ----------

        CREATE TABLE IF NOT EXISTS Categories (
            Id       INTEGER PRIMARY KEY AUTOINCREMENT,
            Title    TEXT    NOT NULL,
            ParentId INTEGER     NULL REFERENCES Categories(Id) ON DELETE RESTRICT,
            IsPublic INTEGER NOT NULL DEFAULT 1 CHECK (IsPublic IN (0, 1))
        );

        CREATE INDEX IF NOT EXISTS IX_Categories_ParentId ON Categories(ParentId);

        -- PriceCents is the normal price. SalePriceCents is the discounted one
        -- and is NULL when the product is not on offer, so "is there an offer"
        -- is a NULL check rather than a sentinel value.
        CREATE TABLE IF NOT EXISTS Products (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            Title          TEXT    NOT NULL,
            Description    TEXT    NOT NULL DEFAULT '',
            FeatureImage   TEXT        NULL,
            PriceCents     INTEGER NOT NULL DEFAULT 0,
            SalePriceCents INTEGER     NULL,
            IsPublic       INTEGER NOT NULL DEFAULT 1 CHECK (IsPublic IN (0, 1))
        );

        -- A product's gallery. FeatureImage above is the single image used in
        -- lists; these are the rest, in SortOrder.
        CREATE TABLE IF NOT EXISTS ProductImages (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            ProductId INTEGER NOT NULL REFERENCES Products(Id) ON DELETE CASCADE,
            Path      TEXT    NOT NULL,
            SortOrder INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_ProductImages_ProductId ON ProductImages(ProductId);

        -- A product can sit in several categories, so the link lives in its own
        -- table rather than a column on Products.
        CREATE TABLE IF NOT EXISTS ProductCategories (
            ProductId  INTEGER NOT NULL REFERENCES Products(Id) ON DELETE CASCADE,
            CategoryId INTEGER NOT NULL REFERENCES Categories(Id) ON DELETE CASCADE,
            PRIMARY KEY (ProductId, CategoryId)
        );

        CREATE INDEX IF NOT EXISTS IX_ProductCategories_CategoryId
            ON ProductCategories(CategoryId);

        -- ---------- Sales ----------

        CREATE TABLE IF NOT EXISTS Sales (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            "DateTime"    TEXT    NOT NULL,
            TotalCents    INTEGER NOT NULL,
            PaymentMethod TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_Sales_DateTime ON Sales("DateTime");

        -- Sold lines are a snapshot: the name and price are copied at the time
        -- of sale so later catalogue edits never rewrite history.
        CREATE TABLE IF NOT EXISTS SaleItems (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            SaleId         INTEGER NOT NULL REFERENCES Sales(Id) ON DELETE CASCADE,
            ProductId      INTEGER NOT NULL,
            Name           TEXT    NOT NULL,
            Category       TEXT    NOT NULL,
            UnitPriceCents INTEGER NOT NULL,
            Quantity       INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_SaleItems_SaleId ON SaleItems(SaleId);
        """;
}
