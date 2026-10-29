using System.Data.Common;

namespace JussiMiniPos.Tests;

public sealed class SchemaTests
{
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Creating_the_schema_twice_is_harmless(string engine)
    {
        using var test = TestDatabase.Create(engine);

        test.Database.EnsureCreated();
        test.Reopen().EnsureCreated();

        Assert.Equal(0, test.Scalar("SELECT COUNT(*) FROM Products;"));
    }

    /// <summary>
    /// Demo seeding keys off "the database is new", not "the tables are
    /// empty", so emptying the catalogue on purpose stays emptied.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void A_database_is_new_only_on_its_first_run(string engine)
    {
        using var test = TestDatabase.CreateUninitialised(engine);

        test.Database.EnsureCreated();
        Assert.True(test.Database.IsNew);

        var reopened = test.Reopen();
        reopened.EnsureCreated();
        Assert.False(reopened.IsNew);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Foreign_keys_are_enforced_on_every_connection(string engine)
    {
        using var test = TestDatabase.Create(engine);

        Assert.ThrowsAny<DbException>(() =>
            test.Execute("INSERT INTO ProductCategories (ProductId, CategoryId, SortOrder) VALUES (999, 999, 0);"));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public void Booleans_only_accept_zero_or_one(string engine)
    {
        using var test = TestDatabase.Create(engine);

        Assert.ThrowsAny<DbException>(() =>
            test.Execute("INSERT INTO Categories (Title, IsPublic) VALUES ('X', 2);"));
    }
}
