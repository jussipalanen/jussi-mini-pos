# Changelog

All notable changes to JussiMiniPos are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project aims to follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

Nothing yet.

## [1.0.0-beta] - 2026-09-08

First release. The checkout, catalogue and sales history all work against a
real database. It is marked beta rather than 1.0.0 because of the gaps listed
under *Known limitations* — VAT in particular is a schema change, and far
cheaper to make before there is real sales data than after.

### Added

- **Kassa (checkout).** Product list with live search by product number or
  name, category filter chips, and a shopping cart. Adding a product already in
  the cart merges it into one line rather than repeating it. Per-line quantity
  steppers, line removal, a running total and an item count.
- **Payment flow.** Choosing *Maksukortti* shows a terminal wait, then
  *Maksu onnistui* with the total, sale number and timestamp. Backing out
  returns to the same cart; a completed sale starts an empty one. A failure
  state covers the sale not being stored.
- **SQLite storage** under `%LOCALAPPDATA%\JussiMiniPos`. A sale and its lines
  are written in one transaction, and only after payment succeeds.
- **Product catalogue in the database.** `Categories` nest through `ParentId`;
  `Products` link to many categories through `ProductCategories`; extra
  pictures live in `ProductImages`. Products carry a normal price and an
  optional offer price, and an `IsPublic` flag that keeps a row out of the till
  without deleting it.
- **Tuotteet (product management).** Paged table with search, category filter
  and a 10/25/50/100 rows-per-page selector. Add, edit, view and delete.
  Hidden products are listed here with a *Piilotettu* badge, since this is
  where they get hidden.
- **Product images.** A feature image and a gallery per product, uploaded
  through the editor and stored beside the database under `images/`. A gallery
  picture can be promoted to feature image. Missing files show a placeholder
  rather than failing, and unreferenced files are cleaned up when a product is
  deleted or the catalogue is reset.
- **Kategoriat (category management).** Add, edit and delete, with the tree
  shown by indentation. The parent picker excludes the category and its own
  descendants, so a cycle cannot be created. Deleting refuses outright when
  there are child categories, and otherwise reports how many products lose the
  category first.
- **Myynti (sales).** Past sales newest first with number, date, payment
  method, item count and total, plus totals across the whole history. A receipt
  view lists every line with its product number, category, quantity and unit
  price. Sales can be deleted after a confirmation that says what is lost.
- **Product thumbnails** in both the till and the management list, with a
  placeholder icon where there is no picture.
- **Product details window** showing the pictures, prices, categories and how
  the product has sold so far.
- **Command line tools** on the same executable: `--seed`, `--seed --reset`,
  `--dump`, `--clear` and `--help`. It attaches to the calling terminal, and
  falls back to a message box when there is none.
- **Demo catalogue** seeded into a brand new database: 7 categories, 20
  products priced in euros with four on offer, and 27 product/category links.
- **Application icon** embedded in the executable, so Explorer, the taskbar and
  the title bar all show it.
- Accessible names throughout, so the lists and icon-only buttons announce
  themselves properly rather than reading out a record's `ToString()`.

### Changed

- Prices are euros throughout, held as `decimal` in memory, stored as integer
  cents, and formatted through `fi-FI` so they render as `2,50 €`.
- Code identifiers are English and only displayed text is Finnish.
  `AppViewNames` and `PaymentMethodNames` map between the two.
- The till reads its catalogue from the database rather than a hard-coded list.
  `DemoCatalog` is seed data only and is never read at runtime.
- Paging moved into a shared `Pager`, used by both the products and sales
  views, so the page arithmetic exists in one place.
- Seeding keys off the database file being new rather than the tables being
  empty, so `--clear` and a hand-emptied catalogue stay empty across restarts.
- The catalogue seeder no longer writes placeholder image paths for files that
  do not exist, now that real pictures can be uploaded.

### Fixed

- Changing rows per page kept the page number instead of the row you were
  looking at, because the top row was worked out from the page size that had
  already been replaced.
- The current page was never highlighted in either pagination bar: `Tag` is
  typed `object`, so a `Trigger`'s value stayed the string `"True"` and never
  matched the bound `bool`.
- Database writes ran unguarded inside click handlers, so a failure escaped and
  took the application down along with the current cart.
- The product editor deleted dropped image files before the caller wrote the
  draft, so a failed write left rows pointing at files that were already gone.
- A `ParentId` cycle could recurse until the stack gave out when opening the
  category editor, and left the affected categories off the list entirely, so
  nobody could fix them.
- Clearing the catalogue reported success even when a category cycle stopped it
  finishing, which left a following `--seed --reset` duplicating the roots.
- Prices in the cart rendered with a full stop instead of a comma, because
  `Run` is a `FrameworkContentElement` and does not take the application's
  language override.
- The initial commit tracked `bin/` and `obj/`; they were removed and a
  `.gitignore` added.

### Known limitations

- **Raportit** is a placeholder and opens nothing.
- **No VAT (ALV)** is recorded or shown anywhere.
- Payment is simulated; there is no card terminal integration, no cash
  handling, and no receipt printing.
- Deleting a sale removes it permanently rather than voiding it.
- There is no automated test suite.
