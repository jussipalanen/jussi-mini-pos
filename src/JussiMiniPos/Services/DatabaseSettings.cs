using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace JussiMiniPos.Services;

/// <summary>Which database engine the application talks to.</summary>
public enum DatabaseProvider
{
    /// <summary>
    /// A local file under the user's application data. The default, and what
    /// a single till on a counter wants: nothing to install and nothing to
    /// keep running.
    /// </summary>
    Sqlite,

    /// <summary>
    /// A shared server, so several tills can sell out of one catalogue and
    /// report over one sales history.
    /// </summary>
    PostgreSql,
}

/// <summary>
/// Which database to open and how to reach it, read before anything is
/// opened.
/// </summary>
/// <remarks>
/// This deliberately does not live in the <c>Options</c> table like the AI
/// settings do. <see cref="OptionsRepository"/> reads that table through a
/// connection, so storing the driver there would mean needing a database
/// connection to find out how to connect to the database. The configuration
/// has to come from outside the database, which leaves a file and the
/// environment.
/// </remarks>
/// <param name="Provider">Which engine to talk to.</param>
/// <param name="ConnectionString">What the driver is opened with.</param>
/// <param name="DatabasePath">The SQLite file, or null under PostgreSQL.</param>
/// <param name="DataDirectory">Where images and the Gemini key live.</param>
/// <param name="Origin">Where these settings were read from, for --dump and diagnostics.</param>
public sealed record DatabaseSettings(
    DatabaseProvider Provider,
    string ConnectionString,
    string? DatabasePath,
    string DataDirectory,
    string Origin)
{
    /// <summary>
    /// The configuration file, looked for beside the executable first and
    /// then in the data directory. Beside the executable is what a deployment
    /// ships and what a developer drops into the working copy; the data
    /// directory is where a user who has only ever run the installer can
    /// reach it without administrator rights.
    /// </summary>
    public const string FileName = "database.env";

    /// <summary>Selects the engine: <c>sqlite</c> or <c>postgresql</c>.</summary>
    public const string ProviderKey = "JUSSIMINIPOS_DB_PROVIDER";

    /// <summary>A complete connection string, used verbatim when it is set.</summary>
    public const string ConnectionStringKey = "JUSSIMINIPOS_DB_CONNECTION";

    /// <summary>Where the SQLite file lives, when the default is not wanted.</summary>
    public const string PathKey = "JUSSIMINIPOS_DB_PATH";

    /// <summary>Where images and the Gemini key live. Defaults beside the SQLite file.</summary>
    public const string DataDirectoryKey = "JUSSIMINIPOS_DATA_DIR";

    public const string HostKey = "JUSSIMINIPOS_DB_HOST";
    public const string PortKey = "JUSSIMINIPOS_DB_PORT";
    public const string NameKey = "JUSSIMINIPOS_DB_NAME";
    public const string UserKey = "JUSSIMINIPOS_DB_USER";
    public const string PasswordKey = "JUSSIMINIPOS_DB_PASSWORD";

    /// <summary>
    /// Marks a password as DPAPI-encrypted, the same marker
    /// <see cref="AiSettings"/> uses for the Gemini key. Its absence means the
    /// value is a plain password, which is what somebody editing the file by
    /// hand will write.
    /// </summary>
    public const string EncryptedPrefix = "DPAPI:";

    /// <summary>Per-user application data, the default home for everything.</summary>
    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JussiMiniPos");

    /// <summary>The SQLite file used when nothing says otherwise.</summary>
    public static string DefaultDatabasePath =>
        Path.Combine(DefaultDataDirectory, "jussiminipos.db");

    /// <summary>
    /// Where a server-backed configuration keeps its images and key, kept
    /// deliberately apart from the SQLite folder.
    /// </summary>
    /// <remarks>
    /// These must not be the same folder. Product images are files on disk
    /// that only the database knows the names of, and deleting rows sweeps
    /// away the pictures nothing points at any more. Two databases sharing one
    /// image folder therefore means each one's sweep deletes the other's
    /// pictures — running <c>--seed</c> against a fresh PostgreSQL database
    /// would wipe the images belonging to the SQLite one. Separate folders by
    /// default; point both at one place with JUSSIMINIPOS_DATA_DIR only if
    /// that is genuinely what you want.
    /// </remarks>
    public static string DefaultServerDataDirectory =>
        Path.Combine(DefaultDataDirectory, "postgresql");

    /// <summary>True when this configuration talks to a server rather than a file.</summary>
    public bool IsServer => Provider == DatabaseProvider.PostgreSql;

    /// <summary>
    /// What to show a user: the file for SQLite, and host/database for
    /// PostgreSQL. Never the connection string, which carries the password.
    /// </summary>
    public string Display => Provider switch
    {
        DatabaseProvider.Sqlite => DatabasePath ?? DefaultDatabasePath,
        _ => Describe(ConnectionString),
    };

    /// <summary>
    /// Reads the configuration. A value in the file wins over the matching
    /// environment variable: the file is the thing an administrator edits on
    /// purpose, so a stray variable in a shell must not silently point a till
    /// at another database.
    /// </summary>
    /// <param name="probeDirectory">
    /// Where to look for the file before the data directory. Defaults to the
    /// executable's own folder; tests pass their own.
    /// </param>
    public static DatabaseSettings Load(string? probeDirectory = null)
    {
        var (values, origin) = ReadFile(probeDirectory);
        var provider = ParseProvider(Read(values, ProviderKey));

        var dataDirectory = Read(values, DataDirectoryKey);
        var explicitConnection = Read(values, ConnectionStringKey);

        if (provider == DatabaseProvider.Sqlite)
        {
            var path = Read(values, PathKey) ?? DefaultDatabasePath;

            return new DatabaseSettings(
                provider,
                explicitConnection ?? SqliteConnectionString(path),
                path,
                dataDirectory ?? Path.GetDirectoryName(path) ?? DefaultDataDirectory,
                origin);
        }

        return new DatabaseSettings(
            provider,
            explicitConnection ?? PostgreSqlConnectionString(values),
            DatabasePath: null,
            dataDirectory ?? DefaultServerDataDirectory,
            origin);
    }

    /// <summary>
    /// Strips the password out of a connection string, so a configuration can
    /// be printed or logged without leaking one.
    /// </summary>
    public static string Describe(string connectionString)
    {
        var parts = new List<string>();

        foreach (var pair in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = pair[..separator].Trim();
            if (key.Equals("Password", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            parts.Add($"{key}={pair[(separator + 1)..].Trim()}");
        }

        return string.Join("; ", parts);
    }

    private static string SqliteConnectionString(string path) =>
        $"Data Source={path}";

    private static string PostgreSqlConnectionString(IReadOnlyDictionary<string, string> values)
    {
        var host = Read(values, HostKey) ?? "localhost";
        var port = Read(values, PortKey) ?? "5432";
        var name = Read(values, NameKey) ?? "jussiminipos";
        var user = Read(values, UserKey) ?? "jussiminipos";
        var password = Decrypt(Read(values, PasswordKey) ?? string.Empty);

        if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidOperationException(
                $"{PortKey} is not a port number: \"{port}\".");
        }

        // Semicolons and quotes in a password would otherwise end the value
        // early and produce a confusing "no password supplied" from the server.
        return $"Host={host};Port={port};Database={name};Username={user};Password={Quote(password)}";
    }

    private static string Quote(string value) =>
        value.Contains(';') || value.Contains('\'') || value.Contains('"')
            ? "'" + value.Replace("'", "''") + "'"
            : value;

    /// <summary>
    /// Decrypts a DPAPI-marked password. A value without the marker is
    /// returned as it stands, so a file written by hand keeps working.
    /// </summary>
    private static string Decrypt(string value)
    {
        if (!value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var encrypted = Convert.FromBase64String(value[EncryptedPrefix.Length..]);
            var plaintext = ProtectedData.Unprotect(
                encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
        {
            // A file copied from another account or another machine cannot be
            // decrypted here. Failing closed with a sentence that says why
            // beats handing the server an unusable password.
            throw new InvalidOperationException(
                $"{PasswordKey} is DPAPI-encrypted and cannot be read by this Windows account. " +
                "Set it again on this machine.", ex);
        }
    }

    private static DatabaseProvider ParseProvider(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "sqlite" or "sqlite3" => DatabaseProvider.Sqlite,
        "postgresql" or "postgres" or "pgsql" or "npgsql" => DatabaseProvider.PostgreSql,
        _ => throw new InvalidOperationException(
            $"{ProviderKey} is not a supported database: \"{value}\". Use \"sqlite\" or \"postgresql\"."),
    };

    /// <summary>The file's value, or the environment variable when the file is silent.</summary>
    private static string? Read(IReadOnlyDictionary<string, string> values, string key)
    {
        if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim();
    }

    private static (Dictionary<string, string> Values, string Origin) ReadFile(string? probeDirectory)
    {
        foreach (var directory in Candidates(probeDirectory))
        {
            var path = Path.Combine(directory, FileName);

            try
            {
                // Read rather than tested for first: File.Exists followed by a
                // read is two answers to the same question, and only the read
                // matters.
                return (Parse(File.ReadAllLines(path)), path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Not configured here; try the next place.
            }
        }

        return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "environment");
    }

    private static IEnumerable<string> Candidates(string? probeDirectory)
    {
        yield return probeDirectory ?? AppContext.BaseDirectory;
        yield return DefaultDataDirectory;
    }

    /// <summary>
    /// Parses <c>KEY=VALUE</c> lines. Blank lines and <c>#</c> comments are
    /// skipped, and a value may be quoted so trailing spaces survive.
    /// </summary>
    private static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // "export KEY=VALUE" so a file can be sourced by a shell too.
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return values;
    }
}
