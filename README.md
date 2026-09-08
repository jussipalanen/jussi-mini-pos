# JussiMiniPos

A small point-of-sale (POS) desktop application built with WPF on .NET 10.

> **Status:** in progress. The checkout (Kassa) works end to end — product
> search, cart, payment and storing the sale. Tuotteet, Myynti and Raportit are
> still placeholders.

## Requirements

- **Windows** (WPF does not run on Linux or macOS)
- **.NET SDK 10.0** or newer — <https://dotnet.microsoft.com/download>
- Optional: Visual Studio 2026 or JetBrains Rider for XAML designer support

Verify the SDK is installed and on your `PATH`:

```powershell
dotnet --version      # should print 10.x
```

## Running the application locally

### 1. Get the source

```powershell
git clone git@github.com:jussipalanen/jussi-mini-pos.git
cd jussi-mini-pos
```

### 2. Restore dependencies

```powershell
dotnet restore
```

### 3. Build and run

```powershell
dotnet run
```

The WPF main window opens. `dotnet run` builds first, so a separate
`dotnet build` is only needed when you want to compile without launching.

Stop the app by closing its window, or with `Ctrl+C` in the terminal.

### Running from Visual Studio

1. Open `JussiMiniPos.csproj` (File → Open → Project/Solution).
2. Make sure `JussiMiniPos` is the startup project.
3. Press `F5` to run with the debugger, or `Ctrl+F5` to run without it.

### Running the compiled executable directly

After a build, the executable is in the build output folder:

```powershell
dotnet build
.\bin\Debug\net10.0-windows\JussiMiniPos.exe
```

### Release build

```powershell
dotnet run -c Release
# or produce a distributable folder:
dotnet publish -c Release -r win-x64 --self-contained false
```

The published output lands in `bin\Release\net10.0-windows\win-x64\publish\`.

### Troubleshooting

| Problem | Fix |
| ------- | --- |
| `dotnet` is not recognized | Install the .NET 10 SDK and reopen the terminal |
| `NETSDK1045: The current .NET SDK does not support targeting net10.0` | Your SDK is older than 10.0 — upgrade it |
| Build errors after pulling changes | `dotnet clean` then `dotnet restore` |
| The app builds but no window appears | Check `StartupUri` in `App.xaml` points at `MainWindow.xaml` |

## Project layout

| Path                   | Purpose                                                |
| ---------------------- | ------------------------------------------------------ |
| `JussiMiniPos.csproj`  | Project file (`net10.0-windows`, WPF enabled)          |
| `App.xaml(.cs)`        | Entry point, merged resources, `fi-FI` culture setup   |
| `MainWindow.xaml(.cs)` | Shell window; hosts one view and handles navigation    |
| `Views/`               | `StartView`, `CheckoutView`, `PaymentView`             |
| `Models/`              | `Product`, `CartLine`, `Sale`, `PaymentMethod`         |
| `Services/`            | `Database`, `SalesRepository`, `CatalogSeeder`, `ProductCatalog` |
| `CommandLine.cs`       | `--seed` / `--dump` / `--clear` handling               |
| `Assets/`              | `Styles.xaml`, `Icons.xaml` and the Lucide `.svg` sources |
| `AssemblyInfo.cs`      | Assembly-level theme configuration                     |

Build output (`bin/`, `obj/`) is generated locally and is not tracked in git.

## Conventions

- **Code is English, the UI is Finnish.** Class, method and resource names use
  English (`CheckoutView`, `Icon.Checkout`, `PaymentMethod.Card`); only strings
  the user reads are Finnish. `AppViewNames` and `PaymentMethodNames` map
  between the two and are the seam to replace if real localisation is added.
- **Prices are euros**, held as `decimal` in memory and formatted through
  `fi-FI`, so they render as `2,50 €`.

## Database

Completed sales are stored in SQLite at:

```
%LOCALAPPDATA%\JussiMiniPos\jussiminipos.db
```

The file and its schema are created on first run by `Database.EnsureCreated()`;
delete the file to start over. Every statement is `IF NOT EXISTS`, so adding
tables to an existing database is safe.

On startup the app also seeds the catalogue tables **if they are empty**, so a
fresh install has something to look at. Once there are rows it does nothing, so
hand-edited data is never overwritten.

### Command line

The same exe doubles as a catalogue tool. It has no console of its own, so it
attaches to the terminal that started it:

```powershell
JussiMiniPos.exe --seed           # fill Categories/Products with demo data
JussiMiniPos.exe --seed --reset   # replace any existing catalogue rows
JussiMiniPos.exe --dump           # print the catalogue tables
JussiMiniPos.exe --clear          # delete the catalogue rows (sales are kept)
JussiMiniPos.exe --help
```

Seeding writes 7 categories (two of them nested under *Juomat*), the 20 demo
products with prices in euros (four of them on offer), 2 placeholder image rows
each, and 27 product/category links — seven products sit in two categories, to
exercise the link table.

Because this is a `WinExe`, PowerShell does not wait for it and the prompt can
come back before the output does. Pipe it to make the shell wait:

```powershell
JussiMiniPos.exe --dump | Out-String
```

None of the catalogue commands touch the `Sales` tables.

### Catalogue

```
Categories                    Products
├── Id                        ├── Id
├── Title                     ├── Title
├── ParentId → Categories.Id  ├── Description
└── IsPublic (0/1)            ├── FeatureImage
                              ├── PriceCents
ProductImages                 ├── SalePriceCents  (NULL = not on offer)
├── Id                        └── IsPublic (0/1)
├── ProductId → Products.Id
├── Path                      ProductCategories
└── SortOrder                 ├── ProductId  → Products.Id
                              └── CategoryId → Categories.Id
                                  (composite primary key)
```

`Categories.ParentId` is a self-reference, so categories nest. A product can
belong to several categories at once, so that link lives in its own table
rather than a column; its extra images do too.

`SalePriceCents` is `NULL` when there is no offer, so "is this discounted" is a
null check rather than a sentinel price.

Products in a subcategory are also linked to its parent, so filtering by one
category id needs no join. To pull a whole branch instead, walk `ParentId`:

```sql
WITH RECURSIVE tree(Id) AS (
    SELECT 1                       -- the category you want
    UNION ALL
    SELECT c.Id FROM Categories c JOIN tree t ON c.ParentId = t.Id
)
SELECT DISTINCT p.* FROM Products p
JOIN ProductCategories pc ON pc.ProductId = p.Id
WHERE pc.CategoryId IN (SELECT Id FROM tree);
```

### Schema changes

`Database.EnsureCreated()` also runs `ApplyMigrations`, which adds columns that
were introduced after a table already existed — `CREATE TABLE IF NOT EXISTS`
cannot do that. Columns added this way land at the end of the table, so column
*order* differs between a fresh and an upgraded database; nothing selects `*`,
so that only matters if you read the schema by eye.

### Sales

```
Sales                         SaleItems
├── Id                        ├── Id
├── DateTime   (ISO 8601)     ├── SaleId  → Sales.Id
├── TotalCents (integer)      ├── ProductId
└── PaymentMethod             ├── Name
                              ├── Category
                              ├── UnitPriceCents
                              └── Quantity
```

A sale and its items are written in one transaction, and only after the payment
succeeds — cancelling or failing a payment leaves nothing in the database.
Sold lines copy the name and price, so later catalogue edits never rewrite
past receipts.

### Conventions

- Column names are **PascalCase** throughout, matching the C# side.
- Booleans are `INTEGER` `0`/`1` with a `CHECK`, since SQLite has no boolean.
- Money is an **integer number of cents**, not `REAL`. SQLite has no decimal
  type, and binary floating point cannot hold values like `0.10` exactly, so a
  column of `REAL` totals drifts once you start summing it for reports.
  `SalesRepository.FromCents` converts back for display.
- Foreign keys are enforced: `Database.OpenConnection()` sets
  `PRAGMA foreign_keys = ON`, which SQLite otherwise leaves off per connection.

## License

Not yet specified.
