# Roadmap

What comes next for JussiMiniPos, in the order it should be built. Each phase
lists what is in scope and what "done" means. When an item ships, tick it here
and record it in [CHANGELOG.md](CHANGELOG.md); the changelog says what shipped,
this file says what is still to come.

The order matters more than the dates. Later phases build on earlier ones:
tests make the schema changes safe, the schema changes settle the shapes the
REST API exposes, and the API is designed once rather than twice.

| Phase | Theme | Target |
| ----- | ----- | ------ |
| 1 | Foundations: repository layout and tests | 1.0.0 |
| 2 | Domain gaps: VAT, voiding sales, users and roles | 1.0.0 |
| 3 | Shared core library | 1.1.0 |
| 4 | C#/.NET REST API | 1.1.0 |
| 5 | Tills on the API, with offline sales | 1.2.0 |
| — | [Missing features](#missing-features): barcodes, cash, refunds, receipts, closing the day, stock and more | By priority |

**Current release:** 1.0.0-beta.5 — VAT on products and receipts, voided sales,
an audit log, user management and role enforcement, with tests on both SQLite
and PostgreSQL and a Windows installer published with every GitHub release.

---

## Phase 1 — Foundations

Nothing here changes behaviour. It makes every later phase safer and cheaper.

### 1.1 Repository layout

The WPF project file sits at the repository root, and an SDK-style project
compiles every `.cs` file beneath it, so a second project cannot be added next
to it. Move it into its own folder first, as a mechanical change on its own.

```
JussiMiniPos.slnx
src/
  JussiMiniPos/            WPF application (moved, unchanged)
tests/
installer/
docker-compose.yml
```

- [x] Move the WPF project to `src/JussiMiniPos/` and add `JussiMiniPos.slnx`.
- [x] Update paths in `ci.yml`, `codeql.yml`, `release.yml`, `installer/JussiMiniPos.iss`,
      `installer/build.ps1`, `README.md` and `AGENTS.md`.
- [ ] Done when CI is green and the release workflow still produces a working
      `JussiMiniPos-setup.exe`.

### 1.2 Automated tests

The data layer was ported to PostgreSQL without tests. Pin its behaviour before
the schema changes in Phase 2.

- [x] Add `tests/JussiMiniPos.Tests` (xUnit) to the solution and to CI.
- [x] Run every data test against **both** engines: SQLite in a unique temporary
      directory, PostgreSQL in a throwaway database. Never the user's profile.
- [x] Cover first: report totals and Finnish date boundaries (including the
      daylight-saving change), product search ranking and Unicode folding,
      schema creation and every `ApplyMigrations` step, sale write atomicity,
      and password hashing. The last-administrator rule moved to 2.3: it lives
      only in the command line today, where no test can reach it.
- [x] Done when the suite runs in CI on every pull request.

---

## Phase 2 — Domain gaps before 1.0.0

These are the *Known limitations* that keep the project in beta. VAT goes first:
it changes the schema of products and sales, which is far cheaper before there
is real sales data than after.

### 2.1 VAT (ALV)

- [x] A VAT rate per product, chosen from a managed list of rates rather than
      typed in, because Finnish rates change over time.
- [x] Each sale line snapshots its rate and VAT amount in integer cents, so a
      later rate change never rewrites history.
- [x] Prices stay VAT-inclusive at the till; receipts and Raportit show the VAT
      breakdown per rate.
- [x] Idempotent migration for existing databases, on both engines.

### 2.2 Void sales instead of deleting them

- [x] Replace deleting a sale with voiding it: who, when and why are recorded,
      and the sale and its lines stay in the database.
- [x] Voided sales are excluded from report totals and shown as voided in Myynti.
- [x] The audit log from *Missing features*, built here because voiding is the
      first thing that must record who did it: sign-ins, voids, price and VAT
      changes, and user changes, each written in the same transaction.

### 2.3 Users and roles

- [x] Move the last-administrator rule out of `CommandLine` into the domain, so
      the command line and the Admin view share it, and test it.
- [x] Add, edit, change the role of and remove users in the Admin view, not only
      from the command line. The last-administrator rule still applies.
- [x] Account lockout after repeated failed sign-ins, and an inactivity timeout.
- [x] Give the `seller` role real meaning: decide which of Kassa, Tuotteet and
      Myynti need a signed-in user, and enforce it in the domain rules.

### 2.4 Installer follow-ups

- [x] Word the uninstaller's data prompt for both providers: list local files
      found in the data folder and make clear that PostgreSQL server data is
      never removed. Keeping local data remains the default.
- [x] Document `%LOCALAPPDATA%\JussiMiniPos\database.env` as the place to
      configure an installed till, since the folder beside the executable is
      under Program Files and needs administrator rights to write.

### 2.5 Till essentials

The *High* items marked *1.0.0* under [Missing features](#missing-features):
barcodes, cash payment, returns and refunds, receipt printing with shop details,
and backup and restore. Opening and closing the till and the end-of-day report
follow cash payment. (The audit log shipped with 2.2.)

- [x] Barcodes: an EAN-13, EAN-8 or UPC-A code per product, checked and unique,
      and scanner input in Kassa that adds the product straight to the cart.
- [x] Cash payment: amount given, change due, and rounding to 5 cents as an
      Admin setting that is on by default. Card stays exact; the rounding is
      stored on the sale.

**1.0.0 ships when Phases 1 and 2 are done.**

---

## Phase 3 — Shared core library

- [x] Extract `src/JussiMiniPos.Core`: models, money in cents, VAT, and domain
      rules such as `CanOpenAdmin`, `CanOpenReports` and the public-product
      filter. No WPF and no database code in it.
- [x] The WPF application references Core; behaviour is unchanged and the tests
      from Phase 1 still pass.

This is what lets the REST API and the tills enforce the same rules from the
same code.

---

## Phase 4 — C#/.NET REST API

A backend REST API written in C# on .NET 10, in this repository as
`src/JussiMiniPos.Api`.

### Why

Under PostgreSQL every till connects to the database directly. That means:

- every till holds the database password with full read and write access, and
  the role rules live only in the desktop client;
- product images are files in each till's own folder, so a picture added at one
  till is missing at another;
- every till holds its own copy of the Gemini API key;
- schema migrations run from whichever till starts first.

An API owns the database, the images, the key and the migrations, and enforces
the rules in one place. It also opens the catalogue to a website, a web shop or
accounting software.

### Technology

| Concern | Choice | Why |
| ------- | ------ | --- |
| Framework | ASP.NET Core **minimal APIs** on .NET 10 | Smallest step from the current code; no MVC controllers needed |
| Data access | Existing repositories and `SqlDialect`, moved behind the API | Proven SQL on both engines; no EF Core rewrite |
| Shared rules | `JussiMiniPos.Core` from Phase 3 | One definition of every rule |
| API description | Built-in OpenAPI document (`Microsoft.AspNetCore.OpenApi`) | Generated from the endpoints; clients can be generated from it |
| Interactive docs | Scalar UI at `/docs` | Try every endpoint from the browser |
| Errors | `ProblemDetails` (RFC 9457) with Finnish messages | A standard shape the till can display as is |
| Rate limiting | Built-in `Microsoft.AspNetCore.RateLimiting` | No extra dependency |
| Authentication | Custom API-key handler; signed-in user tokens | See *Access levels* |
| Hosting | Container image, added to `docker-compose.yml` beside PostgreSQL | One command for a local or demo server |
| Live updates (optional) | SignalR | Push price and catalogue changes to every till |

**SQLite mode stays as it is.** A single till with a local database has no
reason to run a server; the API is for the shared PostgreSQL setup.

### Access levels

| Level | Who | Authenticates with | May |
| ----- | --- | ------------------ | --- |
| **Demo** | Anyone trying the API | Nothing — demo mode only | Read and record sales on demo data |
| **Integration** | A website, web shop or accounting system | API key | What the key's scopes allow, e.g. read the catalogue |
| **Staff** | People at the tills | Username and password → short-lived token | What their role allows |

API keys identify a *system*; staff tokens identify a *person*, so sale history
still records who made each sale.

### Endpoints (version 1)

All routes are under `/api/v1`. Money is always **integer cents** in JSON.

| Resource | Operations | Access |
| -------- | ---------- | ------ |
| `/products` | `GET` list (search, category, paging), `GET /{id}` | `catalog:read` |
| `/products` | `POST`, `PUT /{id}`, `DELETE /{id}` | Staff: manager or admin |
| `/products/{id}/images` | `GET`, `POST` (upload), `DELETE` | Read: `catalog:read`; write: manager or admin |
| `/categories` | `GET` (tree via `parentId`); `POST`, `PUT`, `DELETE` | Read: `catalog:read`; write: manager or admin |
| `/images/{file}` | `GET` | `catalog:read` |
| `/sales` | `GET` list and `GET /{id}`; `POST` | Read: `sales:read` or staff; create: staff |
| `/sales/{id}/void` | `POST` | Staff: manager or admin |
| `/reports/summary`, `/daily`, `/products`, `/categories` | `GET` with a date range | `reports:read` or manager and admin |
| `/assistant/ask` | `POST` | Staff only — every question is a billable Gemini call |
| `/auth/login`, `/me` | `POST`, `GET` | Anyone / signed in |
| `/users`, `/api-keys` | Full management | Admin only |

There is no `PUT` or `DELETE` on sales: history is never rewritten.

A public product as the API returns it:

```json
{
  "id": 42,
  "name": "Kahvi 500 g",
  "description": "…",
  "priceCents": 899,
  "salePriceCents": 749,
  "effectivePriceCents": 749,
  "onSale": true,
  "categories": [{ "id": 3, "title": "Kahvit" }],
  "featureImage": "/api/v1/images/abc.jpg",
  "images": ["/api/v1/images/def.jpg"]
}
```

`effectivePriceCents` is what the till charges, so no client has to work it out.
`isPublic` is never sent, because only public products are ever returned.

### Rules

- **A hidden product is `404 Not Found`**, never `403 Forbidden`, which would
  confirm that it exists. The filter is the same Core rule the AI assistant
  uses.
- **Creating a sale is idempotent.** The client sends a unique key with every
  sale and a repeat is ignored, so a retry after a dropped connection can never
  record a sale twice. Phase 5 depends on this.
- **Optimistic concurrency** on catalogue edits with ETags, so two tills editing
  the same product cannot silently overwrite each other.
- **Caching** with `ETag` and `Cache-Control` on catalogue reads.
- **Paging capped** at 100 rows per page.
- **CORS** allows only configured origins.
- **HTTPS only.** Rate limits per API key, and per IP address in demo mode.
- **Versioned from the start.** Adding to `v1` is free; changing or removing a
  field means `v2`.
- Sales, reports, users, settings and keys are never anonymous.

### API keys

- Format `jmp_` followed by 32 random bytes, so a leaked key is easy to find in
  logs and code and can be matched by secret scanners.
- Shown **once**, when created. The database stores only its SHA-256 hash; a
  random key of that length needs no slow password hash.
- Sent in the `X-Api-Key` header, never in the URL, which ends up in logs.
- Scopes per key: `catalog:read`, `sales:read`, `reports:read`.
- A name, an optional expiry, last-used time, and one-step revocation.
- A key used from a browser is visible in the page source, so give a website a
  `catalog:read` key limited to its own origin, and call anything sensitive
  from a server.
- Managed in the Admin view and with `--apikey-*` command-line commands. A key
  is never printed again after creation or written to a log.

```
ApiKeys: Id, Name, Prefix, KeyHash, Scopes, CreatedAt, ExpiresAt, LastUsedAt, RevokedAt
```

### Demo mode

For demonstrations and testing without a key or a password.

- Off by default; enabled only by `JUSSIMINIPOS_API_DEMO=true`.
- Every request is treated as a signed-in **demo user**. Authorization still
  runs on every endpoint, so no code path skips it and turning demo mode off
  cannot leave anything open.
- The demo user may read the catalogue, sales and reports, record sales, and
  optionally edit the catalogue. Users, API keys and settings are disabled.
- The assistant uses local product search only, never Gemini.
- **The API refuses to start in demo mode unless the database is marked as a
  demo database** by the seeder, so it can never be pointed at a real shop.
- Demo data resets: the demo container has no persistent volume and seeds a
  fresh catalogue with `CatalogSeeder` on every start.
- Every response carries `X-Demo-Mode: true`, and startup logs a warning.

```powershell
docker compose --profile demo up
# then open http://localhost:8080/docs
```

### Build order inside Phase 4

1. [ ] API project, health check, OpenAPI document, Scalar at `/docs`,
       `ProblemDetails`, demo mode, and the demo Compose profile.
2. [ ] Read-only catalogue: products, categories and images.
3. [ ] API keys: table, migration, authentication handler, scopes, rate limits,
       Admin view and CLI.
4. [ ] Staff sign-in and tokens, with roles from Core.
5. [ ] Sales: idempotent create, list, detail, void.
6. [ ] Reports.
7. [ ] Catalogue writes and image uploads, with ETags.
8. [ ] Assistant, users and API-key management endpoints.
9. [ ] Integration tests for every endpoint against both engines, in CI.
10. [ ] Container image built and published from the release workflow.

---

## Phase 5 — Tills on the API

- [ ] A third connection mode for the WPF application, chosen in
      `database.env` like the other two: **local** (SQLite, direct, as today),
      **direct** (PostgreSQL, as today) and **server** (through the API).
- [ ] Offline sales: a completed sale is written to a local queue first and sent
      when the connection returns, using the idempotency key, so the till keeps
      selling through a network outage.
- [ ] The catalogue is cached locally so search keeps working offline.

The direct PostgreSQL mode **stays** as a supported option for internal use —
a shop whose tills sit on its own trusted network can keep connecting straight
to the database. The API is the choice when tills must not hold database
credentials, or reach the server over an untrusted network. Both modes run the
same domain rules from `JussiMiniPos.Core`.

---

## Missing features

What a till in daily use in a shop needs and JussiMiniPos does not have yet,
beyond the phases above. Priorities:

- **High** — a real shop cannot run on the till without it.
- **Medium** — expected soon after; daily work is slower without it.
- **Low** — nice to have.

"Suggested slot" says where an item fits best. Items marked *1.0.0* should be
pulled into Phase 2; the rest are for releases after the API.

### Checkout (Kassa)

| Feature | Today | Priority | Suggested slot |
| ------- | ----- | -------- | -------------- |
| **Barcodes** — an EAN-13/EAN-8 field per product, and scanner input that adds the product straight to the cart | Products are found by number or name only | High | 1.0.0 |
| **Cash payment** — amount given, change due, rounding to 5 cents for cash where the shop applies it | Card is the only payment method | High | 1.0.0 |
| **Split payment** — part card, part cash | One method per sale | Medium | After cash |
| **Returns and refunds** — a refund linked to the original sale and its lines, as its own record, never by editing the sale | No refunds; only deleting a sale (Phase 2 replaces deletion with voiding) | High | 1.0.0 |
| **Discounts at the till** — per line or per cart, as a percentage or an amount, with a reason, limited by role | Only a product's own offer price | Medium | After VAT, which a discount must recalculate |
| **Parked sales** — set a cart aside and serve the next customer | One cart at a time | Low | — |
| **Products sold by weight** — decimal quantities and a scale | Whole-number quantities | Low | — |

### Receipts and hardware

| Feature | Today | Priority | Suggested slot |
| ------- | ----- | -------- | -------------- |
| **Receipt printing** on an ESC/POS thermal printer, and reprinting a past receipt | Receipts are on screen only | High | 1.0.0 |
| **Shop details on receipts** — business name, business ID (Y-tunnus), address, VAT breakdown | No shop details are stored | High | With VAT |
| **Cash drawer** opened through the receipt printer on a cash sale | — | Medium | With cash payment |
| **Receipt by email or as PDF** | — | Low | After the API |
| **Card terminal integration** | Payment is simulated | Medium | After the API; depends on the terminal provider |

### Closing the day

| Feature | Today | Priority | Suggested slot |
| ------- | ----- | -------- | -------------- |
| **Opening and closing the till** — starting cash float, counted cash at close, the difference recorded | — | High | With cash payment |
| **End-of-day report** — totals per payment method and per VAT rate, refunds, voids and discounts, and a printable copy | Raportit covers sales over a date range, not one till's day | High | With cash payment |
| **Sales export for accounting** — CSV per day or month, with VAT | — | Medium | Before the API's `sales:read` |

### Catalogue and stock

| Feature | Today | Priority | Suggested slot |
| ------- | ----- | -------- | -------------- |
| **Stock levels** — quantity on hand, reduced by each sale and restored by a refund, manual adjustments with a reason | No stock at all | Medium | Before a web shop integration |
| **Goods received and low-stock alerts** | — | Low | After stock levels |
| **Catalogue import and export as CSV** — bulk price changes and moving a catalogue between shops | Products are edited one by one; `--seed` loads only the demo catalogue | Medium | — |
| **Product variants** such as sizes and colours | Each variant is a separate product | Low | — |

### Security and operations

| Feature | Today | Priority | Suggested slot |
| ------- | ----- | -------- | -------------- |
| **Audit log** — sign-ins, price changes, voids, refunds, discounts, role changes: who, what and when | Not recorded | High | 1.0.0, with voiding |
| **Backup and restore from the app** — a consistent SQLite copy on demand and on a schedule; guidance for PostgreSQL | Copying the database file by hand | High | 1.0.0 |
| **Update notice** — the start screen says when a newer GitHub release exists | — | Low | — |
| **Code signing** to remove the *Windows protected your PC* warning | Unsigned installer | Medium | Choose a licence first: an OSI-approved one allows free signing through SignPath Foundation; otherwise an OV certificate, or Azure Artifact Signing through a registered business |

### Customers

| Feature | Today | Priority | Suggested slot |
| ------- | ----- | -------- | -------------- |
| **Customer records** — name and contact details attached to a sale, for invoices and returns | Sales are anonymous | Low | Personal data brings GDPR duties: consent, retention and deletion |
| **Loyalty or bonus programme** | — | Low | After customer records |

### Platform

- **Live catalogue updates** to every till with SignalR — Phase 4, optional.
- **Touchscreen-friendly layout** and keyboard shortcuts for the most common
  checkout actions — Low.

Before relying on the till for bookkeeping, check with an accountant which
receipt contents and retention periods apply to the shop. That decides details
of voiding, refunds, the audit log and the end-of-day report.

---

## Decisions

| Decision | Reason |
| -------- | ------ |
| The API lives in this repository | It shares `JussiMiniPos.Core` with the tills, and one change can update the schema, the API and the client together. Revisit if another team takes over a part or the API needs its own release cycle. |
| C# on ASP.NET Core minimal APIs, .NET 10 | Same language and runtime as the tills; the smallest addition to the codebase. |
| No EF Core | The hand-written SQL already runs on both engines through `SqlDialect`. |
| SQLite mode keeps working without a server | A single till should never need one. |
| Direct PostgreSQL mode stays alongside the API | Internal setups on a trusted network keep the simplest path; the API is for when tills must not hold database credentials. |
| VAT before the API | VAT changes the shape of products and sales, which are the core of the API. |
| Integrations use API keys; staff use sign-in | A key identifies a system, a sign-in identifies a person. |
| Demo mode is a demo identity, not disabled authorization | Turning it off cannot leave an endpoint open. |

## Open questions

- Will the shop run several tills? If not, Phase 5 can wait and Phase 2 matters
  more.
- Which licence? It decides whether free code signing is possible.
- Does a web shop integration need stock levels in the first API release?
