using JussiMiniPos.Services;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace JussiMiniPos.Tests;

/// <summary>
/// A database of its own for one test, created empty with the current schema
/// and removed afterwards. Tests never touch the user's real data under
/// %LOCALAPPDATA%\JussiMiniPos.
/// </summary>
/// <remarks>
/// SQLite gets a file in a fresh temporary folder. PostgreSQL gets a fresh
/// database on the server named by <see cref="PostgresVariable"/>, or the
/// docker-compose development server when that is unset; it is dropped again
/// on dispose. When no server can be reached the test is skipped, unless
/// <see cref="RequirePostgresVariable"/> is set, as CI does, in which case it
/// fails: a CI run that silently skipped one engine would prove nothing about
/// it.
/// </remarks>
public sealed class TestDatabase : IDisposable
{
    /// <summary>Connection string of a PostgreSQL login allowed to create databases.</summary>
    public const string PostgresVariable = "JUSSIMINIPOS_TEST_POSTGRES";

    /// <summary>Set to <c>true</c> to fail rather than skip when PostgreSQL is unreachable.</summary>
    public const string RequirePostgresVariable = "JUSSIMINIPOS_TEST_REQUIRE_POSTGRES";

    /// <summary>The server from docker-compose.yml, so a developer needs no setup.</summary>
    private const string DevelopmentServer =
        "Host=localhost;Port=5432;Username=jussipos;Password=jussipos;Database=postgres;Timeout=3";

    public const string Sqlite = "sqlite";
    public const string PostgreSql = "postgresql";

    private readonly string _directory;
    private readonly string? _postgresDatabase;
    private readonly string? _adminConnectionString;

    private TestDatabase(Database database, string directory, string? postgresDatabase, string? adminConnectionString)
    {
        Database = database;
        _directory = directory;
        _postgresDatabase = postgresDatabase;
        _adminConnectionString = adminConnectionString;
    }

    /// <summary>Both engines, for <c>[MemberData]</c> on tests that must hold for each.</summary>
    public static TheoryData<string> Engines => [Sqlite, PostgreSql];

    public Database Database { get; }

    /// <summary>A new, empty database with the current schema.</summary>
    public static TestDatabase Create(string engine)
    {
        var testDatabase = CreateUninitialised(engine);
        testDatabase.Database.EnsureCreated();
        return testDatabase;
    }

    /// <summary>A database that exists but has no schema yet, for migration and first-run tests.</summary>
    public static TestDatabase CreateUninitialised(string engine)
    {
        var directory = Path.Combine(Path.GetTempPath(), "JussiMiniPos.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        return engine switch
        {
            Sqlite => CreateSqlite(directory),
            PostgreSql => CreatePostgreSql(directory),
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown engine."),
        };
    }

    /// <summary>The SQLite file path, for tests that build an old schema by hand.</summary>
    public string SqlitePath => Path.Combine(_directory, "test.db");

    /// <summary>Runs one statement on a fresh connection.</summary>
    public void Execute(string sql)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>The id of the seeded VAT rate with this percentage, e.g. 2550 for 25,5 %.</summary>
    public int VatRateId(int basisPoints = 2550) =>
        new CatalogRepository(Database).GetVatRates().First(rate => rate.BasisPoints == basisPoints).Id;

    /// <summary>Reads one number, such as a row count.</summary>
    public long Scalar(string sql)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        if (_postgresDatabase is not null && _adminConnectionString is not null)
        {
            // Pooled connections would keep the database "in use".
            NpgsqlConnection.ClearAllPools();
            using var admin = new NpgsqlConnection(_adminConnectionString);
            admin.Open();
            using var drop = admin.CreateCommand();

            // The name is generated below from a GUID, never from input.
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{_postgresDatabase}\" WITH (FORCE);";
            drop.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder left behind is harmless; failing the test over it is not.
        }
    }

    /// <summary>A Database over an existing settings object, as the application would open it again.</summary>
    public Database Reopen() => new(Database.Settings);

    private static TestDatabase CreateSqlite(string directory)
    {
        var path = Path.Combine(directory, "test.db");

        // Pooling off, so no handle outlives the test and the folder can be deleted.
        var settings = new DatabaseSettings(
            DatabaseProvider.Sqlite,
            $"Data Source={path};Pooling=False",
            path,
            directory,
            "tests");

        return new TestDatabase(new Database(settings), directory, null, null);
    }

    private static TestDatabase CreatePostgreSql(string directory)
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(PostgresVariable) is { Length: > 0 } configured
            ? configured
            : DevelopmentServer;

        var name = $"jmp_test_{Guid.NewGuid():N}";
        try
        {
            using var admin = new NpgsqlConnection(adminConnectionString);
            admin.Open();
            using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\";";
            create.ExecuteNonQuery();
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            Directory.Delete(directory, recursive: true);

            if (string.Equals(Environment.GetEnvironmentVariable(RequirePostgresVariable), "true", StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }

            Assert.Skip(
                $"PostgreSQL is not reachable ({exception.Message}). Start it with `docker compose up -d`, " +
                $"or point {PostgresVariable} at a server.");
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name };
        var settings = new DatabaseSettings(
            DatabaseProvider.PostgreSql,
            builder.ConnectionString,
            DatabasePath: null,
            directory,
            "tests");

        return new TestDatabase(new Database(settings), directory, name, adminConnectionString);
    }
}
