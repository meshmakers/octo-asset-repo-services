---
description: Whole-request retry of GraphQL mutations whose MongoDB transaction failed with TransientTransactionError - scope, bound, configuration, side effects and limits.
background: true
---

# GraphQL: retry of transient transaction errors

Every GraphQL mutation request runs in one MongoDB transaction: `OctoSessionListener` starts it before the resolvers run
and commits it afterwards (or aborts it when the request has errors). MongoDB aborts such a transaction when it collides
with a parallel transaction on the same document - for example two requests that create entities numbered from the same
auto-increment counter (the engine allocates numbers with one `findOneAndUpdate` with `$inc` per counter and batch). The error carries the label
`TransientTransactionError`: the whole transaction may be run again.

The asset repository does that for GraphQL requests (`TenantDocumentExecutor`,
`TransientTransactionRetry`):

- **What is retried.** A mutation request whose transaction failed with an error labelled `TransientTransactionError`,
  in a resolver or in the commit. The request is executed again as a whole: new session and transaction, every
  resolver again from the request's variables, so entities and their numbers are built afresh (the number written
  into a rolled-back entity is not reused from memory). Rolled-back numbers are handed out again, so parallel
  creations stay gap-free.
- **What is not retried.** Errors without the label (they surface as before); queries and subscriptions (no
  transaction); a commit that ends with `UnknownTransactionCommitResult` - the transaction may have committed on the
  server, and running the handler again could write twice. For that label the driver's own commit retry applies (it
  retries a retryable commit error once); the error then surfaces as before.
- **Bound.** `GraphQl:TransientTransactionRetry:MaxAttempts` attempts in total (default 10, `1` turns the retry off),
  with a full-jitter backoff between them: a random delay up to `BaseDelay * 2^(retry-1)` (default 20 ms), at most
  `MaxDelay` (default 1 s) - about 4.3 s of waiting at worst, half of it on average. A conflicting transaction fails
  at once, so a burst of parallel creations on one counter needs several retries until every request had its turn.
  The last attempt's errors are the response.
- **Time budget.** No retry is scheduled once `GraphQl:TransientTransactionRetry:MaxElapsed` (default 10 s) has
  passed since the first attempt started (a retry scheduled just before may start up to `MaxDelay` later). A counter conflict fails within milliseconds; a transaction the server
  aborted because it ran past `transactionLifetimeLimitSeconds` also carries `TransientTransactionError`, and running
  such a large mutation again would only repeat its load. It is logged at `Warning` level and surfaces as before.
  Zero or less allows no retry.
- Every retry is logged at `Information` level (attempt, delay); a request that needed retries logs the number of
  attempts at the end.

## Side effects and their limits

A retry must not repeat what happened outside the transaction. The request is retried only when every top-level field
of the mutation is `runtime` and it dispatched no CK method call; everything else is not retried and fails with
the first error, as without the retry.

| Side effect | Behaviour on a retry |
|---|---|
| Runtime entity writes (create, update, delete, associations, auto-increment `$inc`) | Inside the transaction: rolled back with the failed attempt, written once by the committed attempt. |
| Engine notifications and staged binaries (`RegisterTransactionCallbacks`: after-commit actions, GridFS cleanup) | After-commit actions run only for the committed attempt. An attempt that failed before its commit runs its rollback actions (its staged GridFS bytes are deleted). An attempt whose commit failed keeps its staged bytes, because the session never runs rollback actions after a commit attempt; they are orphans, and `GridFsOrphanSweeper` removes them after its grace period. |
| Rt entity subscriptions | Fed by MongoDB change streams: only committed changes are seen. |
| File system sibling-name locks | Released in `OctoSessionListener` after each attempt's commit or abort. |
| DataLoader caches, GraphQL listeners and validation rules | Built per attempt; the options are restored to their state before the first attempt, so no listener runs twice. |
| Uploaded files (`IFormFile` in a large-binary attribute) | Read again from the buffered form for the next attempt. |
| CK method calls (`runtime.<type>.<method>`) | **Not retried.** The handler may call other systems and runs outside the request transaction; the resolver marks the request before dispatch. |
| `constructionKit`, `streamData`, `blueprints` mutations | **Not retried.** They write outside the MongoDB transaction (CK cache, CrateDB, blueprint events). A mutation that mixes `runtime` with one of them, or uses a fragment on the mutation root, is not retried either. |

## Configuration

```json
{
  "GraphQl": {
    "TransientTransactionRetry": {
      "MaxAttempts": 10, "BaseDelay": "00:00:00.020", "MaxDelay": "00:00:01", "MaxElapsed": "00:00:10"
    }
  }
}
```

Environment variables: `OCTO_GraphQl__TransientTransactionRetry__MaxAttempts` and so on.

## Tests

- `tests/AssetRepositoryServices.UnitTests/GraphQL/RequestHandling/TransientTransactionRetryTests.cs`: injected errors -
  retried with the label (resolver and commit phase), not retried without it, never for `UnknownTransactionCommitResult`,
  bounded by attempts and by the time budget (an attempt that outlasts it is not retried), not for non-runtime mutations, requests without a transaction or with a method call; listeners and
  validation rules start from the original options on every attempt.
- `tests/AssetRepositoryServices.IntegrationTests/GraphQL/Mutations/TransientTransactionRetryGraphQlTests.cs` (MongoDB
  replica set via Testcontainers): 10 parallel creations of an auto-numbered type in separate requests all succeed with
  distinct, gap-free numbers; an injected transient error is retried once and the entity and its number exist once;
  an error without the label is not retried; the bound holds; a retried create with a binary attribute leaves its
  uploaded bytes in GridFS once.
- `tests/AssetRepositoryServices.IntegrationTests/GraphQL/CkV2/MethodCallTransientRetryTests.cs`: a mutation that
  dispatched a CK method call through the real resolver and gateway is not retried on a transient error; the handler
  is dispatched once.
