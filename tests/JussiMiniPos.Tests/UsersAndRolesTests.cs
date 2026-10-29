using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Tests;

/// <summary>
/// Roadmap 2.3: the last-administrator rule, account lockout, what each role
/// may open, the idle sign-out, and the seller recorded on a sale.
/// </summary>
public sealed class UsersAndRolesTests
{
    private const string Password = "Oikea-salasana-123";

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void The_last_administrator_cannot_be_demoted_or_deleted(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var users = new UserRepository(test.Database);
        var admin = users.InsertUser("paakayttaja", "paa@example.com", Password, UserRole.Admin);

        Assert.Throws<InvalidOperationException>(() =>
            users.UpdateUser(admin, "paakayttaja", "paa@example.com", UserRole.Manager));
        Assert.Throws<InvalidOperationException>(() => users.DeleteUser(admin));
        Assert.Equal(UserRole.Admin, users.FindUserById(admin)?.Role);

        // With a second administrator, either may go.
        var second = users.InsertUser("toinen", "toinen@example.com", Password, UserRole.Admin);
        users.UpdateUser(admin, "paakayttaja", "paa@example.com", UserRole.Manager);
        Assert.Equal(1, users.CountAdmins());
        Assert.Throws<InvalidOperationException>(() => users.DeleteUser(second));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Five_wrong_passwords_lock_the_account_even_against_the_right_one(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var users = new UserRepository(test.Database);
        users.InsertUser("matti", "matti@example.com", Password, UserRole.Seller);

        for (var i = 1; i < UserRepository.MaxFailedSignIns; i++)
        {
            Assert.Equal(SignInStatus.WrongCredentials, users.SignIn("matti", "väärä").Status);
        }

        var locked = users.SignIn("matti", "väärä");
        Assert.Equal(SignInStatus.LockedOut, locked.Status);
        Assert.InRange(locked.LockedUntil!.Value - DateTimeOffset.Now,
            UserRepository.LockoutDuration - TimeSpan.FromMinutes(1), UserRepository.LockoutDuration);

        Assert.Equal(SignInStatus.LockedOut, users.SignIn("matti", Password).Status);
        Assert.True(Assert.Single(users.GetUsers()).IsLocked);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Unlocking_or_a_new_password_lets_a_locked_user_in(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var users = new UserRepository(test.Database);
        var id = users.InsertUser("matti", "matti@example.com", Password, UserRole.Seller);
        Lock(users, "matti");

        users.Unlock(id);
        Assert.Equal(SignInStatus.Success, users.SignIn("matti", Password).Status);

        Lock(users, "matti");
        users.SetPassword(id, "Uusi-salasana-456");
        Assert.Equal(SignInStatus.Success, users.SignIn("matti", "Uusi-salasana-456").Status);
    }

    /// <summary>The count is of failures in a row: a correct password starts it again.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_successful_sign_in_resets_the_failure_count(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var users = new UserRepository(test.Database);
        users.InsertUser("matti", "matti@example.com", Password, UserRole.Seller);

        for (var i = 1; i < UserRepository.MaxFailedSignIns; i++)
        {
            users.SignIn("matti", "väärä");
        }

        Assert.Equal(SignInStatus.Success, users.SignIn("matti", Password).Status);
        Assert.Equal(SignInStatus.WrongCredentials, users.SignIn("matti", "väärä").Status);
    }

    /// <summary>An administrator's edit is one write: details, role and a new password together, in one entry.</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void An_account_edit_is_saved_and_logged_as_one_change(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var users = new UserRepository(test.Database);
        var id = users.InsertUser("matti", "matti@example.com", Password, UserRole.Seller);

        users.UpdateAccount(id, "matti", "matti@example.com", UserRole.Manager, "Matti", "Meikäläinen", "Uusi-salasana-456");

        var user = users.FindUserById(id);
        Assert.Equal(("Matti Meikäläinen", UserRole.Manager), (user?.FullName, user?.Role));
        Assert.True(users.VerifyPassword(id, "Uusi-salasana-456"));
        Assert.Equal("matti: rooli Myyjä → Esimies; nimi; uusi salasana", test.Database.Audit.GetEntries()[0].Details);
    }

    [Theory]
    [InlineData(UserRole.Seller, true, false, false, false, false, false)]
    [InlineData(UserRole.Manager, true, true, true, true, false, false)]
    [InlineData(UserRole.Admin, true, true, true, true, true, true)]
    public void Each_role_opens_what_it_is_for(
        UserRole role, bool checkout, bool catalogue, bool sales, bool reports, bool admin, bool users)
    {
        var user = new User(1, "x", "x@example.com", "", "", role);

        Assert.Equal(
            (checkout, catalogue, sales, reports, admin, users),
            (user.CanUseCheckout, user.CanManageCatalogue, user.CanOpenSales, user.CanOpenReports, user.CanOpenAdmin, user.CanManageUsers));
        Assert.Equal(role != UserRole.Seller, user.CanVoidSales);
    }

    [Theory]
    [InlineData(true, 16, false, false, true)]
    [InlineData(true, 14, false, false, false)]
    [InlineData(false, 60, false, false, false)]
    [InlineData(true, 60, true, false, false)]
    [InlineData(true, 60, false, true, false)]
    public void An_idle_session_ends_unless_a_payment_or_a_dialog_is_open(
        bool signedIn, int idleMinutes, bool paying, bool dialog, bool expected)
    {
        Assert.Equal(expected, IdleSession.ShouldSignOut(signedIn, TimeSpan.FromMinutes(idleMinutes), paying, dialog));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_sale_records_who_sold_it(string engine)
    {
        using var test = TestDatabase.Create(engine);
        var sales = new SalesRepository(test.Database);

        sales.Save(new Sale(DateTimeOffset.Now, 1.00m, PaymentMethod.Card, [new SaleItem(1, "Vesi", "", 1.00m, 1)]) { SoldBy = "maija" });

        Assert.Equal("maija", Assert.Single(sales.GetSales()).SoldBy);
    }

    private static void Lock(UserRepository users, string username)
    {
        for (var i = 0; i < UserRepository.MaxFailedSignIns; i++)
        {
            users.SignIn(username, "väärä");
        }

        Assert.Equal(SignInStatus.LockedOut, users.SignIn(username, Password).Status);
    }
}
