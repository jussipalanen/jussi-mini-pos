# Jussi mini-POS

A small point-of-sale (POS) desktop application built with WPF on .NET 10.

> **Status:** in progress. Kassa works end to end — product search, cart,
> payment and storing the sale. Tuotteet manages the catalogue: search, paging,
> view, add, edit, delete, images, and category management. Myynti lists past
> sales with a receipt view. Kassa also has an AI assistant that recommends
> products out of the catalogue, an Admin view for its settings, users with
> roles, and a profile view. Raportit provides date-filtered sales summaries
> and daily, product and category breakdowns for administrators and managers.
> Data is stored in SQLite by default, or in PostgreSQL when several tills
> share one catalogue and one sales history.

## Requirements

- **Windows** (WPF does not run on Linux or macOS)
- **.NET SDK 10.0** or newer — <https://dotnet.microsoft.com/download>
- Optional: Visual Studio 2026 or JetBrains Rider for XAML designer support
- Optional: **PostgreSQL 13 or newer**, only if you want to run against
  PostgreSQL rather than the default SQLite — see
  [Choosing the database driver](#choosing-the-database-driver). A
  `docker-compose.yml` is included for a local one.

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
| `CHANGELOG.md`         | What changed in each release                            |
| `App.xaml(.cs)`        | Entry point, merged resources, `fi-FI` culture setup   |
| `MainWindow.xaml(.cs)` | Shell window; hosts one view and handles navigation    |
| `Views/`               | `StartView`, `CheckoutView`, `PaymentView`, `ProductsView`, `CategoriesView`, `SalesView`, `ReportsView`, `AdminView`, `ProfileView`, `LoginWindow` and their dialogs; `Pager` is shared paging state |
| `Models/`              | `Product`, `Category`, `CartLine`, `Sale`, `SalesReport`, `PaymentMethod`, `User` |
| `Services/`            | `Database`, `CatalogRepository`, `SalesRepository`, `ReportsRepository`, `CatalogSeeder`, `ImageStore`, `DemoCatalog`, `OptionsRepository`, `UserRepository`, `PasswordHasher`, `PasswordGenerator`, `AppInfo`, and the assistant's `ProductSearch`, `ShoppingAssistant`, `GeminiClient`, `AiSettings` |
| `CommandLine.cs`       | `--seed` / `--dump` / `--clear` / `--ask` / `--user-*` / `--version` handling |
| `Assets/`              | `Styles.xaml`, `Icons.xaml`, the Lucide `.svg` sources and `jussi-mini-pos-logo.svg` |
| `Assets/Icons/icon/`   | Application icon; `favicon.ico` is embedded in the exe  |
| `AssemblyInfo.cs`      | Assembly-level theme configuration                     |

Build output (`bin/`, `obj/`) is generated locally and is not tracked in git.

## Conventions

- **Code is English, the UI is Finnish.** Class, method and resource names use
  English (`CheckoutView`, `Icon.Checkout`, `PaymentMethod.Card`); only strings
  the user reads are Finnish. `AppViewNames` and `PaymentMethodNames` map
  between the two and are the seam to replace if real localisation is added.
- **Prices are euros**, held as `decimal` in memory and formatted through
  `fi-FI`, so they render as `2,50 €`.
- **SVGs are design sources, not assets WPF loads.** WPF cannot render SVG at
  all, so `Assets/Icons/*.svg` are kept for reference and their shapes live in
  `Icons.xaml` as `PathGeometry`. The logo follows the same rule: the
  `Logo` and `Logo.Mark` styles in `Styles.xaml` redraw
  `jussi-mini-pos-logo.svg` in XAML, which also avoids depending on the
  `'Anthropic Sans'` font that file asks for and nobody has installed. Both are
  laid out on the SVG's own 170×60 canvas inside a `Viewbox`, so setting
  `Width` or `Height` at the usage site keeps the proportions:

  ```xml
  <ContentControl Style="{StaticResource Logo}" Width="300" />
  ```

  Its colours are `Brush.Brand` and `Brush.BrandText`, kept apart from
  `Brush.Accent` on purpose: the logo is the brand and the accent is the
  interface. Point them at `Brush.Accent` to make the logo match the UI blue
  instead.

## Database

JussiMiniPos runs on **SQLite** or **PostgreSQL**. SQLite is the default and
needs no configuration, no server and no setup — a first run creates the file
and everything in it. PostgreSQL is there for when one catalogue and one sales
history have to be shared by more than one till.

By default, completed sales are stored in SQLite at:

```
%LOCALAPPDATA%\JussiMiniPos\jussiminipos.db
```

The schema is created on first run by `Database.EnsureCreated()`. Every
statement is `IF NOT EXISTS`, so adding tables to an existing database is safe,
and the same call brings an older database up to date — see
[Schema changes](#schema-changes).

On a **brand new database** the app seeds the catalogue, so a fresh install has
something to look at. Under SQLite it keys off the file being new rather than
the tables being empty — otherwise `--clear`, or deleting the last product by
hand, would be undone by the next restart. Delete the database file to start
over. Under PostgreSQL there is no file to test, so it asks the schema instead:
new means the tables were not there yet.

### Choosing the database driver

The driver is configured in a **`database.env` file**, not in the application's
own settings. Everything in Admin lives in the `Options` table — but that table
is *inside* the database, and reading it needs a connection, so it cannot be
the thing that says how to connect. The configuration has to come from outside
the database, which leaves a file and the environment.

`database.env` is looked for in two places, in order:

1. Beside `JussiMiniPos.exe` — what a deployment ships, and what a developer
   drops into the working copy.
2. `%LOCALAPPDATA%\JussiMiniPos\` — reachable without administrator rights on a
   machine where the install folder is read-only.

Every setting can also be given as an **environment variable of the same
name**. Where both exist the file wins: the file is what an administrator edits
on purpose, so a stray variable in a shell cannot quietly point a till at the
wrong database.

Copy [`database.env.example`](database.env.example) to `database.env` to start.
The real file is gitignored, because it holds a password.

| Setting | Meaning |
| --- | --- |
| `JUSSIMINIPOS_DB_PROVIDER` | `sqlite` (default) or `postgresql` |
| `JUSSIMINIPOS_DB_PATH` | SQLite file location |
| `JUSSIMINIPOS_DB_HOST` / `_PORT` / `_NAME` / `_USER` / `_PASSWORD` | PostgreSQL connection |
| `JUSSIMINIPOS_DB_CONNECTION` | A complete connection string, used verbatim; wins over the fields above |
| `JUSSIMINIPOS_DATA_DIR` | Where images and the Gemini key live |

> **Images do not follow the database.** Product pictures are files on disk that
> only the database knows the names of, and deleting rows sweeps away the ones
> nothing points at any more. Two databases sharing one image folder would
> therefore have each one's sweep delete the other's pictures — running
> `--seed` against a fresh PostgreSQL database would wipe the SQLite
> database's images. The defaults are separate folders for exactly that reason:
> `%LOCALAPPDATA%\JussiMiniPos` for SQLite and
> `%LOCALAPPDATA%\JussiMiniPos\postgresql` for PostgreSQL. Point both at one
> place with `JUSSIMINIPOS_DATA_DIR` only if that is genuinely what you want.

#### SQLite (the default)

Nothing to do. Leaving `database.env` out entirely is the same as:

```ini
JUSSIMINIPOS_DB_PROVIDER=sqlite
```

Point it somewhere else — a shared folder, a different drive — with
`JUSSIMINIPOS_DB_PATH`.

#### PostgreSQL, locally

A [`docker-compose.yml`](docker-compose.yml) is included for development. It is
a convenience for working on the PostgreSQL driver, **not** how the till is
deployed — nothing in the application knows it is talking to a container.

```console
docker compose up -d
```

Then write `database.env`:

```ini
JUSSIMINIPOS_DB_PROVIDER=postgresql
JUSSIMINIPOS_DB_HOST=localhost
JUSSIMINIPOS_DB_PORT=5432
JUSSIMINIPOS_DB_NAME=jussiminipos
JUSSIMINIPOS_DB_USER=jussipos
JUSSIMINIPOS_DB_PASSWORD=jussipos
```

Create the schema and demo catalogue without starting the UI:

```console
JussiMiniPos.exe --seed
JussiMiniPos.exe --dump
```

`--dump` prints which database it opened, with the password stripped out, so it
doubles as a connection test.

#### PostgreSQL, remote or production

Point the same settings at the server. The database itself has to exist and the
user has to own it — the application creates its own tables, but not the
database that holds them:

```console
createdb -h db.example.com -U postgres -O jussipos jussiminipos
```

For anything the individual fields cannot express — TLS, pooling, a non-default
schema — give the whole connection string instead:

```ini
JUSSIMINIPOS_DB_PROVIDER=postgresql
JUSSIMINIPOS_DB_CONNECTION=Host=db.example.com;Port=5432;Database=jussiminipos;Username=jussipos;Password=secret;SSL Mode=Require
```

The user needs `CREATE` on the database for the first run, because that is when
the schema is written. It also needs to be able to run
`CREATE EXTENSION citext` once — that is what makes a username or an email
match whatever the case, the way `COLLATE NOCASE` does under SQLite. On a
managed server where creating extensions is restricted, have an administrator
run `CREATE EXTENSION citext;` in the database first; the application's own
statement is `IF NOT EXISTS` and will then do nothing.

**Keeping the password out of plain text.** A password in `database.env` is
readable by anyone who can read the file. It can instead be stored the way the
Gemini API key is — encrypted with Windows DPAPI under the current user
account, written as `DPAPI:` followed by base64:

```ini
JUSSIMINIPOS_DB_PASSWORD=DPAPI:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA...
```

A value without the `DPAPI:` marker is treated as a plain password, so a file
written by hand keeps working. As with the API key, this defends against
another Windows account and against a copy taken to another machine — not
against code running as the same user, which is the honest limit of storing a
credential a program must read unattended.

#### Switching drivers

The two databases are separate stores, not two views of one. Changing the
provider points the application at a different database; it does not move any
data across. There is no migration tool — export and import with each engine's
own tools if you need the rows to follow.

#### What differs between the two

Almost nothing, by design. Parameters are written `@name`, which both drivers
accept, and every reader reads columns by position, so PostgreSQL folding
unquoted column names to lower case changes nothing. What genuinely differs
lives in `Services/SqlDialect.cs`: the schema, case-insensitive text, the
Unicode-aware folding the search needs, the local-date conversion the reports
need, and `LEAST`/`string_agg` against SQLite's `MIN`/`GROUP_CONCAT`.

### Product images

Pictures live beside the database, so the whole `JussiMiniPos` folder is one
backup unit:

```
%LOCALAPPDATA%\JussiMiniPos\images\
```

Adding a picture in the product editor copies it into that folder under a fresh
GUID name and stores the relative path (`images/ab12cd34ef56.jpg`) in the
database — `Products.FeatureImage` for the thumbnail, `ProductImages` for the
gallery. The original file is left where it was, and importing the same picture
twice makes two copies, so removing one product's image can never blank
another's.

Rows can outlive their files, or point at files that were never there, so the
UI treats a missing image as a placeholder rather than an error. Deleting a
product removes its files; `--seed --reset` and `--clear` sweep up anything left
unreferenced.

### Command line

The same exe doubles as a catalogue tool. It has no console of its own, so it
attaches to the terminal that started it:

```powershell
JussiMiniPos.exe --seed           # fill Categories/Products with demo data
JussiMiniPos.exe --seed --reset   # replace any existing catalogue rows
JussiMiniPos.exe --dump           # print the catalogue tables
JussiMiniPos.exe --clear          # delete the catalogue rows (sales are kept)
JussiMiniPos.exe --ask "Mitä sopii kahvin kanssa?"   # one AI assistant question
JussiMiniPos.exe --users          # list the users
JussiMiniPos.exe --version        # print the version and exit
JussiMiniPos.exe --user-add / --user-update / --user-delete   # see "Users" below
JussiMiniPos.exe --help
```

Seeding writes 7 categories (two of them nested under *Juomat*), the 20 demo
products with prices in euros (four of them on offer), and 27 product/category
links — seven products sit in two categories, to exercise the link table. It
leaves images alone: no picture files ship with the app, so seeded products
start without them.

Because this is a `WinExe`, PowerShell does not wait for it and the prompt can
come back before the output does. Pipe it to make the shell wait:

```powershell
JussiMiniPos.exe --dump | Out-String
```

None of the catalogue commands touch the `Sales` or `Users` tables. `--ask` and
`--users` only read; the `--user-*` commands only touch `Users`.

### Catalogue

```
Options                       (application settings; see Admin)
├── Id
├── OptionName  (UNIQUE)
└── OptionValue

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
null check rather than a sentinel price. The till charges
`Product.EffectivePrice`, which is the offer price when one applies.

The checkout reads this catalogue through `CatalogRepository` — editing a row
changes what the till shows and charges on the next start. `DemoCatalog` is
only seed data and is never read at runtime. Rows with `IsPublic = 0` are left
out of both the product list and the filter chips.

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

In practice these only ever fire against a SQLite file old enough to predate
the column. A PostgreSQL database is always created from the current schema, so
every column is there the first time and each check does nothing.

One migration does more than add a column. `ProductCategories.SortOrder` records
the order the links were written, so the first category a product was given
stays its primary one. SQLite could lean on its implicit `rowid` for that;
PostgreSQL has no such column, so the order is now stored outright. An existing
SQLite file is backfilled from that very `rowid`, which keeps the ordering it
already had.

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

- Column names are **PascalCase** throughout, matching the C# side. PostgreSQL
  folds unquoted names to lower case, which is invisible here because every
  reader reads by position rather than by name.
- Booleans are `0`/`1` with a `CHECK` rather than a boolean type, since SQLite
  has none. PostgreSQL keeps the same shape on purpose, so the reading code
  stays the same for both.
- Money is an **integer number of cents**, not a floating point type. SQLite
  has no decimal type, and binary floating point cannot hold values like `0.10`
  exactly, so a column of `REAL` totals drifts once you start summing it for
  reports. `SalesRepository.FromCents` converts back for display.
- Aggregates are wrapped in `CAST(... AS BIGINT)`. SQLite hands back whatever
  the sum fits in, but PostgreSQL widens `SUM` over a `bigint` to `numeric`,
  which `GetInt64` refuses.
- Foreign keys are enforced. Under SQLite `Database.OpenConnection()` sets
  `PRAGMA foreign_keys = ON`, which it otherwise leaves off per connection;
  PostgreSQL always enforces them and needs no equivalent.
- Parameters are written `@name`, which both drivers accept.

## Sales reports (Raportit)

Open **Raportit** from the start screen and sign in as an administrator or
manager. Sellers cannot open reports; this is checked by the navigation shell
using `User.CanOpenReports`, not just by the button's visibility.

The initial report covers the current month through today. Choose **Tänään**,
**Tämä viikko** (Monday through today), **Tämä kuukausi**, or enter inclusive
start and end dates and press **Näytä raportti**. Changing a date clears the
previous result so it cannot be mistaken for the newly selected period.
All reporting dates and drill-down times use Finnish time, including daylight
saving changes, regardless of the offset originally stored with the sale.

- Summary: total recorded sales in euros, transaction count, units sold and
  average transaction value. An empty period displays zero totals.
- **Päivittäin**: one row per day with sales, totals and proportional bars.
  Days without sales are omitted. **Näytä myynnit** opens that day's current
  transactions, with access to the existing receipt details dialog.
- **Tuotteittain**: quantities and revenue grouped by product ID and recorded
  name. A renamed product can have several rows, preserving historical names.
- **Kategorioittain**: quantities and revenue grouped by the single category
  name saved on each sale line. Missing categories display **Ei kategoriaa**.
- Click table column headings to sort, including quantity and revenue.

Reports read the existing `Sales` and `SaleItems` snapshots; no schema change,
remote service or new package is needed. Catalogue edits and product deletion
do not alter historical results. Deleting a sale removes it from reports.
SQL aggregates integer cents and reads all report sections in one transaction.
Queries run off the UI thread and returning to the start screen prevents a late
result from reopening a view. Date conversion happens during queries; the
existing textual timestamp index does not accelerate this Finnish-date filter.

These reports describe recorded sales from the simulated payment flow. They
do not provide bank settlement, VAT, margin, discount savings, cashier-level
analysis, returns, period comparisons or file exports. Those require additional
features and, for several metrics, new sale-time data. Invalid legacy timestamps
are excluded because they cannot be assigned a reporting date reliably.

## AI assistant (AI-avustaja)

The *AI-avustaja* button in the top right of Kassa's product list opens a
dialog where the cashier can ask, in Finnish, what a customer is after —
*"Mitä sopii kahvin kanssa?"*, *"Etsi halpa välipala"*, *"Etsi alle 10 euron
juomia"*. The answer is a list of catalogue products, each with a one-line
reason and a **Lisää ostoskoriin** button that puts it in the cart behind the
dialog.

That button stays live after a click, so clicking it again adds another one —
quicker than going to the cart's steppers for a second coffee. A badge on the
row counts how many have gone in, and it sits in the row's badge line rather
than under the button, whose height is already fixed, so a click can never grow
a card and shift the ones below out from under the pointer. **Lisää kaikki
ostoskoriin** takes one of everything suggested in a single click.

### How it answers

Retrieval augmented generation, in the plain sense:

1. **Search (`ProductSearch`).** The question is reduced to search stems and
   any constraint hiding in it — a price ceiling from *"alle 10 euron"*, a
   nudge towards the cheap end from *"halpa"*. The stems are matched in SQLite
   against product titles, their categories and their descriptions, scored
   title-first, and the best 40 public rows come back. Finnish inflects the
   words a cashier types, so matching is on stems (*"kahvin"* → `kahv`,
   *"juomia"* → `juom`) rather than whole words. A price ceiling is a `WHERE`,
   not a hint: a product over the limit never reaches the model at all. The
   cheap-end nudge is an `ORDER BY` instead — price leads and relevance breaks
   its ties, so *"halvin juoma"* opens with the 2,00 € water where *"juomia"*
   opens with the best keyword match. It only reorders rows that already
   passed the score filter, so asking for something cheap cannot promote a
   bargain that has nothing to do with the question.
2. **Generation (`GeminiClient`).** Those rows, and only those, are sent to
   Google Gemini with the question. The answer comes back through Gemini's
   structured output as product ids plus reasons.
3. **Resolution (`ShoppingAssistant`).** Each id is looked up in the candidate
   list. An id the model invented simply does not resolve, so the dialog cannot
   offer a product, a price or an offer that is not in the catalogue.

Sales history, product images and anything else in the database are never sent.
The catalogue rows that are sent are the ones already shown in the till.

### Configuring it

Everything is in **Admin**, reached from the link at the bottom of the start
screen: whether the assistant runs at all, the API key, and the model. The key
section links straight to [Google AI Studio](https://aistudio.google.com/apikey)
— the free tier is enough for this — and **Testaa yhteys** checks a pasted key
before it is saved, so a wrong one never replaces a working one.

The test deliberately goes through the same call the assistant makes, rather
than pinging something cheaper: a key that can list models but not generate,
and a model that does not support structured output, would both pass a simpler
check and then fail in the till.

Switching the assistant off hides its button from Kassa entirely, rather than
leaving a button that explains it is unavailable. It takes effect the next time
Kassa is opened; no restart.

The environment variables still work, as the fallback for a machine that has
never been through Admin:

```powershell
$env:GEMINI_API_KEY = "..."
dotnet run
```

| Setting | Stored in | Default |
| ------- | --------- | ------- |
| On/off             | `Options.Ai.Enabled`                     | on |
| Model              | `Options.Ai.Model`                       | `gemini-3.5-flash-lite` |
| API key            | `%LOCALAPPDATA%\JussiMiniPos\gemini.key` | – |
| `GEMINI_API_KEY`   | environment                              | used when no key is stored |
| `GEMINI_MODEL`     | environment                              | used when no model is chosen |

**A stored setting wins over the matching environment variable.** Admin is
where a user expects to be in charge, so the variables are the fallback for an
unconfigured machine rather than an override of a configured one. Admin says so
on screen when `GEMINI_API_KEY` is set and no key is stored.

The key is the one setting that is *not* in the database. It is a secret, and
the database file is the thing that gets copied around as a backup, so it goes
to a file beside it instead. Saving an empty key field leaves the stored key
alone — removing it is its own button, behind a confirmation.

### How the key is stored

`gemini.key` holds the key **encrypted with Windows DPAPI under the current
user account**, written as `DPAPI:` followed by base64. So:

- Another Windows account on the same machine cannot read it.
- A copy of the file — in a backup, or moved to another machine — cannot be
  decrypted. It fails closed: the assistant falls back to plain search.
- It is not readable by opening the file in an editor.

What it does **not** defend against is code running as the same Windows user:
that code can ask DPAPI to decrypt the file exactly as this application does.
That is the honest limit of storing a credential a program must be able to read
unattended — the alternative is prompting for the key on every launch, which a
till cannot do. Treat it as protection against a copied file and a shared
machine, not against malware in the user's own session.

A file *without* the `DPAPI:` prefix is treated as a plain key, so a key set up
by hand keeps working; saving from Admin encrypts whatever is there. If DPAPI
itself is unavailable, saving falls back to a plain key rather than failing —
an unencrypted key is worse than an encrypted one and much better than an
assistant that cannot be configured.

Google retires models. When the configured one goes, the API answers `404`
naming its replacement, and that arrives as the dialog's notice band rather
than as a crash — the suggestions fall back to plain search in the meantime.
Picking another model in Admin is the fix; `AiSettings.DefaultModel` only
decides where a fresh install starts. The five models offered were each checked
against the API rather than taken from documentation: being listed by the
models endpoint is not the same as being callable, and a retired model is
still listed.

### Testing it

`--ask` runs one question through the whole path without starting the UI, and
prints what each step made of it:

```powershell
JussiMiniPos.exe --ask "Etsi alle 10 euron juomia" | Out-String
```

It reports the model, whether a key was found, the search stems and price
ceiling, how many candidate rows the model was given, and the products it
picked. That separates a bad search from a bad answer, and a rejected key shows
up as the same notice the dialog shows.

### When there is no key

The retrieval half needs no API at all, so the dialog degrades into a search
box rather than a dead end: it lists what the search found, and says in the
notice band that AI suggestions are off and to add a key in Admin. A failed or
rejected API call behaves the same way, with the API's own message.

## Admin and signing in

Under the tiles on the start screen are **Kirjaudu**, **Admin** and, once
signed in, who that is and **Kirjaudu ulos**. Admin holds the application's own
settings, as opposed to its data — today the AI assistant: on/off, the API key
and the model, all described above.

**Only the `admin` role can open Admin.** Opening it while signed out shows the
login prompt with a line saying why, rather than simply refusing; signed in as
another role, it says so and stays put. The session lasts as long as the
process — restarting signs everybody out, which for a till on a shared counter
is the safer default.

Settings live in the `Options` table, one row per name, so they survive a
restart. `--dump` prints them. A name with no row means "use the default", so a
fresh database has no rows at all and nothing has to be seeded.

### Users

```
Users
├── Id
├── Username      (UNIQUE, COLLATE NOCASE)
├── Email         (UNIQUE, COLLATE NOCASE)
├── FirstName     (empty when not given)
├── LastName      (empty when not given)
├── PasswordHash
└── Role          (CHECK: 'admin' | 'manager' | 'seller')
```

`FirstName` and `LastName` were added after the table existed, so they arrive
through `Database.ApplyMigrations` rather than the `CREATE TABLE`. `ALTER TABLE
ADD COLUMN` appends them physically after `Role`, which is why every query
names its columns explicitly instead of relying on their order.

Either the username or the email works at the prompt, matched
case-insensitively — `COLLATE NOCASE` on the column, so the match is
case-insensitive without `lower()` defeating the unique index.

`manager` can open Raportit alongside `admin`; `seller` cannot. Admin settings
remain restricted to administrators. `User.CanOpenAdmin`
is the single place that decides.

### First run: signing in for the first time

An empty `Users` table gets one administrator seeded into it, so a new install
has somebody who can open Admin:

| Username | Email | Password | Role |
| -------- | ----- | -------- | ---- |
| `admin`  | `admin@example.com` | `admin` | admin |

The application says so in a dialog before its window appears, and the command
line prints it the first time any command runs. So:

1. Start the app. Dismiss the dialog that gives you the credentials above.
2. Click **Kirjaudu** under the tiles and sign in as `admin` / `admin`.
3. Click your name, now shown at the bottom of the start screen, to open
   **Oma profiili**, and change the password under *Vaihda salasana*. It wants
   the current password (`admin`), the new one, and the new one again.

**Change it before doing anything else.** `admin` / `admin` is the weakest
credential there is and it is written in this file, so anyone who has seen the
repository knows it. Nothing enforces the change — the app only warns.

From a terminal instead, without the UI:

```powershell
JussiMiniPos.exe --user-update --user admin --password
```

The seeding is keyed off the table being empty rather than the database being
new, unlike the catalogue: `Users` is new to databases that already exist, and
an empty one would otherwise mean nobody can ever open Admin again.

**If the administrator password is lost, reset it — do not try to re-seed.**
Only the hash is stored, so there is nothing to recover, but the command line
sets a new password without asking for the old one:

```powershell
JussiMiniPos.exe --user-update --user admin --password
```

Emptying the table to make it re-seed is not a route back in: the last
administrator cannot be deleted (see below), so the count can never reach zero
through `--user-delete`. Re-seeding only happens for a database that has never
had a user — a new one, or one whose `Users` rows were removed with something
other than this application.

**Only this seeded administrator gets a fixed password.** Any other user
created without one gets a generated password instead — see below.

### Oma profiili

Signed in, the name on the start screen is a button that opens the profile
view: first name, last name, email, and a password change.

The two halves save independently. Renaming yourself should not require your
password, and changing your password should not be bundled with an edit you
might not want to keep.

- **Details.** Save is dead until a field actually differs from what is stored,
  compared against the row rather than tracked with a flag, so typing a change
  and undoing it leaves nothing to save. The email is checked for a plausible
  shape and for being free before anything is written, so a clash is a sentence
  rather than a SQLite error about an index. Saving re-reads the row and tells
  the shell, so the start screen follows a renamed user without signing out.
- **Password.** Needs the current password, the new one, and the new one again.
  The confirmation is checked as it is typed rather than on save, the new
  password must differ from the old, and the minimum length comes from
  `PasswordHasher.MinimumLength` so this and the command line cannot disagree.

**A user cannot change their own username or role here.** Those belong to an
administrator, and `UserRepository.UpdateProfile` names neither column, so no
amount of rewriting the view can reach them.

### Managing users from the command line

There is no user-management screen; the same exe does it instead. `--user`
takes a username or an email, and roles are `admin`, `manager` or `seller`:

```powershell
JussiMiniPos.exe --users | Out-String        # list them

JussiMiniPos.exe --user-add --username matti --email matti@example.com `
                 --role seller --password --firstname Matti --lastname Meikäläinen
JussiMiniPos.exe --user-update --user matti --role manager
JussiMiniPos.exe --user-update --user matti --password
JussiMiniPos.exe --user-delete --user matti
```

Passwords have three ways in, so a person, a script and an unattended run each
have one that suits:

| How | What happens |
| --- | ------------ |
| `--password "..."`     | Uses exactly that. What a script wants. |
| `--password` (no value) | Prompts, without echoing — stays out of shell history. |
| omitted                 | **Generates one and prints it once.** |

A generated password looks like `Kfx7-Rm9t-Qbv4-Xhn6`: sixteen characters from
an alphabet with no lookalikes (no `O`/`0`, no `I`/`l`/`1`), grouped so it can
be read aloud and typed back. Only its hash is stored, so the line the command
prints is the one and only time it can be seen.

`--user-update --generate-password` replaces someone's password with a fresh
generated one — the "they forgot theirs" case, where nobody has to invent a
password. Otherwise `--user-update` only touches the password when
`--password` is given, so changing a role cannot reset one by accident.

Passwords given by hand must be at least `PasswordHasher.MinimumLength`
characters — 8. The seeded `admin` / `admin` is the one exception, since its
whole point is being easy to type once, which also means `admin` cannot be set
back through these commands: only the first-run seeder writes it.

Note that **`--user-update` does not ask for the old password.** That is what
makes it the recovery path for a forgotten one, and it also means anyone who
can run the executable on that machine can take over the administrator
account. *Oma profiili* does require the old password; the command line is
trusted because reaching it already means having the machine.

**The last administrator cannot be deleted or demoted.** Either would leave
Admin unreachable with no way back short of editing the database by hand, so
both are refused until another administrator exists. If the table does end up
empty, the next start seeds `admin` again.

Every command reports what it did and exits `0`, or explains what stopped it
and exits `1`.

### How passwords are stored

`PasswordHash` is named for what it holds: a PBKDF2-SHA256 digest at 600,000
iterations, with a random 16-byte salt per user, stored as
`pbkdf2-sha256$iterations$salt$hash`.

Passwords are **hashed, not encrypted**. Encryption implies a key that turns
the stored value back into the password, and nothing — not this application,
not an administrator, not somebody who takes a copy of the database — should be
able to do that. The format is self-describing so the iteration count can be
raised later without invalidating hashes written before the change, comparison
is fixed-time so timing says nothing about how much of a hash was guessed, and
a wrong username costs the same as a wrong password so the prompt cannot be
used to find out which usernames exist.

`--dump` prints users without the hash column.

## License

Not yet specified.
