using System;
using System.Security.Cryptography;

namespace JussiMiniPos.Services;

/// <summary>
/// Turns a password into something safe to store, and checks one against it.
///
/// Passwords are hashed, not encrypted: encryption implies a key that can turn
/// the stored value back into the password, and nothing here — not this
/// application, not an administrator, not somebody who takes a copy of the
/// database — should be able to do that. PBKDF2 is deliberately slow, so a
/// stolen hash is expensive to attack by guessing, and every hash gets its own
/// random salt, so two users with the same password do not share a hash and a
/// precomputed table is no help.
/// </summary>
public static class PasswordHasher
{
    /// <summary>
    /// Iteration count, following OWASP's current PBKDF2-SHA256 guidance. It
    /// costs a fraction of a second once per login, which nobody notices, and
    /// multiplies the cost of guessing by the same factor.
    /// </summary>
    /// <summary>
    /// Shortest password that will be stored. Not much of a policy — more a
    /// guard against a typo becoming a one-character password. Lives here so
    /// the profile view and the command line cannot disagree about it.
    /// </summary>
    public const int MinimumLength = 8;

    private const int Iterations = 600_000;

    private const int SaltBytes = 16;

    private const int HashBytes = 32;

    private const string Algorithm = "pbkdf2-sha256";

    /// <summary>
    /// A hash to store, as
    /// <c>pbkdf2-sha256$iterations$salt$hash</c>. Self-describing on purpose:
    /// the iteration count can be raised later without invalidating hashes
    /// written before the change.
    /// </summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations);

        return string.Join('$',
            Algorithm,
            Iterations.ToString(),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>
    /// True when <paramref name="password"/> is the one behind
    /// <paramref name="stored"/>. A malformed or unknown-algorithm hash is a
    /// failure rather than an exception: a corrupt row should refuse a login,
    /// not take down the login window.
    /// </summary>
    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        var parts = stored.Split('$');
        if (parts.Length != 4
            || parts[0] != Algorithm
            || !int.TryParse(parts[1], out var iterations)
            || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Derive(password, salt, iterations, expected.Length);

        // Compared in fixed time, so how long the check takes says nothing
        // about how much of the hash was guessed correctly.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length = HashBytes) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);
}
