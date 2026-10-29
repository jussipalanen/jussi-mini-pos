using System.Data.Common;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Tests;

/// <summary>
/// Signing in and the user table. Each hash costs a real PBKDF2 derivation,
/// so these tests create as few users as they can.
/// </summary>
public sealed class UserRepositoryTests
{
    private const string Password = "Oikea-salasana-123";

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Signing_in_accepts_the_username_or_email_in_any_case(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var users = new UserRepository(test.Database);
        var id = users.InsertUser("Matti", "matti@example.com", Password, UserRole.Manager, "Matti", "Meikäläinen");

        var byUsername = users.Authenticate("MATTI", Password);
        var byEmail = users.Authenticate("Matti@Example.com", Password);

        Assert.Equal(id, byUsername?.Id);
        Assert.Equal(id, byEmail?.Id);
        Assert.Equal(UserRole.Manager, byUsername?.Role);
        Assert.Equal("Matti Meikäläinen", byUsername?.FullName);
    }

    /// <summary>A wrong password and an unknown user get the same answer, so usernames cannot be probed.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_wrong_password_and_an_unknown_user_are_both_refused(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var users = new UserRepository(test.Database);
        users.InsertUser("matti", "matti@example.com", Password, UserRole.Seller);

        Assert.Null(users.Authenticate("matti", "väärä-salasana"));
        Assert.Null(users.Authenticate("maija", Password));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Usernames_and_emails_are_unique_regardless_of_case(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var users = new UserRepository(test.Database);
        users.InsertUser("matti", "matti@example.com", Password, UserRole.Seller);

        Assert.ThrowsAny<DbException>(() =>
            users.InsertUser("MATTI", "toinen@example.com", Password, UserRole.Seller));
        Assert.ThrowsAny<DbException>(() =>
            users.InsertUser("toinen", "MATTI@EXAMPLE.COM", Password, UserRole.Seller));
    }

    /// <summary>
    /// The default administrator is only created into an empty table, and
    /// the administrator count is what the last-administrator guard relies on.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void The_default_administrator_is_created_once(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var users = new UserRepository(test.Database);

        var password = users.EnsureDefaultAdmin();
        Assert.Equal(UserRepository.DefaultPassword, password);
        Assert.Null(users.EnsureDefaultAdmin());
        Assert.Equal(1, users.CountAdmins());

        var admin = users.Authenticate(UserRepository.DefaultUsername, UserRepository.DefaultPassword);
        Assert.NotNull(admin);
        Assert.True(admin.CanOpenAdmin);
        Assert.True(admin.CanOpenReports);

        users.InsertUser("myyja", "myyja@example.com", Password, UserRole.Seller);
        Assert.Equal(1, users.CountAdmins());
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_changed_password_replaces_the_old_one(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var users = new UserRepository(test.Database);
        var id = users.InsertUser("matti", "matti@example.com", Password, UserRole.Seller);

        users.SetPassword(id, "Uusi-salasana-456");

        Assert.False(users.VerifyPassword(id, Password));
        Assert.True(users.VerifyPassword(id, "Uusi-salasana-456"));
    }

    [Theory]
    [InlineData(UserRole.Admin, true, true)]
    [InlineData(UserRole.Manager, false, true)]
    [InlineData(UserRole.Seller, false, false)]
    public void Roles_decide_who_opens_Admin_and_Raportit(UserRole role, bool admin, bool reports)
    {
        var user = new User(1, "matti", "matti@example.com", "", "", role);

        Assert.Equal(admin, user.CanOpenAdmin);
        Assert.Equal(reports, user.CanOpenReports);
    }
}
