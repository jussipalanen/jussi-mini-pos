# AGENTS.md

This file applies to the entire repository. It describes the current project
baseline and the expectations for automated coding agents working here.

## Project snapshot

JussiMiniPos is a Windows-only point-of-sale desktop application built with WPF
on .NET 10. The current project version is `1.0.0-beta.3`. It uses
`Microsoft.Data.Sqlite` for local persistence and has no automated test project
yet.

The application is intentionally small and direct:

- `App.xaml(.cs)` configures global resources, Finnish culture, and headless
  command-line dispatch.
- `MainWindow.xaml(.cs)` is the composition root and navigation shell. It owns
  the repositories, the process-lifetime signed-in user, and cached checkout
  state.
- `Views/` contains paired WPF XAML/code-behind screens and dialogs. Views use
  events for navigation and implement `INotifyPropertyChanged` where binding
  state requires it.
- `Models/` contains domain records/classes such as products, sales, users, and
  cart lines.
- `Services/` contains SQLite schema/repositories, image storage, authentication,
  application settings, catalogue search, and Gemini integration.
- `CommandLine.cs` provides non-UI catalogue, user, assistant, and version
  commands through the same executable.
- `.github/workflows/` enforces a strict Release build, formatting, dependency
  review, NuGet auditing, and CodeQL analysis. Keep these checks green and keep
  `.github/dependabot.yml` aligned when package ecosystems change.
- `Assets/Styles.xaml` and `Assets/Icons.xaml` are the shared WPF design system.
  SVG files under `Assets/Icons/` are design sources; WPF uses the corresponding
  `PathGeometry` resources rather than loading SVG files at runtime.
- `README.md` documents behavior and operator workflows. `CHANGELOG.md` follows
  Keep a Changelog and records release-facing changes.

`Raportit` provides read-only sales summaries and daily/product/category
breakdowns for administrators and managers. Preserve Finnish date boundaries,
sale snapshots, and the `CanOpenReports` domain rule when changing reporting.

## Build and run

Use PowerShell on Windows with the .NET 10 SDK:

```powershell
dotnet restore
dotnet build --no-restore
dotnet run
```

Useful additional commands:

```powershell
dotnet run -c Release
dotnet publish -c Release -r win-x64 --self-contained false
dotnet run -- --version
```

The project currently has no automated test suite. For every code or XAML
change, at minimum run `dotnet build --no-restore` after dependencies have been
restored. For behavior changes, manually exercise the affected UI or CLI flow
and report what was and was not verified. Do not claim that tests passed when
only a build was run.

Running the application or most CLI commands can create or modify real data in
`%LOCALAPPDATA%\JussiMiniPos`. In particular, `--seed`, `--seed --reset`,
`--clear`, and `--user-*` are mutating commands. Never run them against the
user's normal profile as an incidental smoke test. Prefer tests that construct
`Database` with a unique temporary database path and clean up that isolated
directory afterward. `--ask` may make a billable network request when a Gemini
key is configured.

Do not edit or commit generated output from `bin/`, `obj/`, publish folders, IDE
metadata, local databases, image stores, key files, or `.env` files.

## Coding conventions

- Keep identifiers, comments, XML documentation, database names, and commit
  content in English. Keep all user-visible UI and error text in Finnish.
  `AppViewNames` and `PaymentMethodNames` are the existing English/Finnish
  mapping seams.
- Preserve nullable-reference-type correctness. Do not silence warnings with
  `!` or broad suppressions unless the invariant is explicit and unavoidable.
- Match the existing modern C# style: file-scoped namespaces, primary
  constructors where clear, records for value-shaped data, collection
  expressions, raw string literals for SQL, `var` where the type is apparent,
  and early returns.
- Keep changes focused. Reuse existing models, repositories, helpers, XAML
  resources, and view events before introducing a framework or dependency.
  This codebase does not currently use a DI container or a full MVVM framework.
- Add concise comments or XML documentation for non-obvious invariants and
  security/data-integrity decisions; avoid comments that merely restate code.
- Parameterize all SQLite values. Interpolated SQL is acceptable only for
  compile-time-controlled identifiers or query fragments that cannot be
  parameterized, following the existing repository patterns.
- Dispose connections, commands, readers, and transactions with `using`.
  Multi-row or multi-table writes that represent one operation must be atomic.
- Keep expensive cryptography and remote calls from blocking the WPF UI thread.
  Preserve the existing async behavior in authentication and Gemini flows, and
  avoid introducing long-running database or file work in event handlers.
- Handle expected write failures at the view boundary with `ViewErrors.Try` or
  an equivalently user-friendly Finnish error path. Do not let routine SQLite
  or file failures escape WPF event handlers and terminate the active sale.

## UI and WPF rules

- Put reusable colors, brushes, typography, buttons, form controls, cards, and
  icon geometry in the shared resource dictionaries instead of duplicating
  local values.
- Refer to resources using the established names such as `Brush.*`, `Text.*`,
  `Button.*`, `Icon.*`, `Logo`, and `Logo.Mark`.
- When adding an icon, keep the SVG as a design reference and add a WPF
  `PathGeometry` to `Assets/Icons.xaml`; do not add an SVG runtime dependency.
- Preserve `fi-FI` display behavior. Prices are euros and should be bound or
  formatted on `FrameworkElement` controls so the culture metadata configured
  in `App.xaml.cs` applies.
- Keep XAML and its code-behind consistent: names, handlers, bindings, property
  notifications, enabled/visibility states, and dialog ownership must all be
  updated together.
- Modal windows should set their owner. Navigation remains coordinated through
  `MainWindow` and view events unless a deliberate architecture change is in
  scope.
- Preserve minimum-window usability (`1000x640`) and check layouts with Finnish
  strings, empty states, validation messages, and long content.

## Data and domain invariants

- Store money in SQLite as integer cents, never `REAL`. Use
  `SalesRepository.ToCents` and `SalesRepository.FromCents`; use `decimal` in
  the domain and UI.
- Store timestamps in invariant ISO 8601 round-trip form. Keep database column
  names PascalCase, booleans as checked `INTEGER` values `0`/`1`, and foreign
  key enforcement enabled on every connection.
- Completed sales and their line snapshots are written in one transaction only
  after successful payment. Catalogue edits must never rewrite sale history.
- A missing `SalePriceCents` means no discount. The till charges
  `Product.EffectivePrice`.
- Products may belong to several categories. Category hierarchy uses
  `ParentId`; deleting a parent with children remains restricted.
- Image paths stored in SQLite are relative to the application data directory.
  Missing files must degrade to placeholders. Coordinate database changes and
  physical image cleanup without deleting files that may still be referenced.
- New tables belong in the idempotent schema in `Database`. Changes to tables
  that may already exist also require an idempotent migration in
  `ApplyMigrations`. Never depend on column order or use `SELECT *`.
- New installations seed the demo catalogue based on whether the database file
  is new. Do not change this to "tables are empty" behavior. Default-user
  seeding intentionally follows different rules; read `UserRepository` and the
  README before changing it.

## Authentication, secrets, and AI safety

- Never commit, print, log, or expose API keys, plaintext passwords, password
  hashes, salts, or live database contents. Avoid passing secrets on command
  lines because shell history and process inspection can reveal them.
- Passwords use the self-describing PBKDF2-SHA256 format in `PasswordHasher`.
  Preserve random salts, fixed-time verification, malformed-hash failure, and
  equivalent work for unknown usernames. Do not weaken iteration counts or the
  minimum-password rules.
- Preserve the rule that the last administrator cannot be deleted or demoted.
  Authorization for Admin belongs in the domain rule (`CanOpenAdmin`), not only
  in button visibility.
- The Gemini key is stored separately from SQLite and protected with Windows
  DPAPI when available. A stored key takes precedence over `GEMINI_API_KEY`;
  stored model settings take precedence over `GEMINI_MODEL`.
- Keep AI answers grounded in catalogue retrieval: only public candidate rows
  may be sent, returned IDs must resolve against that candidate set, and sales,
  users, images, secrets, and unrelated database content must never be sent.
- Preserve the no-key/API-failure fallback to local product search and surface
  actionable Finnish notices instead of crashing or inventing products.
- Model availability changes over time. Verify model-list changes against the
  live API and update `AiSettings`, UI descriptions, README, and changelog
  together; do not assume a listed model supports structured output.

## Change checklist

Before handing off a change:

1. Inspect nearby code and preserve its behavior unless the task explicitly
   changes it.
2. Update all affected layers (model, schema/migration, repository, service,
   XAML, code-behind, CLI, and documentation) rather than patching only the
   visible symptom.
3. Build with zero warnings and errors.
4. Verify the affected behavior with isolated data. For UI work, check success,
   cancellation, validation, empty, and failure paths as relevant.
5. Update `README.md` when setup, behavior, data layout, security, or operator
   commands change. Update `CHANGELOG.md` under `Unreleased` for user-visible
   changes. Update the version only when the task explicitly includes a release.
6. Review the diff for accidental generated files, secrets, English UI text,
   destructive database behavior, and unrelated edits.
