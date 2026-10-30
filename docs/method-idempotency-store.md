# Method idempotency store (AB#7824, F3.5)

A CK method call that carries an idempotency key runs at most once per tenant, method, caller subject and key, whichever
asset repository replica the retry reaches. The rules (scope, parameter fingerprint, in-progress, stored results, lifetime,
store location) are OD-4 (AB#6922); the filter that applies them is `CkMethodIdempotencyFilter` (octo-common-services,
band 300, AB#7820). This document describes the store behind it: `Services/Methods/MongoMethodIdempotencyStore.cs`
(`ICkMethodIdempotencyStore`, contract AB#7817). The GraphQL argument `idempotencyKey` that feeds it is K6 (AB#7825): the optional `idempotencyKey: String` argument on every typed
method mutation (see the section "Method idempotency key argument" in `CLAUDE.md`).

## Where the records live

- Collection `MethodIdempotency` in the **tenant database**, one document per key. It is **part of the tenant backup** (the
  dump has no exclusion) and is removed with the tenant. A tenant copy under another tenant id carries dead records: the
  tenant id is part of the key hash, so they never match.
- Written through the raw admin client of `ITenantMongoDatabaseProvider`, outside the request's transaction (the claim is
  visible to every replica at once), with majority write concern. The collection and its TTL index are created on first use.

## Document

| Field | Content |
|-------|---------|
| `_id` | Key id: lower-case hex SHA-256 over tenant, method, caller subject and the raw key (length-prefixed). **The raw key is never stored.** |
| `state` | `0` in progress, `1` completed, `2` unknown (burned) |
| `methodId` | Qualified method id |
| `firstInvocationId` | Invocation that took the claim; only it may complete or release |
| `fingerprint`, `fingerprintKeyId` | Keyed HMAC of the parameters as given by the pipeline, and the id of the key that made it. **Raw parameters are never stored.** |
| `claimedAt`, `leaseUntil`, `expiresAt` | Claim time, end of the in-flight window, end of the lifetime (UTC) |
| `response` | The response envelope (JSON) of a completed call, else `null` |

Methods whose result is `resultSensitive` (or unknown) refuse a key in the filter before the store is reached (OD-4 G2 = A),
so no sensitive result is ever written.

## Atomic claim

- A claim is an **insert**; the unique `_id` makes exactly one of any number of concurrent calls (on any replica) the
  winner. A duplicate key means a record exists.
- An expired record is taken over with one `findOneAndReplace` whose filter matches only a record that is still expired
  (`expiresAt <= now`), so of two takers one wins.
- `Complete` (state `completed` or `unknown`) and `Release` are conditional on `_id`, `firstInvocationId`, `state = in progress`
  and `expiresAt > now`.
- The contract's `Complete`/`Release` carry the key id but not the tenant. The owner always runs in the process that claimed,
  so the store remembers the tenant of its own in-flight claims in memory (swept after the record lifetime). A call for a claim
  this process does not know returns `false`; the lease then lapses and the key reads as unknown (burned), which is the safe side.

## Expiry

- Every document has its own `expiresAt` = claim time + `Methods:Idempotency:Ttl` (default 24 h, maximum 7 d, a larger value is
  clamped and logged once, zero or negative fails the start). A changed setting applies to **new claims**; no index is touched.
- A TTL index (`expiresAt_ttl`, `expireAfterSeconds: 0`) removes expired documents; it is **housekeeping only**. The store
  compares `expiresAt` itself, so a record the monitor has not removed yet (it runs about once a minute) counts as expired.
- Expiry uses the replica's clock. Clock skew between replicas is of the order of the lifetime error (seconds on a 24 h lifetime).

## Fails closed

Any database problem is thrown as `CkMethodIdempotencyStoreException`: no admin credentials (`System:AdminUser`), unknown tenant,
unreachable MongoDB, timeout (10 s per call), unexpected write result. The store never reports a claim on a guess. The filter maps it
to `METHOD_HANDLER_NOT_AVAILABLE` with reason `idempotency-store-unavailable`; the method is not executed and the caller retries
later. Calls without a key never touch the store. This differs from `MongoFileSystemNameLeaseStore`, which proceeds without
admin credentials.

## Registration

`RuntimeEngineBuilderExtensions` registers `AddCkMethodIdempotency()` and `MongoMethodIdempotencyStore` as the store. Keyed
calls additionally need the fingerprint key (`Methods:Idempotency:Fingerprint:Current:Id` / `Secret`, at least 32 bytes base64,
a secret of the deployment); without it the filter refuses a keyed call (`fingerprint-unavailable`). Unkeyed calls are unaffected.

## Latency of a keyed call

A keyed call adds two writes to the call: **claim** (insert) and **complete** (update). Measured by
`MongoMethodIdempotencyStoreTests.Benchmark_KeyedCall_ClaimAndComplete_LatencyIsReported` (500 samples after 50 warm-up
calls, sequential, single-node replica set in a Testcontainer on the developer machine, majority write concern; run
`dotnet test -c DebugL --filter "FullyQualifiedName~Benchmark_KeyedCall" --logger "console;verbosity=detailed"`):

| Step | p50 | p95 | p99 |
|------|-----|-----|-----|
| claim | 3.4 ms | 6.0 ms | 8.5 ms |
| complete | 3.8 ms | 7.0 ms | 9.1 ms |
| **claim + complete** | **7.5 ms** | **12.0 ms** | 16.2 ms |

The figures are the cost of the store on a local network; a replica set across nodes adds the majority acknowledgement round trip.
Re-measure on test-2 at the Phase 3 gate (AB#7829). The overhead budget of the method path is in
`octo-common-services/docs/method-performance.md`.
