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
    /// <summary>
    /// When the account is locked after too many failed sign-ins, until
    /// when; null when it is not. Only filled in where the user list needs it.
    /// </summary>
    public DateTimeOffset? LockedUntil { get; init; }

    public bool IsLocked => LockedUntil is { } until && until > DateTimeOffset.Now;

    /// <summary>"Esimies", for lists.</summary>
    public string RoleText => UserRoleNames.Finnish(Role);

    /// <summary>"Lukittu klo 14.35 asti", or empty, for the user list.</summary>
    public string LockText => IsLocked ? $"Lukittu klo {LockedUntil!.Value.ToLocalTime():HH.mm} asti" : string.Empty;

    /// <summary>Only an administrator may open Admin.</summary>
    public bool CanOpenAdmin => Role == UserRole.Admin;

    /// <summary>Only an administrator adds, changes, unlocks or removes accounts.</summary>
    public bool CanManageUsers => Role == UserRole.Admin;

    /// <summary>
    /// Every signed-in user may sell. A seller can do nothing else; Kassa is
    /// what the role is for.
    /// </summary>
    public bool CanUseCheckout => true;

    /// <summary>Tuotteet, categories and VAT rates: administrators and managers.</summary>
    public bool CanManageCatalogue => Role is UserRole.Admin or UserRole.Manager;

    /// <summary>Myynti, the full sales history: administrators and managers.</summary>
    public bool CanOpenSales => Role is UserRole.Admin or UserRole.Manager;

    /// <summary>Sales reports are available to administrators and managers.</summary>
    public bool CanOpenReports => Role is UserRole.Admin or UserRole.Manager;

    /// <summary>
    /// Voiding takes a sale out of the takings, so it is limited to the
    /// people who answer for them: administrators and managers.
    /// </summary>
    public bool CanVoidSales => Role is UserRole.Admin or UserRole.Manager;

    /// <summary>
    /// Who may give money back against a sale. Administrators and managers
    /// may refund any sale; a seller only their own sales of the day, which is
    /// what a customer returning to the till needs and no more.
    /// </summary>
    /// <param name="soldBy">The username recorded on the sale.</param>
    /// <param name="soldToday">Whether the sale was made today, Finnish time.</param>
    public bool CanRefund(string? soldBy, bool soldToday) =>
        Role is UserRole.Admin or UserRole.Manager
        || (soldToday && string.Equals(soldBy, Username, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>May open sales reports.</summary>
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
