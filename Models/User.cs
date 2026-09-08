using System;

namespace JussiMiniPos.Models;

/// <summary>
/// Who may use the application, as stored in the Users table. The password is
/// not here in any form: only <c>Users.PasswordHash</c> holds anything about
/// it, and that never leaves <c>PasswordHasher</c>.
/// </summary>
/// <param name="Id">Row id.</param>
/// <param name="Username">What is typed at the login prompt.</param>
/// <param name="Email">Also accepted at the login prompt.</param>
/// <param name="Role">What the user is allowed to do.</param>
public sealed record User(int Id, string Username, string Email, UserRole Role)
{
    /// <summary>Only an administrator may open Admin.</summary>
    public bool CanOpenAdmin => Role == UserRole.Admin;

    /// <summary>"admin (Ylläpitäjä)", for the line that says who is signed in.</summary>
    public string Display => $"{Username} ({UserRoleNames.Finnish(Role)})";
}

/// <summary>
/// What a user is allowed to do. Stored as the lower-case names in
/// <c>Users.Role</c>, which a CHECK constraint restricts to exactly these.
/// </summary>
public enum UserRole
{
    /// <summary>Full access, including Admin.</summary>
    Admin,

    /// <summary>Reserved for later; no extra rights yet.</summary>
    Manager,

    /// <summary>Reserved for later; no extra rights yet.</summary>
    Seller,
}

/// <summary>
/// Maps <see cref="UserRole"/> to the strings the database stores and the
/// Finnish labels the user reads — the same split as <c>AppViewNames</c>.
/// </summary>
public static class UserRoleNames
{
    public static string Finnish(UserRole role) => role switch
    {
        UserRole.Admin => "Ylläpitäjä",
        UserRole.Manager => "Esimies",
        UserRole.Seller => "Myyjä",
        _ => role.ToString(),
    };

    /// <summary>The value written to <c>Users.Role</c>.</summary>
    public static string ToStorage(UserRole role) => role.ToString().ToLowerInvariant();

    /// <summary>
    /// Reads a stored role. Anything unrecognised becomes the least
    /// privileged role rather than the most: a row that should not parse must
    /// not be a way into Admin.
    /// </summary>
    public static UserRole FromStorage(string? value) =>
        Enum.TryParse<UserRole>(value, ignoreCase: true, out var role) ? role : UserRole.Seller;
}
