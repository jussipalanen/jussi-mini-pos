using System;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace JussiMiniPos.Services;

/// <summary>
/// The SQL that differs between the supported engines, in one place, so the
/// repositories can be written once.
/// </summary>
/// <remarks>
/// Only genuine differences live here. Parameters are written <c>@name</c>
/// throughout, which both drivers accept, and every reader reads by ordinal,
/// so PostgreSQL folding unquoted identifiers to lower case changes nothing.
/// Aggregates are wrapped in <c>CAST(... AS BIGINT)</c> at the call site
/// rather than here: SQLite is happy to hand back whatever the sum fits in,
/// but PostgreSQL widens <c>SUM</c> over a <c>bigint</c> to <c>numeric</c>,
/// which <c>GetInt64</c> refuses.
/// </remarks>
public abstract class SqlDialect
{
    /// <summary>The dialect for a provider.</summary>
    public static SqlDialect For(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.Sqlite => new SqliteDialect(),
        DatabaseProvider.PostgreSql => new PostgreSqlDialect(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>The whole schema, safe to run against a database that has it already.</summary>
    public abstract string Schema { get; }

    /// <summary>Opens a connection and puts it in the state the repositories expect.</summary>
    public abstract DbConnection Open(string connectionString);

    /// <summary>
    /// Case-insensitive folding that understands "ä" and "ö". SQLite's own
    /// <c>lower()</c> folds ASCII only, so there it is a .NET callback;
    /// PostgreSQL's is already Unicode-aware.
    /// </summary>
    public abstract string Fold(string expression);

    /// <summary>
    /// The local calendar date of an ISO timestamp that carries its own
    /// offset. Comparing the text, or applying a fixed +02, would misclassify
    /// midnight and summer time.
    /// </summary>
    public abstract string ReportDate(string expression);

    /// <summary>The smaller of two values.</summary>
    public abstract string Least(string first, string second);

    /// <summary>Joins a column's values into one string.</summary>
    public abstract string GroupConcat(string expression, string separator);

    /// <summary>
    /// Compares a name column against a parameter without regard to case, so
    /// either spelling of a username or an email matches the stored row.
    /// </summary>
    /// <remarks>
    /// Not simply <c>=</c> under PostgreSQL. The column is citext, but the
    /// driver sends the parameter explicitly typed as text, and PostgreSQL
    /// resolves <c>citext = text</c> by casting the column down to text —
    /// which is a case-sensitive comparison, and silently so. The same query
    /// typed into psql matches, because a bare literal there is untyped and
    /// coerces the other way, which makes this an easy one to test by hand
    /// and still ship broken.
    /// </remarks>
    public abstract string TextEquals(string column, string parameter);

    /// <summary>Whether a column is already there, for the migrations.</summary>
    public abstract string ColumnExistsSql(string table);

    /// <summary>Registers whatever the connection needs before the reporting queries run.</summary>
    public virtual void PrepareReporting(DbConnection connection)
    {
    }

    /// <summary>Registers whatever the connection needs before the search queries run.</summary>
    public virtual void PrepareSearch(DbConnection connection)
    {
    }

    /// <summary>The Finnish wall clock, which is what a Finnish till reports in.</summary>
    protected const string ReportZone = "Europe/Helsinki";
}

/// <summary>SQLite: a local file, and the default.</summary>
public sealed class SqliteDialect : SqlDialect
{
    public override DbConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        // SQLite defaults foreign keys to off, per connection.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    public override string Fold(string expression) => $"fold({expression})";

    public override string ReportDate(string expression) => $"report_date({expression})";

    public override string Least(string first, string second) => $"MIN({first}, {second})";

    public override string GroupConcat(string expression, string separator) =>
        $"GROUP_CONCAT({expression}, {separator})";

    /// <summary>Both name columns are COLLATE NOCASE, so plain equality already folds case.</summary>
    public override string TextEquals(string column, string parameter) =>
        $"{column} = {parameter}";

    public override string ColumnExistsSql(string table) =>
        $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @column;";

    /// <summary>
    /// ISO timestamps carry their original offset, so the conversion is a
    /// .NET one. Invalid legacy timestamps become NULL rather than a false
    /// date, which drops them from a report instead of misfiling them.
    /// </summary>
    public override void PrepareReporting(DbConnection connection)
    {
        ((SqliteConnection)connection).CreateFunction("report_date", (string value) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
                ? ReportsRepository.LocalDate(timestamp).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null, isDeterministic: true);
    }

    /// <summary>
    /// SQLite's own lower() and LIKE fold ASCII only, which would leave
    /// "Äyriäiset" unmatched by "äyri". .NET knows better, so the folding is
    /// handed to it.
    /// </summary>
    public override void PrepareSearch(DbConnection connection)
    {
        ((SqliteConnection)connection).CreateFunction(
            "fold",
            (string? value) => value?.ToLowerInvariant(),
            isDeterministic: true);
    }

    /// <summary>
    /// Column names are PascalCase throughout so the whole database reads the
    /// same way. Booleans are INTEGER 0/1 with a CHECK, because SQLite has no
    /// boolean type. Money is an integer number of cents; see
    /// <see cref="SalesRepository"/> for why.
    /// </summary>
    public override string Schema =>
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
            -- Insert order, so the category written first stays the product's
            -- primary one. SQLite could lean on its implicit rowid for that;
            -- PostgreSQL has no such column, so the order is stored outright.
            SortOrder  INTEGER NOT NULL DEFAULT 0,
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

/// <summary>PostgreSQL: a shared server, for more than one till.</summary>
public sealed class PostgreSqlDialect : SqlDialect
{
    public override DbConnection Open(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        // Foreign keys are always enforced here, so there is no PRAGMA to
        // match — the SQLite one exists only because SQLite defaults them off.
        return connection;
    }

    /// <summary>PostgreSQL's lower() is already Unicode-aware, so nothing to register.</summary>
    public override string Fold(string expression) => $"lower({expression})";

    /// <summary>
    /// The stored text carries its own offset, so casting to timestamptz
    /// keeps the instant and AT TIME ZONE moves it to the Finnish wall clock,
    /// daylight saving included. to_char rather than a cast to text, so the
    /// result is "2026-09-11" whatever the server's DateStyle is set to —
    /// and a string, which is what the SQLite function returns and what the
    /// reader and the BETWEEN both expect.
    /// </summary>
    public override string ReportDate(string expression) =>
        $"to_char((({expression})::timestamptz AT TIME ZONE '{ReportZone}')::date, 'YYYY-MM-DD')";

    public override string Least(string first, string second) => $"LEAST({first}, {second})";

    public override string GroupConcat(string expression, string separator) =>
        $"string_agg({expression}, {separator})";

    /// <summary>The cast makes the comparison citext-to-citext, which folds case.</summary>
    public override string TextEquals(string column, string parameter) =>
        $"{column} = {parameter}::citext";

    public override string ColumnExistsSql(string table) =>
        $"""
        SELECT COUNT(*) FROM information_schema.columns
         WHERE table_schema = current_schema()
           AND lower(table_name) = lower('{table}')
           AND lower(column_name) = lower(@column);
        """;

    /// <summary>
    /// The same tables, in PostgreSQL's spelling. The column types are chosen
    /// to match how the repositories read them: PostgreSQL is strict where
    /// SQLite is not, so a column read with GetInt64 has to be a bigint even
    /// where the values would fit in an int.
    /// </summary>
    public override string Schema =>
        """
        -- Case-insensitive text, which is what COLLATE NOCASE buys under
        -- SQLite: either the username or the email can be typed in any case
        -- and still match, without lower() defeating the unique index.
        CREATE EXTENSION IF NOT EXISTS citext;

        -- ---------- Application options ----------

        -- See the SQLite schema for why the Gemini key is deliberately not
        -- one of these rows.
        CREATE TABLE IF NOT EXISTS Options (
            Id          INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            OptionName  TEXT    NOT NULL UNIQUE,
            OptionValue TEXT    NOT NULL
        );

        -- ---------- Users ----------

        CREATE TABLE IF NOT EXISTS Users (
            Id           INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            Username     CITEXT  NOT NULL UNIQUE,
            Email        CITEXT  NOT NULL UNIQUE,
            FirstName    TEXT    NOT NULL DEFAULT '',
            LastName     TEXT    NOT NULL DEFAULT '',
            PasswordHash TEXT    NOT NULL,
            Role         TEXT    NOT NULL CHECK (Role IN ('admin', 'manager', 'seller'))
        );

        -- ---------- Catalogue ----------

        -- IsPublic is bigint rather than boolean because the repositories read
        -- it with GetInt64 and compare against 0, which is the shape SQLite's
        -- INTEGER 0/1 forced. Keeping the two engines the same here means the
        -- reading code stays single-dialect.
        CREATE TABLE IF NOT EXISTS Categories (
            Id       INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            Title    TEXT    NOT NULL,
            ParentId INTEGER     NULL REFERENCES Categories(Id) ON DELETE RESTRICT,
            IsPublic BIGINT  NOT NULL DEFAULT 1 CHECK (IsPublic IN (0, 1))
        );

        CREATE INDEX IF NOT EXISTS IX_Categories_ParentId ON Categories(ParentId);

        CREATE TABLE IF NOT EXISTS Products (
            Id             INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            Title          TEXT    NOT NULL,
            Description    TEXT    NOT NULL DEFAULT '',
            FeatureImage   TEXT        NULL,
            PriceCents     BIGINT  NOT NULL DEFAULT 0,
            SalePriceCents BIGINT      NULL,
            IsPublic       BIGINT  NOT NULL DEFAULT 1 CHECK (IsPublic IN (0, 1))
        );

        CREATE TABLE IF NOT EXISTS ProductImages (
            Id        INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            ProductId INTEGER NOT NULL REFERENCES Products(Id) ON DELETE CASCADE,
            Path      TEXT    NOT NULL,
            SortOrder INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_ProductImages_ProductId ON ProductImages(ProductId);

        CREATE TABLE IF NOT EXISTS ProductCategories (
            ProductId  INTEGER NOT NULL REFERENCES Products(Id) ON DELETE CASCADE,
            CategoryId INTEGER NOT NULL REFERENCES Categories(Id) ON DELETE CASCADE,
            -- Insert order, so the category written first stays the product's
            -- primary one. SQLite could lean on its implicit rowid for that;
            -- PostgreSQL has no such column, so the order is stored outright.
            SortOrder  INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (ProductId, CategoryId)
        );

        CREATE INDEX IF NOT EXISTS IX_ProductCategories_CategoryId
            ON ProductCategories(CategoryId);

        -- ---------- Sales ----------

        -- "DateTime" stays quoted, exactly as under SQLite, so the column is
        -- spelled the same way in both engines and the shared queries need no
        -- dialect of their own.
        CREATE TABLE IF NOT EXISTS Sales (
            Id            BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "DateTime"    TEXT   NOT NULL,
            TotalCents    BIGINT NOT NULL,
            PaymentMethod TEXT   NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_Sales_DateTime ON Sales("DateTime");

        CREATE TABLE IF NOT EXISTS SaleItems (
            Id             BIGINT  GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            SaleId         BIGINT  NOT NULL REFERENCES Sales(Id) ON DELETE CASCADE,
            ProductId      INTEGER NOT NULL,
            Name           TEXT    NOT NULL,
            Category       TEXT    NOT NULL,
            UnitPriceCents BIGINT  NOT NULL,
            Quantity       INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_SaleItems_SaleId ON SaleItems(SaleId);
        """;
}
