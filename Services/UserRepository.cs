using System;
using System.Collections.Generic;
using System.Threading;
using JussiMiniPos.Models;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos.Services;

/// <summary>
/// Reads and writes the Users table, and checks a login against it.
/// </summary>
public sealed class UserRepository(Database database)
{
    /// <summary>The user seeded into an empty table, so Admin is reachable.</summary>
    public const string DefaultUsername = "admin";

    public const string DefaultEmail = "admin@example.com";

    /// <summary>
    /// Known to anybody who has read the README, so it is a starting point and
    /// not a secret. Changing it needs a password change screen, which does
    /// not exist yet — see the note in the README.
    /// </summary>
    public const string DefaultPassword = "AdminPos1234!";

    /// <summary>
    /// Verifies a login and returns the user, or null when the name or the
    /// password is wrong.
    /// </summary>
    /// <remarks>
    /// Deliberately gives one answer for both cases. Saying "no such user"
    /// would let anybody test which usernames exist. The dummy verify on the
    /// miss keeps a wrong username as slow as a wrong password, so the reply
    /// time does not leak it either.
    /// </remarks>
    public User? Authenticate(string usernameOrEmail, string password)
    {
        var found = FindWithHash(usernameOrEmail);

        if (found is null)
        {
            PasswordHasher.Verify(password, DummyHash.Value);
            return null;
        }

        var (user, hash) = found.Value;
        return PasswordHasher.Verify(password, hash) ? user : null;
    }

    /// <summary>Every user, for <c>--dump</c>. Hashes are not included.</summary>
    public IReadOnlyList<User> GetUsers()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Username, Email, Role FROM Users ORDER BY Id;";

        using var reader = command.ExecuteReader();

        var users = new List<User>();
        while (reader.Read())
        {
            users.Add(Read(reader));
        }

        return users;
    }

    public int Count()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Users;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Adds a user and returns the id SQLite assigned.</summary>
    public int InsertUser(string username, string email, string password, UserRole role)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Users (Username, Email, PasswordHash, Role)
            VALUES ($username, $email, $passwordHash, $role);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$passwordHash", PasswordHasher.Hash(password));
        command.Parameters.AddWithValue("$role", UserRoleNames.ToStorage(role));

        return (int)(long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// One user by username or email, or null when there is no such row.
    /// Matched case-insensitively, as at the login prompt.
    /// </summary>
    public User? FindUser(string usernameOrEmail) => FindWithHash(usernameOrEmail)?.User;

    /// <summary>
    /// Changes a user's name, email and role. The password is not touched;
    /// <see cref="SetPassword"/> does that, so a routine edit cannot reset one
    /// by leaving a field blank.
    /// </summary>
    public void UpdateUser(int id, string username, string email, UserRole role)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Users SET Username = $username, Email = $email, Role = $role
             WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$role", UserRoleNames.ToStorage(role));
        command.ExecuteNonQuery();
    }

    /// <summary>Replaces a user's password with a fresh hash of a new one.</summary>
    public void SetPassword(int id, string password)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Users SET PasswordHash = $passwordHash WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$passwordHash", PasswordHasher.Hash(password));
        command.ExecuteNonQuery();
    }

    public void DeleteUser(int id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Users WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// How many administrators there are, for the guard that stops the last
    /// one being deleted or demoted. Admin would otherwise become unreachable
    /// with no way back short of editing the database by hand.
    /// </summary>
    public int CountAdmins()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Users WHERE Role = $role;";
        command.Parameters.AddWithValue("$role", UserRoleNames.ToStorage(UserRole.Admin));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Puts the default administrator into an empty table and says whether it
    /// did. Keyed off the table being empty rather than the database being new,
    /// unlike the catalogue: the table is new to databases that already exist,
    /// and an empty one would otherwise mean nobody can ever open Admin again.
    /// </summary>
    public bool EnsureDefaultAdmin()
    {
        if (Count() > 0)
        {
            return false;
        }

        InsertUser(DefaultUsername, DefaultEmail, DefaultPassword, UserRole.Admin);
        return true;
    }

    /// <summary>
    /// A real hash of a throwaway password, used to spend the same time on an
    /// unknown username as on a known one. Built once, lazily, because
    /// deriving it costs as much as a login.
    /// </summary>
    private static readonly Lazy<string> DummyHash =
        new(() => PasswordHasher.Hash("nobody"), LazyThreadSafetyMode.ExecutionAndPublication);

    private (User User, string Hash)? FindWithHash(string usernameOrEmail)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();

        // Both columns are COLLATE NOCASE, so the match is case-insensitive
        // without lower() defeating the unique index.
        command.CommandText =
            """
            SELECT Id, Username, Email, Role, PasswordHash
            FROM Users
            WHERE Username = $name OR Email = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", usernameOrEmail.Trim());

        using var reader = command.ExecuteReader();
        return reader.Read() ? (Read(reader), reader.GetString(4)) : null;
    }

    private static User Read(SqliteDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        UserRoleNames.FromStorage(reader.GetString(3)));
}
