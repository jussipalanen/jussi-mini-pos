using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using JussiMiniPos.Models;
using System.Data.Common;

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
    /// The first-run password, deliberately trivial so getting into a new
    /// install needs nothing written down. It is a starter credential and not
    /// a secret: anyone who has seen this repository knows it, so the app says
    /// to change it the moment it is used.
    ///
    /// It is only ever used for the seeded administrator. Any other user
    /// created without a password gets one from
    /// <see cref="PasswordGenerator"/> instead.
    /// </summary>
    public const string DefaultPassword = "admin";

    /// <summary>Wrong passwords in a row before an account locks.</summary>
    public const int MaxFailedSignIns = 5;

    /// <summary>
    /// How long a locked account refuses sign-ins. Long enough to make
    /// guessing slow, short enough that a cashier who fumbled their password
    /// is not stuck for the shift; an administrator can unlock sooner.
    /// </summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    /// <summary>The user for a correct login, or null. See <see cref="SignIn"/>.</summary>
    public User? Authenticate(string usernameOrEmail, string password) =>
        SignIn(usernameOrEmail, password).User;

    /// <summary>
    /// Checks a login, counting failures: after <see cref="MaxFailedSignIns"/>
    /// wrong passwords in a row the account is locked for
    /// <see cref="LockoutDuration"/>. A correct password clears the count.
    /// </summary>
    /// <remarks>
    /// An unknown username and a wrong password give the same answer, so the
    /// prompt cannot be used to find out which usernames exist, and the dummy
    /// verify keeps them equally slow. A locked account still costs a full
    /// verify, so its reply is no quicker either. The lock itself does reveal
    /// that the account exists, after several failures — the usual trade for
    /// stopping a password from being guessed.
    /// </remarks>
    public SignInResult SignIn(string usernameOrEmail, string password)
    {
        var found = FindWithHash(usernameOrEmail);

        if (found is null)
        {
            PasswordHasher.Verify(password, DummyHash.Value);

            // What was typed is not repeated: a password typed into the
            // username field by mistake would otherwise land in the log.
            database.Audit.Record(AuditActions.SignInFailed, "Tuntematon käyttäjätunnus tai sähköposti.");
            return new SignInResult(SignInStatus.WrongCredentials);
        }

        var (user, hash, failures, lockedUntil) = found.Value;

        if (lockedUntil is { } until && until > DateTimeOffset.Now)
        {
            PasswordHasher.Verify(password, DummyHash.Value);
            database.Audit.Record(AuditActions.SignInFailed, $"Lukittu tili {user.Username}: kirjautuminen estetty.");
            return new SignInResult(SignInStatus.LockedOut, LockedUntil: until);
        }

        if (!PasswordHasher.Verify(password, hash))
        {
            return RecordFailure(user, failures + 1);
        }

        WriteSignInState(user.Id, failures: 0, lockedUntil: null);
        database.Audit.Record(AuditActions.SignIn, user.Display, actor: user.Username);
        return new SignInResult(SignInStatus.Success, user);
    }

    private SignInResult RecordFailure(User user, int failures)
    {
        if (failures < MaxFailedSignIns)
        {
            WriteSignInState(user.Id, failures, lockedUntil: null);
            database.Audit.Record(AuditActions.SignInFailed, $"Väärä salasana käyttäjälle {user.Username}.");
            return new SignInResult(SignInStatus.WrongCredentials);
        }

        // The count starts again after the lock, so the next five tries get
        // the same allowance rather than one each.
        var until = DateTimeOffset.Now.Add(LockoutDuration);
        WriteSignInState(user.Id, failures: 0, lockedUntil: until);
        database.Audit.Record(AuditActions.SignInFailed,
            $"Väärä salasana käyttäjälle {user.Username}. Tili lukittiin {LockoutDuration.TotalMinutes:0} minuutiksi.");
        return new SignInResult(SignInStatus.LockedOut, LockedUntil: until);
    }

    private void WriteSignInState(int id, int failures, DateTimeOffset? lockedUntil)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Users SET FailedSignIns = @failures, LockedUntil = @lockedUntil WHERE Id = @id;";
        Database.AddParameter(command, "@id", id);
        Database.AddParameter(command, "@failures", failures);
        Database.AddParameter(command, "@lockedUntil",
            lockedUntil?.ToString("o", CultureInfo.InvariantCulture), System.Data.DbType.String);
        command.ExecuteNonQuery();
    }

    /// <summary>Lets a locked account sign in again straight away.</summary>
    public void Unlock(int id)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var before = ReadById(connection, transaction, id);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Users SET FailedSignIns = 0, LockedUntil = NULL WHERE Id = @id;";
        Database.AddParameter(command, "@id", id);
        command.ExecuteNonQuery();

        if (before is not null)
        {
            database.Audit.Record(connection, transaction, AuditActions.UserUnlocked, before.Username);
        }

        transaction.Commit();
    }

    /// <summary>Every user, with any lock, for the user list and <c>--users</c>. Hashes are not included.</summary>
    public IReadOnlyList<User> GetUsers()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Username, Email, FirstName, LastName, Role, LockedUntil FROM Users ORDER BY Id;";

        using var reader = command.ExecuteReader();

        var users = new List<User>();
        while (reader.Read())
        {
            users.Add(Read(reader) with { LockedUntil = ReadTimestamp(reader, 6) });
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
    public int InsertUser(
        string username,
        string email,
        string password,
        UserRole role,
        string firstName = "",
        string lastName = "")
    {
        // Hashed before the transaction opens: the derivation is slow, and
        // there is no reason to hold a write lock while it runs.
        var hash = PasswordHasher.Hash(password);

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO Users (Username, Email, FirstName, LastName, PasswordHash, Role)
            VALUES (@username, @email, @firstName, @lastName, @passwordHash, @role)
            RETURNING Id;
            """;
        Database.AddParameter(command, "@username", username);
        Database.AddParameter(command, "@email", email);
        Database.AddParameter(command, "@firstName", firstName);
        Database.AddParameter(command, "@lastName", lastName);
        Database.AddParameter(command, "@passwordHash", hash);
        Database.AddParameter(command, "@role", UserRoleNames.ToStorage(role));

        var id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        database.Audit.Record(connection, transaction, AuditActions.UserCreated,
            $"{username} ({UserRoleNames.Finnish(role)}), {email}");

        transaction.Commit();
        return id;
    }

    /// <summary>
    /// One user by username or email, or null when there is no such row.
    /// Matched case-insensitively, as at the login prompt.
    /// </summary>
    public User? FindUser(string usernameOrEmail) => FindWithHash(usernameOrEmail)?.User;

    /// <summary>
    /// Changes a user's login name, email and role — the fields an
    /// administrator owns. The password is not touched;
    /// <see cref="SetPassword"/> does that, so a routine edit cannot reset one
    /// by leaving a field blank.
    /// </summary>
    public void UpdateUser(int id, string username, string email, UserRole role) =>
        UpdateAccount(id, username, email, role);

    /// <summary>
    /// Changes an account as an administrator does it, in one transaction
    /// with one audit entry: login name, email, role, and optionally the name
    /// and a new password. A name left null is kept; a new password also
    /// lifts any lock.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The change would demote the last administrator.
    /// </exception>
    public void UpdateAccount(
        int id,
        string username,
        string email,
        UserRole role,
        string? firstName = null,
        string? lastName = null,
        string? newPassword = null)
    {
        // Hashed before the transaction opens: the derivation is slow.
        var hash = newPassword is null ? null : PasswordHasher.Hash(newPassword);

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var before = ReadById(connection, transaction, id)
            ?? throw new InvalidOperationException($"Käyttäjää #{id} ei löydy.");

        // Losing the last administrator would leave Admin unreachable with no
        // way back short of editing the database by hand. Checked here, in
        // the transaction, so the command line and the Admin view share it.
        if (before.Role == UserRole.Admin && role != UserRole.Admin && CountAdmins(connection, transaction) <= 1)
        {
            throw new InvalidOperationException(
                $"{before.Username} on ainoa ylläpitäjä. Ylennä ensin toinen käyttäjä ylläpitäjäksi.");
        }

        firstName ??= before.FirstName;
        lastName ??= before.LastName;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            UPDATE Users SET Username = @username, Email = @email, Role = @role,
                             FirstName = @firstName, LastName = @lastName
                             {(hash is null ? string.Empty : ", PasswordHash = @hash, FailedSignIns = 0, LockedUntil = NULL")}
             WHERE Id = @id;
            """;
        Database.AddParameter(command, "@id", id);
        Database.AddParameter(command, "@username", username);
        Database.AddParameter(command, "@email", email);
        Database.AddParameter(command, "@role", UserRoleNames.ToStorage(role));
        Database.AddParameter(command, "@firstName", firstName);
        Database.AddParameter(command, "@lastName", lastName);
        if (hash is not null)
        {
            Database.AddParameter(command, "@hash", hash);
        }

        command.ExecuteNonQuery();

        var changes = new List<string>();
        if (before.Username != username)
        {
            changes.Add($"tunnus {before.Username} → {username}");
        }

        if (!string.Equals(before.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"sähköposti {before.Email} → {email}");
        }

        if (before.Role != role)
        {
            changes.Add($"rooli {UserRoleNames.Finnish(before.Role)} → {UserRoleNames.Finnish(role)}");
        }

        if (before.FirstName != firstName || before.LastName != lastName)
        {
            changes.Add("nimi");
        }

        // That a password was set, never what it was.
        if (hash is not null)
        {
            changes.Add("uusi salasana");
        }

        database.Audit.Record(connection, transaction, AuditActions.UserUpdated,
            $"{before.Username}: {(changes.Count == 0 ? "ei muutoksia" : string.Join("; ", changes))}");

        transaction.Commit();
    }

    /// <summary>
    /// Changes the fields a user owns about themselves. Separate from
    /// <see cref="UpdateUser"/> on purpose: this statement names no Username
    /// and no Role, so editing a profile cannot rename a login or hand out a
    /// role however the caller is written.
    /// </summary>
    public void UpdateProfile(int id, string firstName, string lastName, string email)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var before = ReadById(connection, transaction, id);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE Users SET FirstName = @firstName, LastName = @lastName, Email = @email
             WHERE Id = @id;
            """;
        Database.AddParameter(command, "@id", id);
        Database.AddParameter(command, "@firstName", firstName);
        Database.AddParameter(command, "@lastName", lastName);
        Database.AddParameter(command, "@email", email);
        command.ExecuteNonQuery();

        if (before is not null)
        {
            database.Audit.Record(connection, transaction, AuditActions.UserUpdated,
                $"{before.Username}: nimi tai sähköposti");
        }

        transaction.Commit();
    }

    /// <summary>Sets a user's name, for the command line.</summary>
    public void SetName(int id, string firstName, string lastName)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var before = ReadById(connection, transaction, id);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE Users SET FirstName = @firstName, LastName = @lastName WHERE Id = @id;";
        Database.AddParameter(command, "@id", id);
        Database.AddParameter(command, "@firstName", firstName);
        Database.AddParameter(command, "@lastName", lastName);
        command.ExecuteNonQuery();

        if (before is not null)
        {
            database.Audit.Record(connection, transaction, AuditActions.UserUpdated, $"{before.Username}: nimi");
        }

        transaction.Commit();
    }

    /// <summary>
    /// True when <paramref name="password"/> is this user's current one. What
    /// the profile view checks before allowing a password change, so knowing
    /// the old password is required to set a new one.
    /// </summary>
    public bool VerifyPassword(int id, string password)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PasswordHash FROM Users WHERE Id = @id;";
        Database.AddParameter(command, "@id", id);

        return PasswordHasher.Verify(password, command.ExecuteScalar() as string);
    }

    /// <summary>One user by row id, re-read after a change.</summary>
    public User? FindUserById(int id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Username, Email, FirstName, LastName, Role FROM Users WHERE Id = @id;";
        Database.AddParameter(command, "@id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Replaces a user's password with a fresh hash of a new one.</summary>
    public void SetPassword(int id, string password)
    {
        var hash = PasswordHasher.Hash(password);

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var before = ReadById(connection, transaction, id);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A new password also lifts any lock: an administrator setting one is
        // the usual answer to a user who locked themselves out.
        command.CommandText =
            "UPDATE Users SET PasswordHash = @passwordHash, FailedSignIns = 0, LockedUntil = NULL WHERE Id = @id;";
        Database.AddParameter(command, "@id", id);
        Database.AddParameter(command, "@passwordHash", hash);
        command.ExecuteNonQuery();

        // Who and when only: neither the password nor its hash is recorded.
        if (before is not null)
        {
            database.Audit.Record(connection, transaction, AuditActions.PasswordChanged, before.Username);
        }

        transaction.Commit();
    }

    public void DeleteUser(int id)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var before = ReadById(connection, transaction, id);

        if (before is { Role: UserRole.Admin } && CountAdmins(connection, transaction) <= 1)
        {
            throw new InvalidOperationException(
                $"{before.Username} on ainoa ylläpitäjä. Lisää ensin toinen ylläpitäjä.");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM Users WHERE Id = @id;";
        Database.AddParameter(command, "@id", id);
        command.ExecuteNonQuery();

        if (before is not null)
        {
            database.Audit.Record(connection, transaction, AuditActions.UserDeleted, before.Display);
        }

        transaction.Commit();
    }

    private static int CountAdmins(DbConnection connection, DbTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM Users WHERE Role = @role;";
        Database.AddParameter(command, "@role", UserRoleNames.ToStorage(UserRole.Admin));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>One user inside a write's transaction, for the audit entry describing it.</summary>
    private static User? ReadById(DbConnection connection, DbTransaction transaction, int id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT Id, Username, Email, FirstName, LastName, Role FROM Users WHERE Id = @id;";
        Database.AddParameter(command, "@id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
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
        command.CommandText = "SELECT COUNT(*) FROM Users WHERE Role = @role;";
        Database.AddParameter(command, "@role", UserRoleNames.ToStorage(UserRole.Admin));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Puts the default administrator into an empty table and returns the
    /// password it was given, so the caller can tell the user. Returns null
    /// when there were already users and nothing was done.
    ///
    /// Keyed off the table being empty rather than the database being new,
    /// unlike the catalogue: the table is new to databases that already exist,
    /// and an empty one would otherwise mean nobody can ever open Admin again.
    /// </summary>
    /// <remarks>
    /// Only the hash is stored, so nothing can read the password back out of
    /// the database afterwards — even this trivial one. Returning it is how
    /// the caller comes to know what to display.
    /// </remarks>
    public string? EnsureDefaultAdmin()
    {
        if (Count() > 0)
        {
            return null;
        }

        InsertUser(DefaultUsername, DefaultEmail, DefaultPassword, UserRole.Admin);

        return DefaultPassword;
    }

    /// <summary>
    /// A real hash of a throwaway password, used to spend the same time on an
    /// unknown username as on a known one. Built once, lazily, because
    /// deriving it costs as much as a login.
    /// </summary>
    private static readonly Lazy<string> DummyHash =
        new(() => PasswordHasher.Hash("nobody"), LazyThreadSafetyMode.ExecutionAndPublication);

    private static DateTimeOffset? ReadTimestamp(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private (User User, string Hash, int Failures, DateTimeOffset? LockedUntil)? FindWithHash(string usernameOrEmail)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();

        // Either name matches whatever the case, without lower() defeating the
        // unique index: COLLATE NOCASE under SQLite and citext under
        // PostgreSQL. Which of the two needs saying in the SQL is the
        // dialect's business — see SqlDialect.TextEquals.
        command.CommandText =
            $"""
            SELECT Id, Username, Email, FirstName, LastName, Role, PasswordHash, FailedSignIns, LockedUntil
            FROM Users
            WHERE {database.Dialect.TextEquals("Username", "@name")}
               OR {database.Dialect.TextEquals("Email", "@name")}
            LIMIT 1;
            """;
        Database.AddParameter(command, "@name", usernameOrEmail.Trim());

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (Read(reader), reader.GetString(6), reader.GetInt32(7), ReadTimestamp(reader, 8))
            : null;
    }

    /// <summary>
    /// Reads a row selected as
    /// <c>Id, Username, Email, FirstName, LastName, Role</c>. Every query here
    /// selects those six first, in that order, so a hash that follows them
    /// does not shift the ones this reads.
    /// </summary>
    private static User Read(DbDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        UserRoleNames.FromStorage(reader.GetString(5)));
}
