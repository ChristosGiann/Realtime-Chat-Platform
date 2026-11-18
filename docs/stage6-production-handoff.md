# Stage 6 complete — Redis scale-out handoff

Το Stage 6 συνδέει περισσότερα από ένα API instances με κοινή PostgreSQL, Redis και SignalR Redis backplane. Η PostgreSQL παραμένει η μόνη durable πηγή αλήθειας για Identity, memberships, messages και delivery/read progress. Redis και SignalR μεταφέρουν προσωρινό coordination και live events. Επιτυχές `SendAsync` δεν σημαίνει ότι ο browser παρατήρησε το event και δεν υπάρχει durable realtime replay.

## Χαρτογράφηση

| Issue | Παραδοτέο |
| --- | --- |
| #96 | [Distributed semantics και failure model](stage6-distributed-semantics.md) |
| #97 | [Redis configuration, namespace, health και shared keys](redis-infrastructure.md) |
| #98 | [SignalR Redis backplane](signalr-redis-backplane.md) |
| #99 | [Distributed connection ownership και presence](distributed-presence.md) |
| #100 | [Distributed typing leases](distributed-typing.md) |
| #101 | [Cross-instance revocation και protected publication](distributed-revocation.md) |
| #102 | [Failure, restart, REST recovery και lock contention verification](stage6-failure-verification.md) |
| #103 | Πραγματικά browser contexts σε δύο ξεχωριστές API διεργασίες και τελική regression/production handoff επαλήθευση. |

Το [#88 AI unread summaries](https://github.com/ChristosGiann/Realtime-Chat-Platform/issues/88) παραμένει deferred και δεν αποτελεί μέρος του Stage 6.

## Απαιτούμενη deployment τοπολογία

- Όλα τα API instances χρησιμοποιούν την ίδια PostgreSQL database και έχουν εφαρμοσμένες τις EF migrations πριν δεχθούν traffic. Δεν υπάρχει Stage 6 migration.
- Όλα τα συμβατά instances συνδέονται στο ίδιο Redis coordination history και στο ίδιο SignalR backplane. Θέστε `Redis:Enabled=true` και `Redis:BackplaneEnabled=true`, κοινά `ConnectionString`, `ApplicationName`, `Deployment`, `ProtocolVersion` και `RunId`. Κάθε διεργασία έχει **μοναδικό** server-owned `RealtimeInstance:InstanceId`· κάθε boot δημιουργεί νέο `BootEpoch`.
- Το Redis namespace είναι `application:deployment:protocol:run:coordination:` για leases, typing, commands/ACKs και `application:deployment:protocol:run:backplane:` για SignalR Pub/Sub. Το Redis database number μόνο του δεν απομονώνει Pub/Sub. Μη μοιράζεστε namespace ανάμεσα σε μη συμβατές εκδόσεις ή deployments. Μην αποθηκεύετε secrets, message bodies ή durable receipts στο coordination namespace.
- Όλα τα instances μοιράζονται persistent `SharedDataProtection:KeyRingPath` και ακριβώς ίδιο `SharedDataProtection:ApplicationName`. Το key ring χρειάζεται προστατευμένο κοινό storage, κατάλληλα permissions, backup και encryption at rest. Μην το τοποθετείτε σε ephemeral Redis. Τα cookie/session settings πρέπει να είναι συμβατά σε όλες τις διεργασίες.
- Ο reverse proxy/load balancer πρέπει να υποστηρίζει HTTPS/WSS και WebSocket upgrade. Το frontend χρησιμοποιεί κανονικό SignalR negotiation και credentialed cookies· απαιτείται session affinity για την ακολουθία negotiation/transport όπου το transport το χρειάζεται. Shared cookies και Redis backplane δεν υποκαθιστούν affinity. Διατηρήστε το configured CORS allowlist και Origin validation για κάθε πραγματικό frontend origin. Μη χρησιμοποιήσετε client-supplied user IDs για authentication.
- Το local Compose Redis είναι development configuration: private network, ACL/credentials, TLS, Redis HA και failover policy ορίζονται στο deployment. Για HA απαιτείται μία συνεπής writable Redis history· divergent writable partitions δεν αποτελούν υποστηριζόμενο consensus model. Μη θεωρείτε την ύπαρξη Redis service ως αυτόματη απόδειξη capacity ή HA.

Ενδεικτικές **ονόματα** environment settings, χωρίς παραγωγικά credentials:

```text
ConnectionStrings__ChatDatabase
Redis__Enabled=true
Redis__BackplaneEnabled=true
Redis__ConnectionString
Redis__ApplicationName
Redis__Deployment
Redis__ProtocolVersion
Redis__RunId
RealtimeInstance__InstanceId
SharedDataProtection__KeyRingPath
SharedDataProtection__ApplicationName
Cors__AllowedOrigins__0
```

Κρατήστε το `Microsoft.AspNetCore.SignalR.StackExchangeRedis` στην patched έκδοση `10.0.15`. Πριν από rolling upgrade, επιβεβαιώστε συμβατό protocol/namespace και αποσύρετε παλιές connections· μη σερβίρετε ασύμβατες Stage 5/6 instances στο ίδιο traffic pool.

## Security και publication contract

Κάθε protected event επιλέγει **ρητούς connection recipients** κάτω από PostgreSQL conversation `FOR UPDATE`, με τρέχον roster, ακριβές `MembershipId` (incarnation), subscription generation, `InstanceId/BootEpoch` και `CoordinationEpoch`. Το SignalR group/backplane είναι μηχανισμός routing, όχι authorization boundary. Stale subscription/event δεν μεταφέρεται σε νέα incarnation.

Remove/leave κάνει all-owner revocation ACK barrier **πριν** το PostgreSQL commit, με συνολικό budget 5s. Missing owner ή ACK, NACK, Redis uncertainty και epoch change οδηγούν σε rollback και retryable 503. Μετά από επιτυχές remove, μελλοντικές protected publications δεν επιλέγουν revoked subscription. Bytes που είχαν ήδη μπει στην ουρά πριν από το barrier δεν ανακαλούνται. Αν το commit αποτύχει μετά τα ACKs, παλιές subscriptions παραμένουν fenced και απαιτείται fresh subscribe.

`Sent = PostgreSQL commit`, `Delivered = durable recipient acknowledgement`, `Read = durable read progress`, με **Read ⇒ Delivered ⇒ Sent**. REST history, delivery/read state και receipt snapshots αποκαθιστούν χαμένα live events. Η ανάκτηση δεν δημιουργεί duplicate durable state· idempotent `ClientMessageId` συνεχίζει να ισχύει.

## Health και degraded mode

| Endpoint | Ερμηνεία |
| --- | --- |
| `/health/live` | Η API διεργασία απαντά· δεν ελέγχει dependencies. |
| `/health/ready` | PostgreSQL readiness. Αν αποτύχει, μη δρομολογείτε durable API traffic. |
| `/health/realtime` | Redis connectivity. 503 σημαίνει ότι distributed realtime/coordination δεν είναι διαθέσιμα· το durable REST μπορεί να παραμένει διαθέσιμο αν `/health/ready` είναι 200. Το 200 δεν πιστοποιεί μόνο του end-to-end delivery ή πλήρη cluster safety. |

Σε Redis outage: μη μετατρέπετε unknown presence σε «όλοι offline», μη χρησιμοποιείτε local-only typing fallback και μη θεωρείτε το backplane durable queue. Committed writes παραμένουν στην PostgreSQL· post-commit publication failure δεν αναιρεί το REST success. Remove/leave χωρίς αποδεδειγμένο barrier αποτυγχάνει κλειστά. Μετά την αποκατάσταση, οι clients επανασυνδέονται, κάνουν fresh authorized subscribe και REST resync. Redis reset/epoch αλλαγή ακυρώνει uncertain owner και typing state· παλιές leases δεν resurrect. Typing απαιτεί νέα activity και λήγει σε έως 5s από accepted start. Presence ξαναχτίζεται από current connections και μπορεί να είναι προσωρινά unknown.

Σε API crash/restart, το άλλο instance συνεχίζει durable REST και live λειτουργία. Το νέο process έχει νέο BootEpoch· ο browser χρειάζεται reconnect/fresh subscribe. Οι leases του παλιού owner λήγουν χωρίς graceful disconnect. Το automated #103 Playwright test σκοτώνει πραγματική API διεργασία και ελέγχει ότι το άλλο instance συνεχίζει, ενώ missed message/receipt ανακτώνται από REST. Τα #99–#102 integration tests επιβεβαιώνουν lease/epoch fencing, Redis pause/restart, stale owner, typing expiry και fail-closed revocation υπό races.

## Automated verification

Το `frontend/playwright.stage6.config.ts` εκκινεί δύο **ξεχωριστές OS API διεργασίες** (`5081`, `5082`), δύο Vite origins (`5174 → A`, `5175 → B`), κοινή PostgreSQL, Redis namespace και Data Protection key ring. Τα browser tests ελέγχουν πραγματικά WebSocket URLs και API requests, χωρίς mock transport ή production load balancer. Ο test-only controller στο `127.0.0.1:5090` σκοτώνει/επανεκκινεί την A για process failure verification. Το CI προσθέτει Redis service στο browser E2E job. Οι υπόλοιπες Stage 1–5 E2E ροές συνεχίζουν να χρησιμοποιούν το αρχικό single-instance setup.

Τοπικά, χρησιμοποιήστε **αποκλειστική προσωρινή** PostgreSQL και Redis, θέστε `E2E_CONNECTION_STRING` και `E2E_REDIS_CONNECTION_STRING`, και εκτελέστε από το `frontend`:

```powershell
npm run test:e2e:stage6
```

Οι δοκιμές καλύπτουν cross-instance `MessageCreated`, durable REST history χωρίς duplicate, cross-instance receipts/read, multi-tab presence, typing και expiry, removal/rejoin με νέα incarnation και παλιά ACK rejection, καθώς και process kill/restart με missed message/receipt REST recovery. Τα υπάρχοντα distributed integration tests καλύπτουν Redis outage/restart και revocation ACK/race failures που δεν χρειάζονται browser.

## Διαγνωστικά και γνωστά όρια

- Παρακολουθήστε τα τρία health endpoints, Redis connectivity/epoch, owner lease και `Distributed revocation barrier failed; membership rolled back` warnings. Το [conversation lock meter](stage6-failure-verification.md) δίνει `conversation.lock.wait`/`conversation.lock.hold` ανά operation. Τα δείγματα του #102 είναι τοπικές integration παρατηρήσεις, **όχι production benchmark** ή εγγύηση ότι κάθε deployment μένει κάτω από 5s.
- Redis Pub/Sub και SignalR δεν παρέχουν exactly-once delivery, durable replay ή client observation ACK. Ένα event μπορεί να χαθεί κατά outage/reconnect· το REST resync είναι υποχρεωτικό.
- Ήδη enqueued bytes δεν ανακαλούνται. Idle sockets δεν ανιχνεύουν ακαριαία κάθε administrative Identity deletion/security-stamp change· η επόμενη protected operation επανελέγχει την ισχύ της session.
- Οι automated δοκιμές καλύπτουν API process kill, όχι πραγματική δικτυακή partition ή divergent writable Redis split brain. Αυτά χρειάζονται ελεγχόμενη deployment validation. Autoscaling infrastructure, production capacity/load test και Redis HA topology δεν παρέχονται από το repository.
- Το #88 παραμένει deferred. Δεν υπάρχει outbox/exactly-once ή durable SignalR replay στο Stage 6.

Με επιτυχή Release backend, frontend/UI, παλιά και νέα browser E2E, καθαρό EF model και πράσινο CI, το #103 πληροί το core Stage 6 acceptance contract. Το parent #95 μπορεί να αξιολογηθεί για closure **μετά** το review και merge του #103· δεν κλείνει από αυτό το PR.
