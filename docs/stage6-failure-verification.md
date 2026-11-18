# Stage 6 failure verification

This document records the #102 verification. The [final Stage 6 handoff](stage6-production-handoff.md) includes the separate-process browser tests added in #103.

The integration topology runs two independent TCP Kestrel hosts (A and B), one PostgreSQL container and one Redis container. The hosts share the database, Redis namespace and Data Protection key ring. They run inside the same test process, so this verifies host restart and cross-instance routing, but does not simulate an operating-system process kill or network partition. That remains an operational validation item for #103.

## Expected failure and recovery behavior

| Failure | Degraded behavior | Recovery |
| --- | --- | --- |
| API A stops while B stays healthy | A's sockets and subscriptions disappear; B continues serving durable REST reads and writes. | A starts with a new BootEpoch. Clients reconnect to either instance and subscribe again. The old owner generation cannot become current. |
| Redis/backplane unavailable | `/health/realtime` returns 503, while `/health/ready` can remain healthy. Distributed presence, typing, subscriptions and revocation coordination fail closed. A committed PostgreSQL message remains committed even if its broadcast fails. | Restore Redis, wait for `/health/realtime` to return 200, reconnect and subscribe. Fetch messages and receipts via REST. No durable SignalR replay occurs. |
| Owner disappears during revocation | A missing owner acknowledgement cannot prove cleanup. Membership mutation rolls back. | Retry after owner lease expiry and recovery. Do not bypass the barrier. |
| Typing or presence owner becomes stale | The old lease/BootEpoch is not current. Typing expires; presence is reconstructed from live connections. | Reconnect and use fresh `GetPresence`/`GetTyping` snapshots. |
| Realtime message or receipt event missed | The client may temporarily show stale state. PostgreSQL remains authoritative. | Fetch message history, read state and receipt pages via REST. Repeated reads do not create durable duplicates. |

The Stage 6 presence, typing and distributed revocation suites additionally cover stale owner leases, membership incarnation, missing/negative/late ACKs, concurrent send/receipt/typing versus removal, and failed group cleanup. The #102 suite adds an explicit A restart while B remains available, a controlled PostgreSQL row-lock contention probe, and owner disappearance after barrier command delivery. The last case must return 503 and retain the PostgreSQL membership.

The Redis container restart test checks whether Testcontainers assigned a new host port. When it did, it recreates both API hosts with the new test endpoint before checking recovery. This is a test harness detail; production Redis should retain a stable address. The pause/unpause tests verify same-endpoint reconnect without an API restart.

The synthetic short-ticket lease tests use a five-second ticket and poll the Redis-backed snapshot until expiry. This avoids coupling their correctness assertions to host startup and CI scheduling time; production lease and revocation budgets are unchanged.

## Conversation lock diagnostics

The API emits `conversation.lock.wait` and `conversation.lock.hold` histograms in milliseconds under meter `Chat.Api.ConversationLock`, tagged only with low-cardinality `operation`: `message.send`, `receipt.advance`, `membership.change`, or `typing.change`. Wait starts immediately before the existing `FOR UPDATE` query and ends after acquisition. Hold ends after commit (or when an early return unwinds the transaction). The metric adds no SQL, database schema or lock-order change.

For reproducible contention, the integration test opens a PostgreSQL transaction, locks the target conversation row, starts an HTTP send, waits for PostgreSQL `pg_stat_activity.wait_event_type = 'Lock'`, then releases the row. It writes observed wait/hold milliseconds to test output and asserts metric emission, without a brittle wall-clock threshold. Run the same probe on the target deployment and compare distributions with the 5-second coordination budget; a local CI sample is diagnostic, not a production latency guarantee. Under concurrent sends, send versus receipt, and send versus membership removal, the same row serializes the operations. Typing obtains the lock through `ChatHub.TypingMembers`; its Redis coordination can extend lock hold. No lock optimization is introduced in #102.

Example local run (Windows Docker Desktop, Release build, 2026-11-17; wait/hold in ms): send 31.88/17.75, receipt 41.08/20.54, typing 36.89/45.55, membership removal 24.59/132.94. These are observed samples, not thresholds or capacity estimates. Reproduce with `dotnet test tests/Chat.Api.IntegrationTests/Chat.Api.IntegrationTests.csproj -c Release --filter FullyQualifiedName~Stage6FailureVerificationTests --logger "console;verbosity=detailed"`; the xUnit standard output lists each sample.

## Operations

- `/health/live`: process liveness; `/health/ready`: PostgreSQL readiness; `/health/realtime`: Redis coordination/backplane readiness.
- Inspect structured `Distributed revocation barrier failed; membership rolled back` warnings when membership changes return 503. Check Redis connectivity, owner leases, BootEpoch and failed ACKs before retrying.
- A restart requires fresh SignalR connection and conversation subscriptions. A Redis restart invalidates uncertain coordination state; use REST resynchronization after connectivity returns.
- PostgreSQL is the durable source of truth. SignalR and Redis do not provide durable event replay. The current deployment still needs #103 operational validation for process kills, partitions, sustained contention and production capacity.
