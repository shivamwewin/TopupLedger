# TopupLedger

B2B mobile top-up ledger for Dubai. Clean Architecture, one process, CQRS with MediatR. Commands write through MySQL transactions. Queries read through EF Core. There is no second database and no message broker.

Projects: `TopupLedger.Domain`, `TopupLedger.Application`, `TopupLedger.Infrastructure`, `TopupLedger.Api`, `TopupLedger.Portal`, `TopupLedger.Tests`.

## Run

Requirements: .NET 8 SDK, Docker, MySQL 8 if you are not using Compose.

```bash
docker compose up --build
```

API `http://localhost:8080` (Swagger in Development). Portal `http://localhost:8081`. From the host, MySQL is `127.0.0.1:3307` (published there so it does not collide with a local MySQL on 3306). Database `topupledger`, user `topup` / `topup`. Inside Compose the API uses hostname `mysql` on port 3306. Visual Studio reads `appsettings.Development.json` and connects to port 3307.

Without Compose, create the database, then:

```bash
mysql -utopup -ptopup topupledger < db/schema.sql
mysql -utopup -ptopup topupledger < db/seed.sql
dotnet run --project src/TopupLedger.Api
dotnet run --project src/TopupLedger.Portal
```

Copy `appsettings.Example.json` into the API and Portal configuration. Set `ConnectionStrings:TopupLedger` (include `GuidFormat=Char36`) and `DataProtection:KeyBase64`. Do not commit a production key. The Development files contain only the assignment’s published demo key.

`LedgerRules:Variant` defaults to **V1** because the PDF left the variant blank. Change it to the code you were assigned. See `DECISIONS.md`.

Demo partner `ak_test_retail_marina` / `sk_test_E3JNS3_UAE_2026_k7Rm`, opening balance 50,000.00 AED. Portal `admin` / `Admin123!` (ADMIN) and `viewer` / `Viewer123!` (VIEWER). Operator tokens `dev-etisalat-token` and `dev-du-token`.

`dotnet test` runs the unit tests and the MySQL integration tests. Integration tests start `mysql:8.0` with Testcontainers. On this machine that command finished **44 passed, 0 failed, 0 skipped**.

## V1 vectors

Half-up, cutoff 22:00 Asia/Dubai (fixed UTC+4). Retailer fils, distributor share fils, business day:

| Face AED | UTC instant | Retailer | Share | Business day |
| --- | --- | --- | --- | --- |
| 13.53 | 2026-09-30T19:59:59Z | 27 | 10 | 2026-10-01 |
| 125.05 | 2026-09-30T20:00:00Z | 313 | 75 | 2026-10-01 |
| 125.05 | 2026-09-30T19:59:59Z | 294 | 94 | 2026-10-01 |
| 49.99 | 2026-10-01T18:45:00Z | 100 | 37 | 2026-10-02 |
| 200.00 | 2026-10-01T19:40:00Z | 520 | 160 | 2026-10-02 |
| 109.80 | 2026-10-11T18:15:00Z | 275 | 65 | 2026-10-12 |

A 10.00 AED top-up debits 980 fils. V1 distributor share on that amount is 8 fils, credited to the parent only after `SUCCESS`.

## API

`POST /api/v1/partner/topups` returns 202. HMAC headers: `X-Api-Key`, `X-Timestamp`, `X-Signature`. `Idempotency-Key` is a UUID. Canonical string is method, path, timestamp, idempotency key, lowercase SHA-256 of the raw body, joined by `\n`. Postman: `postman/TopupLedger.postman_collection.json`.

Also: `GET /api/v1/partner/topups/{id}`, keyset `GET /api/v1/partner/topups`, `GET /api/v1/partner/wallet`, `GET /api/v1/partner/settlements/{businessDay}`, `POST /api/v1/operator/callbacks/{etisalat|du}` with `X-Operator-Token`. Health: `/health/live`, `/health/ready` (ready checks MySQL).

Transaction search is keyset pagination on `(created_at_utc, id)`. `OFFSET` is rejected. On the seeded MySQL 8 database, `EXPLAIN` for a partner, a created-at window, and a keyset cursor uses `ix_topups_partner_created` (`partner_id`, `created_at_utc`, `id`): `type=ref`, `Backward index scan`, `Using index`. An MSISDN search uses `ix_topups_msisdn_created` the same way. A status-only search on that small table picked `ix_topups_dispatch` and a filesort; `ix_topups_status_created` is still the index intended for a large status filter. These plans were not measured on a one-million-row table.

Ledger check after any run: `mysql topupledger < db/ledger-check.sql` must return no rows.

## Known gaps

Number portability is not implemented; the prefix map is authoritative. `schema.sql` is the schema source; there is no EF migration history. `schema.sql` and `seed.sql` were applied to MySQL 8 and `db/ledger-check.sql` returned no rows. Portal pages were not clicked through in a browser in this run.

Cursor was used to implement and review this solution. Commission vectors and the HMAC body hash were calculated and then locked in tests, rather than copied from an unreliable OCR of the PDF.
