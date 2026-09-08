using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos.Services;

/// <summary>
/// Reads and writes the Options table: the application settings the user
/// changes in Admin. A name with no row means "not set", so callers fall
/// back to their own default rather than to an empty string — which is why
/// every getter takes the fallback with it.
/// </summary>
public sealed class OptionsRepository(Database database)
{
    /// <summary>The stored value, or null when the option has never been set.</summary>
    public string? Get(string name)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT OptionValue FROM Options WHERE OptionName = $name;";
        command.Parameters.AddWithValue("$name", name);

        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// The stored flag, or <paramref name="fallback"/> when it is unset or was
    /// left in a state nothing wrote — a hand-edited row should not turn a
    /// feature off silently.
    /// </summary>
    public bool GetBool(string name, bool fallback) => Get(name) switch
    {
        "1" => true,
        "0" => false,
        _ => fallback,
    };

    /// <summary>
    /// Stores a value, replacing any previous one. Written as an upsert so a
    /// name stays a single row and never needs a read first.
    /// </summary>
    public void Set(string name, string value)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Options (OptionName, OptionValue) VALUES ($name, $value)
            ON CONFLICT(OptionName) DO UPDATE SET OptionValue = excluded.OptionValue;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>Stores a flag as the 0/1 the rest of the schema uses.</summary>
    public void SetBool(string name, bool value) => Set(name, value ? "1" : "0");

    /// <summary>Every option, for <c>--dump</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> GetAll()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT OptionName, OptionValue FROM Options ORDER BY OptionName;";

        using var reader = command.ExecuteReader();

        var options = new List<KeyValuePair<string, string>>();
        while (reader.Read())
        {
            options.Add(new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));
        }

        return options;
    }
}
