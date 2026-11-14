# Stage 5 — επαλήθευση και παράδοση στο Stage 6

Το Stage 5 (#76, #77–#84) ολοκληρώνει κοινή authenticated SignalR σύνδεση ανά tab,
authorized presence/typing και durable per-member delivery/read receipts.
PostgreSQL παραμένει η πηγή αλήθειας. Το merge στο dev δεν ενημερώνει τη main.

## Εγγυήσεις

| Περιοχή | Πραγματικό contract |
| --- | --- |
| Sent | Επιτυχές message commit στην PostgreSQL, με τα υπάρχοντα idempotent REST retries |
| Delivered | Current member ACK μετά από client observation/synchronization πλήρους retained prefix |
| Read | Explicit durable read advancement· στο ίδιο transaction προχωρά και D ώστε R≤D |
| Group | Ανεξάρτητα R/D ανά current membership· UI counts εξαιρούν sender και χειρίζονται N=0 |
| Rejoin | Νέο server-owned MembershipId, null R/D· retained history παραμένει προσβάσιμο με νέα εξουσιοδότηση |
| Events | Receipt notifications μόνο μετά το commit· failure/rollback/no-op δεν δημιουργούν false transition |
| Recovery | REST snapshots από PostgreSQL, roster-aware pagination και serialized client refresh· όχι replay |
| Presence | Online όταν υπάρχει τουλάχιστον μία γνωστή valid connection στην ίδια API instance |
| Typing | Authorized subscription, server throttle/5s lease, client idle stop και ανεξάρτητη λήξη |
| Revocation | Conversation lock συντονίζει membership, subscriptions και publication· current recipients μόνο |

Τεκμηρίωση: [shared connection](signalr-shared-connection.md),
[presence](signalr-presence.md), [typing](signalr-typing.md),
[delivery persistence](message-delivery-state.md), [receipt events](receipt-events.md),
[React UX](stage5-react.md), [αρχικό design](stage5-semantics.md).

## Κάλυψη

| Συμπεριφορά | Έλεγχοι |
| --- | --- |
| Πρώτη/τελευταία connection, expiry και authorized presence | SignalRPresenceTests και stage5.spec.ts με δύο πραγματικά tabs |
| Typing throttle, expiry, aggregation, subscription generation και isolation | SignalRTypingTests, messages.spec.ts, typing.spec.ts και removal E2E |
| D monotonic, UUID/timestamp ordering, R⇒D, backfill και migration round-trip | DeliveryStateTests και DeliveryMigrationTests σε PostgreSQL 18/Testcontainers |
| Post-commit visibility, rollback, transport failure, no-op ACK | ReceiptBroadcastTests με ανεξάρτητο DbContext και SignalR barriers |
| Snapshot pagination, cohort replacement, stale incarnation | ReceiptBroadcastTests και receipt-snapshots.spec.ts |
| Direct Sent → Delivered → Read και persisted invariant | receipts.spec.ts με πραγματικό browser/API/PostgreSQL/SignalR |
| Group 0/2 → 1/2 → 2/2, δύο tabs, remove/rejoin, N=0 | stage5.spec.ts, API snapshots και stale ACK 409 |
| Χαμένο read event ενώ ο sender είναι offline | stage5.spec.ts, επαναφορά από REST και reload |
| Gap 35 messages, ασφαλές prefix ACK και visible pagination | reconnect.spec.ts και blocked-history UI test |
| Άμεσο cleanup και κανένα νέο protected receipt/typing/presence μετά από removal | stage5.spec.ts, stage3/stage4 E2E και backend authorization tests |
| Online retry και refresh κατά εκκρεμή membership mutation | messages.spec.ts και conversations.spec.ts με ελεγχόμενα races |
| Stage 1–4 regressions | Ολόκληρη backend/UI/E2E suite, χωρίς skipped scenarios |

## Εκτέλεση και CI

Απαιτούνται Docker, SDK του global.json, Node.js 24 και Chromium. Οι integration
tests δημιουργούν αναλώσιμες PostgreSQL 18 βάσεις με Testcontainers. Το E2E connection
string πρέπει να αφορά αποκλειστική δοκιμαστική βάση, ποτέ production δεδομένα.

```powershell
dotnet build RealtimeChat.sln --configuration Release
dotnet test RealtimeChat.sln --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project src/Chat.Api --configuration Release --no-build
git diff --check
cd frontend
npm ci
npm run lint
npm run build
npm run test:ui
$env:E2E_CONNECTION_STRING = '<αποκλειστική δοκιμαστική PostgreSQL>'
npm run test:e2e
```

Ο E2E runner επαληθεύει το model, εφαρμόζει migrations και εκκινεί πραγματικό
Release API και production frontend. Το CI χρησιμοποιεί καθαρή PostgreSQL service.
Τα browser jobs τρέχουν στο επίσημο Playwright `v1.64.1-noble`, που αντιστοιχεί
στη locked npm έκδοση και περιέχει browsers/OS dependencies. Δεν χρειάζεται APT
εγκατάσταση σε κάθε job. Κατά αναβάθμιση Playwright ενημερώνεται και το image.
Το backend job παραμένει στον host runner με Docker για Testcontainers.

Το #81 πρόσθεσε τη μοναδική Stage 5 migration `AddMembershipDeliveryProgress`:
MembershipId, nullable delivery FK/index και backfill D=R. Τα #82–#84 δεν
προσθέτουν schema αλλαγές. Upgrade/downgrade διατηρούν messages/read anchors.

## Περιορισμοί και Stage 6

- Presence/typing/connection registry είναι **single-instance, ephemeral**. Restart
  χάνει την κατάσταση. Δεν υπάρχει Redis/backplane ή durable LastSeen.
- Abrupt disconnect εντοπίζεται από transport timeout (ClientTimeoutInterval 30s,
  KeepAliveInterval 15s), όχι ακριβώς όταν χάνεται φυσικά το δίκτυο. Idle sockets
  δεν κάνουν συνεχή Identity security-stamp/deletion revalidation.
- Event loss μετά το commit είναι αποδεκτό: PostgreSQL/REST ανακτά την κατάσταση.
  Δεν υπάρχει outbox, exactly-once delivery, offline event replay ή send queue.
- Ήδη queued bytes δεν ανακαλούνται. Best-effort membership invalidation μπορεί
  να χαθεί· reconnect ή επόμενο authorized request αποκαθιστά το client view.
- Το R≤D εγγυάται η κοινή transactional υπηρεσία. Απευθείας SQL/legacy writer
  εκτός αυτής δεν αποκτά αυτόματα τις ίδιες monotonic εγγυήσεις.
- Τα timestamps των anchors είναι CreatedAt των messages, όχι ώρα ανθρώπινης
  ανάγνωσης. Read παραμένει client assertion, όχι απόδειξη ότι ο άνθρωπος διάβασε.
- Πρώτη παρατήρηση μεγάλου ιστορικού απαιτεί πολλές REST pages. Receipt events
  προκαλούν coalesced snapshot refresh, άρα υπάρχει πρόσθετο read load.
- Το κοινό conversation lock περιλαμβάνει authorization και enqueue. Πριν το
  scaling απαιτούνται μετρήσεις contention και διατήρηση όλων των race tests.
- Stage 6 πρέπει να ορίσει distributed connection/presence/typing aggregation,
  membership revocation σε όλες τις instances, ordering/epoch semantics και
  λειτουργία μετά από restart/partition. Ένα backplane μόνο του δεν αρκεί.
- Οι προϋπάρχουσες OpenAPI/dev-tool dependency προειδοποιήσεις παραμένουν εκτός
  αυτού του scope. Δεν έγιναν άσχετες αναβαθμίσεις.
- Το #88 (τοπικές AI περιλήψεις) παραμένει ρητά deferred μέχρι την ολοκλήρωση
  του βασικού roadmap. Δεν αποτελεί Stage 5 ή Stage 6 prerequisite.
