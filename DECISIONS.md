# Decisions

## Variant

The assignment form left the variant blank. The running configuration is **V1**: half-up rounding and a 22:00 Dubai business-day cutoff. `LedgerRules.FromVariant` is the only switch. Money code sees a rounding mode and a cutoff time, not `if (variant == "V1")`. Set `LedgerRules:Variant` to the code you were assigned before submission. V0 and V2 are implemented and covered by the same unit vectors.

## Money

Amounts are integer fils (`BIGINT`). `decimal` is used only to apply a percentage, then rounded to a whole fil. Credits are positive and debits are negative. Each movement is a pair that sums to zero. The house clearing account is the contra and may go negative. Partner wallets have `CHECK (balance_fils >= 0)`.

A debit is `UPDATE wallets SET balance_fils = balance_fils - @debit WHERE partner_id = @id AND balance_fils >= @debit`. Zero rows means 402. That conditional update is the overspend guard. An in-memory lock would not hold across API instances.

Historical ledger rows are not updated or deleted. MySQL triggers raise SQLSTATE 45000. Refunds and expiry are compensating pairs.

## Idempotency

The idempotency row is committed as `IN_PROGRESS` before the debit, so another instance can see it. There is no `ConcurrentDictionary`. Same key and same body returns the stored status and body bytes. Same key and a different body is 422. A live lease is 409. After 24 hours the row is reset and the key can be reused. Validation, authentication, and rate limiting run before the key is claimed, so a bad request does not consume it. 402 and duplicate `clientRef` are stored on the idempotency row after the business transaction rolls back.

Deadlocks (1213) and lock-wait timeouts (1205) are retried inside the write. The unique `(partner_id, idempotency_key)` index plus rollback is what prevents a double debit if two requests pass the claim together.

## Callbacks

`X-Operator-Token` is compared in constant time. The top-up row is locked with `SELECT … FOR UPDATE`. A repeated `(topup_id, event_seq)` returns 200 and changes nothing. A lower `eventSeq` than the highest already seen is stored and ignored. Terminal states do not change. `FAILED` and expiry refund the debit once. A callback after `EXPIRED`, including a late `SUCCESS`, is written to `late_callbacks` and does not move money. `SUCCESS` credits the parent distributor only when `distributor_share_fils > 0` and a parent exists. A negative share from rounding is not posted; the published slabs do not produce one.

## Rate limit

`POST /api/v1/partner/topups` uses a MySQL token bucket, 20 permits per second, burst 40, shared by every instance. The first write is `INSERT … ON DUPLICATE KEY UPDATE`, which takes an exclusive row lock. `INSERT IGNORE` followed by `SELECT … FOR UPDATE` deadlocks under a burst because the duplicate path takes only a shared lock. In-memory limiting would triple the allowance behind three instances.

## Authentication

Partner HMAC is SHA-256 of the raw body, then HMAC-SHA256 of `METHOD`, path, unix timestamp, idempotency key, and that hash, joined by `\n` with no trailing newline. GET uses an empty idempotency key and the hash of zero bytes. Clock skew is ±300 seconds. An unknown key and a bad signature return the same 401 body. Comparison is constant time.

The HMAC secret must be recoverable, so it is encrypted with AES-256-GCM rather than hashed. The 32-byte key comes from `DataProtection:KeyBase64`. Committed appsettings leave it empty. The Development file and Compose file contain only the published assignment demo key.

Portal passwords are PBKDF2-SHA256, 100,000 iterations.

## Settlement and reconciliation

A settlement batch is one row per business day. Lines are aggregates of `SUCCESS` top-ups for that day, plus the parent’s income from children. Generating twice does not insert a second batch. The worker claims `QUEUED` work with `FOR UPDATE SKIP LOCKED`. The partner API returns 404 until the batch is `READY`. A partner with no activity still gets a zero line.

Reconciliation streams the CSV to a temp file while hashing it. The same file hash returns the existing run. Duplicate operator transaction ids in the file are counted, not stored twice. Categories are `MISSING_IN_OURS`, `AMOUNT_MISMATCH`, `STATUS_MISMATCH`, and `MISSING_IN_OPERATOR`. An MSISDN-only difference is not its own category; it fails the match and is not given a separate finding type.

## Other boundaries

- Prefixes decide the operator. Number portability is not implemented. `055` is DU, so the HMAC sample body that claims Etisalat is a signature example and fails validation.
- Dubai time is a fixed UTC+4 offset. The OS time zone is never consulted. V0’s 00:00 cutoff is the Dubai calendar date; every other cutoff rolls the instant forward once local time reaches it.
- `schema.sql` is the DDL source. EF Core maps that schema. A second migration history would drift.
- Cross-partner `GET` of a top-up returns 403 `FORBIDDEN`. That code is not in the assignment’s error table.
- A `GET` of another partner’s top-up is the only invented status code. Settlement JSON is the aggregate described above; the PDF did not specify the property names.
