using System;
using System.Linq;

namespace JussiMiniPos.Models;

/// <summary>
/// Who may use the application, as stored in the Users table. The password is
/// not here in any form: only <c>Users.PasswordHash</c> holds anything about
/// it, and that never leaves <c>PasswordHasher</c>.
/// </summary>
/// <param name="Id">Row id.</param>
/// <param name="Username">What is typed at the login prompt.</param>
/// <param name="Email">Also accepted at the login prompt.</param>
/// <param name="FirstName">Given name, empty when none has been entered.</param>
/// <param name="LastName">Family name, empty when none has been entered.</param>
/// <param name="Role">What the user is allowed to do.</param>
public sealed record User(
    int Id,
    string Username,
    string Email,
    string FirstName,
    string LastName,
    UserRole Role)
{
    /// <summary>Only an administrator may open Admin.</summary>
    public bool CanOpenAdmin => Role == UserRole.Admin;

    /// <summary>
    /// "Matti Meikäläinen", or as much of it as has been filled in. Empty when
    /// neither name is set — <see cref="Name"/> is what callers want.
    /// </summary>
    public string FullName => string.Join(' ',
        new[] { FirstName, LastName }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>
    /// What to call this user on screen: their name when there is one, and the
    /// username otherwise. The seeded administrator has no name, so something
    /// always has to stand in.
    /// </summary>
    public string Name => FullName.Length > 0 ? FullName : Username;

    /// <summary>"Matti Meikäläinen (Ylläpitäjä)", for the line that says who is signed in.</summary>
    public string Display => $"{Name} ({UserRoleNames.Finnish(Role)})";
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
