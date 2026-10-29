using JussiMiniPos.Services;

namespace JussiMiniPos.Tests;

/// <summary>
/// Every step in <c>Database.ApplyMigrations</c>, run against a SQLite file
/// shaped like one written by an older version. PostgreSQL databases are
/// always created from the current schema, so the migrations are SQLite-only.
/// </summary>
public sealed class MigrationTests
{
    /// <summary>
    /// Users before names, Products before prices, and ProductCategories
    /// before SortOrder, when SQLite's implicit rowid carried link order.
    /// </summary>
    private const string OldSchema =
        """
        CREATE TABLE Users (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            Username     TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            Email        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            PasswordHash TEXT    NOT NULL,
            Role         TEXT    NOT NULL CHECK (Role IN ('admin', 'manager', 'seller'))
        );
        CREATE TABLE Categories (
            Id       INTEGER PRIMARY KEY AUTOINCREMENT,
            Title    TEXT    NOT NULL,
            ParentId INTEGER     NULL REFERENCES Categories(Id) ON DELETE RESTRICT,
            IsPublic INTEGER NOT NULL DEFAULT 1 CHECK (IsPublic IN (0, 1))
        );
        CREATE TABLE Products (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            Title        TEXT    NOT NULL,
            Description  TEXT    NOT NULL DEFAULT '',
            FeatureImage TEXT        NULL,
            IsPublic     INTEGER NOT NULL DEFAULT 1 CHECK (IsPublic IN (0, 1))
        );
        CREATE TABLE ProductCategories (
            ProductId  INTEGER NOT NULL REFERENCES Products(Id) ON DELETE CASCADE,
            CategoryId INTEGER NOT NULL REFERENCES Categories(Id) ON DELETE CASCADE,
            PRIMARY KEY (ProductId, CategoryId)
        );
        CREATE TABLE Sales (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            "DateTime"    TEXT    NOT NULL,
            TotalCents    INTEGER NOT NULL,
            PaymentMethod TEXT    NOT NULL
        );
        CREATE TABLE SaleItems (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            SaleId         INTEGER NOT NULL REFERENCES Sales(Id) ON DELETE CASCADE,
            ProductId      INTEGER NOT NULL,
            Name           TEXT    NOT NULL,
            Category       TEXT    NOT NULL,
            UnitPriceCents INTEGER NOT NULL,
            Quantity       INTEGER NOT NULL
        );

        -- A sale from before VAT was recorded.
        INSERT INTO Sales ("DateTime", TotalCents, PaymentMethod) VALUES ('2026-01-10T12:00:00.0000000+02:00', 250, 'Card');
        INSERT INTO SaleItems (SaleId, ProductId, Name, Category, UnitPriceCents, Quantity)
        VALUES (1, 1, 'Vanha tuote', 'Ensin linkitetty', 250, 1);

        INSERT INTO Users (Username, Email, PasswordHash, Role)
        VALUES ('vanha', 'vanha@example.com', 'x', 'admin');

        INSERT INTO Categories (Id, Title) VALUES (1, 'Ensin luotu'), (2, 'Ensin linkitetty');
        INSERT INTO Products (Id, Title) VALUES (1, 'Vanha tuote');

        -- Linked in the opposite order to the ids, so a backfill that used
        -- the category id instead of the rowid would get it wrong.
        INSERT INTO ProductCategories (ProductId, CategoryId) VALUES (1, 2);
        INSERT INTO ProductCategories (ProductId, CategoryId) VALUES (1, 1);
        """;

    [Fact]
    public void An_old_database_is_brought_up_to_the_current_schema()
    {
        using var test = TestDatabase.CreateUninitialised(TestDatabase.Sqlite);
        test.Execute(OldSchema);

        // What the application does on start-up, against an existing file.
        var database = test.Reopen();
        database.EnsureCreated();

        Assert.False(database.IsNew);

        var product = Assert.Single(new CatalogRepository(database).GetProducts(publicOnly: false));
        Assert.Equal(0m, product.Price);
        Assert.Null(product.SalePrice);
        Assert.Equal(product.Price, product.EffectivePrice);

        // The SortOrder backfill keeps the link written first as the primary category.
        Assert.Equal("Ensin linkitetty", product.PrimaryCategory);

        // Products from before VAT get the general rate, so VAT is never under-reported.
        Assert.Equal(2550, product.Vat.BasisPoints);
        Assert.Equal([2550, 1350, 1000, 0], new CatalogRepository(database).GetVatRates().Select(rate => rate.BasisPoints));

        // A line sold before VAT keeps "not recorded" rather than an invented figure.
        var oldLine = Assert.Single(Assert.Single(new SalesRepository(database).GetSales()).Items);
        Assert.Null(oldLine.VatBasisPoints);
        Assert.Null(oldLine.VatAmount);

        var user = new UserRepository(database).FindUser("VANHA");
        Assert.NotNull(user);
        Assert.Equal(string.Empty, user.FirstName);
        Assert.Equal(string.Empty, user.LastName);
    }

    [Fact]
    public void Migrating_twice_changes_nothing()
    {
        using var test = TestDatabase.CreateUninitialised(TestDatabase.Sqlite);
        test.Execute(OldSchema);

        test.Reopen().EnsureCreated();
        test.Reopen().EnsureCreated();

        var product = Assert.Single(new CatalogRepository(test.Database).GetProducts(publicOnly: false));
        Assert.Equal("Ensin linkitetty", product.PrimaryCategory);
    }
}
