# Changelog

All notable changes to JussiMiniPos are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project aims to follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

Nothing yet.

## [1.0.0-beta.3] - 2026-09-11

### Changed

- The main application window title is now **Jussi mini-POS**.

### Added

- **Raportit** for administrators and managers: inclusive date filters,
  today/week/month presets, sales totals, transaction and unit counts, average
  transaction value, and sortable daily, product and category breakdowns.
  Daily rows include proportional sales bars and access to transactions and
  receipt details. Dates use Finnish time with daylight saving support.
  Reports use historical sale snapshots and load without blocking the UI.
  Date inputs use matching rounded fields, vertically centered text and clear
  calendar buttons aligned with the report action.
- GitHub Actions checks for Release builds, formatting, NuGet vulnerabilities,
  dependency review and CodeQL, plus Dependabot update configuration.

## [1.0.0-beta.2] - 2026-09-08

Still a beta of 1.0.0 rather than a release: the gaps under *Known limitations*
have not moved, and VAT in particular is still a schema change that is far
cheaper to make before there is real sales data than after. What this adds is
the AI assistant, the settings and users behind it, and a profile view.

### Added

- **The version in the corner of the start screen**, read from the assembly's
  informational version so it can only ever be the version that was built.
  `--version` prints the same string without opening the database.
- **The logo on the start screen**, in place of the "JussiMiniPos" heading —
  the logo carries the name, so a heading under it only said it twice.
  `Assets/Icons/jussi-mini-pos-logo.svg` is kept as the design source and
  redrawn in XAML by the `Logo` and `Logo.Mark` styles, because WPF cannot
  render SVG and that file asks for a font nobody has installed. Its colours
  are `Brush.Brand` and `Brush.BrandText`, deliberately separate from
  `Brush.Accent`.

- **Oma profiili**, reached by clicking the signed-in name on the start screen.
  A user can change their own first name, last name and email, and their
  password. The two halves save independently: renaming yourself should not
  require your password, and changing your password should not be bundled with
  an edit you might not want to keep.
  - Save stays dead until a field differs from the stored row, compared against
    the row rather than tracked with a flag, so typing a change and undoing it
    leaves nothing to save. The email is checked for shape and for being free
    before anything is written. A save re-reads the row and hands it to the
    shell, so the start screen follows a renamed user without signing out.
  - Changing the password needs the current one, the new one and the new one
    again. The confirmation is checked while it is typed rather than on save,
    the new password must differ from the old, and both PBKDF2 derivations run
    off the UI thread.
  - **A user cannot change their own username or role here.** Those belong to
    an administrator, and `UserRepository.UpdateProfile` names neither column,
    so the view cannot reach them however it is rewritten.
- **`FirstName` and `LastName` on users**, added through
  `Database.ApplyMigrations` because the table already existed. Where a name is
  set it replaces the username in the signed-in line and anywhere else a user
  is shown; the seeded administrator starts without one, so the username
  stands in. `--user-add` and `--user-update` take `--firstname` and
  `--lastname`, and `--users` has a Name column.
- **User management on the command line**: `--users` lists them, and
  `--user-add`, `--user-update` and `--user-delete` do what they say.
  `--user` takes a username or an email. Leaving `--password` with no value
  prompts for it without echoing, so it stays out of shell history and off the
  screen; passing a value works for scripts, and a piped line works too.
  `--user-update` only touches the password when `--password` is given, so
  changing a role cannot reset one by accident, and it reads the new password
  before writing anything, so cancelling the prompt leaves the user untouched.
  **The last administrator cannot be deleted or demoted** — either would leave
  Admin unreachable with no way back short of editing the database by hand.
  Taken names, unknown roles, unknown users, passwords under 8 characters and a
  no-op update are each reported as a sentence and exit `1`.
- **Users and signing in.** A `Users` table (`Id`, `Username`, `Email`,
  `PasswordHash`, `Role`) with both name columns `UNIQUE COLLATE NOCASE`, so
  either one works at the prompt and the match is case-insensitive without
  `lower()` defeating the unique index. **Kirjaudu** under the start screen's
  tiles opens the login dialog; once in, the start screen shows who is signed
  in and offers **Kirjaudu ulos**. The session lasts as long as the process, so
  restarting signs everybody out — the safer default for a till on a shared
  counter.
- **Admin is restricted to the `admin` role.** Opening it while signed out
  shows the login prompt with a line saying why, rather than refusing; signed
  in as another role, it says so and stays put. Roles are `admin`, `manager`
  and `seller`, held to those three by a `CHECK`; the latter two carry no extra
  rights yet, and `UserRole.CanOpenAdmin` is the single place that decides.
  Admin's header shows which account is making the change.
- **A seeded default administrator** — `admin` / `admin@example.com` /
  `admin` — written into an empty `Users` table, with the application and the
  command line both saying so and telling the user to change it. Keyed off the
  table being empty rather than the database being new, unlike the catalogue,
  because `Users` is new to databases that already exist and an empty one would
  mean nobody can ever open Admin again — which also makes deleting every user
  the way back in when the password is lost, since only its hash is stored.
  This is the one fixed password in the application, and it is trivial on
  purpose: it exists to be typed once and replaced.
- **Generated passwords** (`PasswordGenerator`) for every other user created
  without one. `--user-add` with no `--password` makes one up and prints it
  once, and `--user-update --generate-password` resets somebody's forgotten
  password the same way. Sixteen characters from an alphabet with no lookalikes
  (no `O`/`0`, no `I`/`l`/`1`), grouped as `Kfx7-Rm9t-Qbv4-Xhn6` so it can be
  read aloud and typed back. Nothing is written into the source, so no two
  installs share a credential.
- **PBKDF2 password hashing** (`PasswordHasher`): SHA-256 at 600,000
  iterations, a random 16-byte salt per user, stored self-describing as
  `pbkdf2-sha256$iterations$salt$hash` so the iteration count can be raised
  later without invalidating existing hashes. Passwords are hashed, not
  encrypted: nothing — including an administrator, and including whoever takes
  a copy of the database — can turn a stored value back into a password.
  Comparison is fixed-time, a malformed hash refuses the login instead of
  throwing, and a wrong username costs the same as a wrong password so the
  prompt cannot be used to enumerate usernames. `--dump` prints users without
  the hash column.
- **The stored Gemini API key is encrypted at rest** with Windows DPAPI under
  the current user account, written as `DPAPI:` plus base64. Another Windows
  account cannot read it, and a copy of the file taken to another machine fails
  closed — the assistant falls back to plain search. It does not defend against
  code running as the same user, which is the honest limit of storing a
  credential a program must read unattended. A file without the prefix is
  treated as a plain key, so one set up by hand keeps working, and saving from
  Admin encrypts whatever is there.
- **Adding several products from one answer.** Each suggestion's button stays
  live after a click, so clicking it again adds another one, and a badge on the
  row counts how many have gone in. **Lisää kaikki ostoskoriin** takes one of
  everything suggested in a single click. The count badge sits in the row's
  existing badge line rather than under the button: that line's height is
  already fixed, so a click cannot grow the card and shift the ones below out
  from under the pointer.
- **`Testaa yhteys` in Admin**, which calls Gemini with what is on screen
  rather than what is saved — so a pasted key can be checked before it replaces
  a working one. It goes through the same structured-output call the assistant
  makes, because a key that can list models but not generate, and a model that
  does not support structured output, would both pass a simpler check and then
  fail in the till. Verified against a bad key, an unknown model and a retired
  one; each reports the API's own message.
- **A link to Google AI Studio** in Admin's key section, so getting a key is
  not a matter of knowing the URL. It opens in the default browser, and the
  scheme is checked first so nothing but http(s) can ever be launched.
- **Admin view**, reached from a link under the tiles on the start screen. It
  holds the application's own settings, as opposed to its data: whether the AI
  assistant runs, the Gemini API key, and which model to call. Switching the
  assistant off hides its button from Kassa entirely rather than leaving one
  that explains itself, and takes effect the next time Kassa is opened. Saving
  an empty key field leaves the stored key alone — removing it is its own
  button, behind a confirmation — and leaving with unsaved changes asks first.
- **`Options` table** for application settings: `Id`, `OptionName` (unique) and
  `OptionValue`, written through `OptionsRepository` as an upsert so a name
  stays one row. A name with no row means "use the default", so a fresh
  database needs nothing seeded. `--dump` prints the table.
- **A model picker** offering five Gemini models, cheapest first, each with
  what choosing it costs. Every one was checked against the API rather than
  taken from documentation: being listed by the models endpoint is not the same
  as being callable, and a retired model is still listed.
- **AI-avustaja (AI assistant) in Kassa.** A button above the product list
  opens a dialog where the cashier asks in Finnish what a customer is after —
  *"Mitä sopii kahvin kanssa?"*, *"Etsi halpa välipala"*, *"Etsi alle 10 euron
  juomia"* — and gets back catalogue products, each with a one-line reason and
  a button that puts it in the cart. The dialog stays open while products are
  added, so a follow-up question does not mean starting over. Three example
  questions are one click away, and Enter sends.
- **Product search used as retrieval (`ProductSearch`).** The question is
  reduced to search stems and to the constraints in it, and scored against
  product titles, categories and descriptions in SQLite — title hits first.
  Matching is on stems rather than whole words, because Finnish inflects what a
  cashier types: *"kahvin"* finds *Kahvi* and *"juomia"* finds *Juomat*. A
  price ceiling like *"alle 10 euron"* is applied as a `WHERE` on the price the
  till would actually charge, so a product over the limit never reaches the
  model. Case folding is done in .NET through a SQLite function, since SQLite's
  own `LIKE` and `lower()` only fold ASCII and would miss `ä` and `ö`.
- **Google Gemini for the generation half (`GeminiClient`).** The retrieved
  rows are the only products the model is shown, and the reply comes back
  through Gemini's structured output as ids and reasons. Ids are resolved
  against the candidate list rather than trusted, so a product, price or offer
  that is not in the catalogue cannot be suggested. The default model is
  `gemini-3.5-flash-lite`, the cheapest one with a free tier. A retired model
  answers `404` naming its replacement, which surfaces as the dialog's notice
  band rather than as a failure.
- **`--ask "…"` command line option** that runs one question through the whole
  path without starting the UI, reporting the model, whether a key was found,
  the search stems and price ceiling, how many rows the model was given and the
  products it picked — which is what separates a bad search from a bad answer.
- **Key configuration** through Admin, stored in a `gemini.key` file next to
  the database. The key is the one setting deliberately kept out of the
  database: it is a secret, and the database file is the thing that gets copied
  around as a backup. `GEMINI_API_KEY` and `GEMINI_MODEL` still work as the
  fallback for a machine that has never been through Admin — a stored setting
  wins over the matching variable, since Admin is where a user expects to be in
  charge. Admin says so on screen when the variable is set and no key is
  stored.

### Changed

- `CatalogRepository.GetProducts` takes an optional set of ids, so the
  assistant can search in SQL and read just the rows it found back through the
  repository, rather than a second mapping from row to `Product`.
- The start screen is a two-row grid rather than one centred stack, so the
  Admin link can sit at the bottom without becoming a fifth tile.
- The assistant's add button reads *Lisää ostoskoriin* rather than *Koriin*.
  Its action column is 168px, which the measured 121px label fits with room
  spare, and the reply text and *Lisää kaikki ostoskoriin* share a line, so the
  longer label costs no visible results.

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

- **Adding and removing users is command line only.** A signed-in user can edit
  their own details and password in *Oma profiili*, but adding a user, changing
  someone else's role or deleting an account needs `--user-add`,
  `--user-update` or `--user-delete`. There is also no account lockout after
  repeated failures beyond a fixed delay, and no session timeout.
- **Signing in gates Admin and Raportit.** Kassa, Tuotteet and Myynti are open
  to anyone at the machine, as before; `seller` exists so the role is in place
  rather than because it does anything yet.
- **No VAT (ALV)** is recorded or shown anywhere.
- Payment is simulated; there is no card terminal integration, no cash
  handling, and no receipt printing.
- Deleting a sale removes it permanently rather than voiding it.
- There is no automated test suite.
