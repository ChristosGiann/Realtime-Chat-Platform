# Cross-instance membership revocation — #101

This document records the #101 implementation. For the final Stage 6 deployment contract, see the [production handoff](stage6-production-handoff.md).

## Authority and ordering

PostgreSQL remains the authority for membership, messages and receipts. Redis stores
only short lived owner leases, command metadata and ACKs. SignalR groups and the
backplane route bytes; neither grants access. In distributed mode, removal/leave
serializes on `Conversations FOR UPDATE`, checks authorization and the exact current
`MembershipId`, snapshots live registered `(InstanceId, BootEpoch)` owners and the
target's exact connection/subscription generations, and calls EF `SaveChanges`.
The transaction is **not committed** yet.

The coordinator publishes one namespaced command to every owner in the snapshot,
including owners without a target connection. It requires a valid ACK from each
within a **single five second budget** covering the snapshot and barrier. Expiry of
an owner lease after selection does not remove it from this required set. A lost
command, missing/late ACK, NACK, Redis timeout or changed coordination epoch means
rollback and retryable HTTP 503. Redis Pub/Sub subscriber counts and successful
enqueue are never treated as ACK. A no-op removal has no barrier or false event.

The command contains a random operation ID, conversation/user/removed MembershipId,
coordination epoch, expected owner, captured exact subscription values and a
deadline measured by Redis `TIME`. `revoke-operation:{id}` and
`revoke-ack:{id}:{owner}` live at most 30 seconds. The remote handler validates
operation, epoch, owner, incarnation and deadline, deletes only matching Redis
subscription values, removes only matching local generations from protected groups,
and forgets typing sequences. If group cleanup fails, it aborts the socket and
returns NACK. Local command processing is serialized and its bounded 30s result
cache keeps a failed operation NACK across duplicate deliveries. Duplicate commands
are idempotent for the old generation; they
cannot remove a new generation or incarnation. Late ACK writes are rejected
atomically against the Redis deadline and operation metadata.

Remote handlers do **not** acquire the PostgreSQL conversation lock: the
coordinator holds it while awaiting their ACKs. Future Subscribe calls wait for
that lock and must recheck membership. No local mutex is held while waiting for
the database or the remote barrier. After all ACKs, the coordinator commits and
returns success. Post-commit `ConversationAccessRevoked` and membership events are
best-effort UI notifications, not security authority.

If PostgreSQL commit fails **after** fencing, the membership remains in the
database but old subscriptions stay fenced. The client must make a fresh authorized
Subscribe. No rollback path resurrects old generations. Rejoin creates a new
MembershipId with fresh receipt state and requires a fresh subscription; old
commands and typing leases remain tied to the previous incarnation.

## Protected publications

`MessageCreated`, `ReceiptAdvanced`, membership notifications, presence and typing
select explicit connection IDs from the current PostgreSQL membership roster and
matching distributed subscription incarnation while holding the conversation row
lock through publication enqueue. Redis checks current coordination epoch,
connection/owner leases, authenticated user, ticket expiry and exact MembershipId
before selecting a recipient. The backplane then routes only to those IDs. No
blind `Clients.Group` publication is an authorization boundary. The access-revoked
UX notification targets current user connections after commit.

The guarantee concerns **future protected publication after successful removal**.
Bytes enqueued before the barrier cannot be recalled. Durable writes committed
before a Redis/backplane failure remain successful and recover through REST; lost
realtime events are not replayed. During Redis uncertainty removal/leave fails
closed, and protected realtime publication cannot safely select recipients.

This design assumes one shared PostgreSQL authority and one Redis coordination
history, compatible deployments and the session-affinity/shared-key requirements
in [Stage 6 semantics](stage6-distributed-semantics.md). It does not provide
consensus across divergent writable Redis partitions, exactly-once delivery or
instant detection of an idle socket's deleted Identity user. Restart, long pause,
partition and full browser recovery verification remain in #102/#103.
