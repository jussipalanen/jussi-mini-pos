using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using JussiMiniPos.Models;

namespace JussiMiniPos.Services;

/// <summary>
/// An append-only record of who did what: sign-ins, voids, catalogue and
/// price changes, VAT rates and user accounts. There is deliberately no way
/// to change or delete an entry through the application.
/// </summary>
/// <remarks>
/// Changes write their entry through <see cref="Record(DbConnection, DbTransaction, string, string)"/>
/// inside their own transaction, so a change and its entry are stored
/// together or not at all — a void can never happen without its trace.
/// Details are written for a person to read and never contain passwords,
/// hashes or keys.
/// </remarks>
public sealed class AuditLog(Database database)
{
    /// <summary>The most entries the log view reads at once.</summary>
    public const int DefaultLimit = 500;

    /// <summary>
    /// Whoever is acting, recorded on every entry: the signed-in username in
    /// the application, a command-line marker for <c>--user-*</c> and the
    /// like, or null when nobody is signed in. Process-lifetime state, like
    /// the signed-in user it mirrors.
    /// </summary>
    public string? Actor { get; set; }

    /// <summary>The actor used for command-line runs, naming the Windows account behind them.</summary>
    public static string CommandLineActor => $"komentorivi ({Environment.UserName})";

    private static readonly CultureInfo Finnish = CultureInfo.GetCultureInfo("fi-FI");

    /// <summary>"9,20 €" whatever culture the process runs in, so entries read the same everywhere.</summary>
    public static string Money(decimal amount) => amount.ToString("N2", Finnish) + " €";

    /// <summary>Records an event that is not part of a larger write, such as a sign-in.</summary>
    /// <param name="actor">
    /// Who it was, when that is not <see cref="Actor"/> — a sign-in is done
    /// by the person signing in, before they are the signed-in user.
    /// </param>
    public void Record(string action, string details, string? actor = null)
    {
        using var connection = database.OpenConnection();
        Write(connection, transaction: null, action, details, actor ?? Actor);
    }

    /// <summary>Records an event inside the transaction of the change it describes.</summary>
    public void Record(DbConnection connection, DbTransaction transaction, string action, string details) =>
        Write(connection, transaction, action, details, Actor);

    /// <summary>The newest entries first.</summary>
    public IReadOnlyList<AuditEntry> GetEntries(int limit = DefaultLimit)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, "DateTime", Actor, Action, Details
            FROM AuditLog
            ORDER BY Id DESC
            LIMIT @limit;
            """;
        Database.AddParameter(command, "@limit", limit);

        using var reader = command.ExecuteReader();

        var entries = new List<AuditEntry>();
        while (reader.Read())
        {
            entries.Add(new AuditEntry(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return entries;
    }

    private static void Write(DbConnection connection, DbTransaction? transaction, string action, string details, string? actor)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO AuditLog ("DateTime", Actor, Action, Details)
            VALUES (@dateTime, @actor, @action, @details);
            """;
        Database.AddParameter(command, "@dateTime", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
        Database.AddParameter(command, "@actor", actor, System.Data.DbType.String);
        Database.AddParameter(command, "@action", action);
        Database.AddParameter(command, "@details", details);
        command.ExecuteNonQuery();
    }
}
