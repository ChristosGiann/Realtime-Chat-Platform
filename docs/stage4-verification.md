# Stage 4 — επαλήθευση και παράδοση

Το Stage 4 (#59, #60–#66) προσθέτει authenticated SignalR, εξουσιοδοτημένα
conversation groups, post-commit MessageCreated, ανάκληση σε όλα τα tabs,
live React UI και επανασύνδεση με REST recovery. PostgreSQL παραμένει η πηγή αλήθειας.

## Συμβόλαια

- `/hubs/chat`: υπάρχον cookie/session, έλεγχοι Identity, ακριβή allowed origins,
  credentials και ξεχωριστός έλεγχος WebSocket Origin. Οι mutations διατηρούν CSRF.
- `Subscribe(Guid)` / `Unsubscribe(Guid)`: actor από τον server principal,
  ποτέ από client-supplied user ID. Membership ελέγχεται στην PostgreSQL.
- `MessageCreated`: ασφαλές REST MessageResponse με durable id/clientMessageId,
  conversationId, senderId, body και UTC createdAt. Πρώτα commit, μετά event.
- Safe retry 200 δεν εκπέμπει δεύτερο event. Rejected/rolled-back writes δεν
  εκπέμπουν τίποτα. Transport failure μετά το commit δεν αναιρεί το REST 201.
- Remove/leave αφαιρεί όλα τα tabs υπό κοινό conversation row lock. Ήδη queued
  bytes μπορεί να φτάσουν μετά την αφαίρεση· μεταγενέστερο broadcast δεν την παρακάμπτει.
- Reconnect επανελέγχει πρόσβαση, κάνει νέο Subscribe και διαβάζει REST pages
  μέχρι το προηγούμενο durable anchor. 401 επιστρέφει στο login, 403/404 σταματά
  την ανάκτηση και καθαρίζει προστατευμένα δεδομένα. Rejoin βλέπει το durable
  ιστορικό σύμφωνα με την πολιτική Stage 2, αλλά απαιτεί νέα subscription.

## Κάλυψη

| Έλεγχος | Απόδειξη |
| --- | --- |
| Cookie/anonymous/expired/deleted/stamp/lockout, Origin/CORS | SignalRInfrastructureTests και πραγματικό browser handshake |
| Group isolation, actor spoofing, subscribe/mutation race | SignalRSubscriptionTests με πραγματική PostgreSQL |
| Commit visibility, rollback, idempotency, transport failure | SignalRBroadcastTests, ανεξάρτητο DbContext και barriers |
| Remove/leave, δύο tabs, racing sends, rejoin | SignalRRevocationTests και stage4.spec.ts |
| Αποτυχία unsubscribe δεν κρύβει σύνδεση από revocation | SignalRFailedUnsubscribeRemainsRevocable με ελεγχόμενο group transport failure |
| Live A↔B, sender REST+event, rejected writes, conversation switch | stage4.spec.ts με πραγματικές WebSocket frames |
| Offline → 35 νέα μηνύματα → REST pages → live reply | reconnect.spec.ts χωρίς API mocks |
| Reconnect μετά από membership/session loss | stage4.spec.ts με πραγματικά cookies και membership mutations |
| Initial retry, deduplication, text safety, error UI | messages.spec.ts με ελεγχόμενο transport |
| Προηγούμενη λειτουργικότητα | Όλα τα backend και Stage 1–3 UI/E2E regressions |

## Εκτέλεση

Απαιτούνται Docker (PostgreSQL/Testcontainers), το SDK του global.json, Node.js 24
και Chromium (`npx playwright install chromium` από frontend).

```powershell
dotnet build RealtimeChat.sln --configuration Release
dotnet test RealtimeChat.sln --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project src/Chat.Api --configuration Release --no-build
cd frontend
npm ci
npm run lint
npm run build
npm run test:ui
$env:E2E_CONNECTION_STRING = '<connection string απομονωμένης αναλώσιμης PostgreSQL>'
npm run test:e2e
```

Ο E2E runner ελέγχει pending model changes, εφαρμόζει migrations και ξεκινά
πραγματικό API/production frontend. Το CI εκτελεί τις ίδιες κατηγορίες ελέγχων
με καθαρή PostgreSQL 18. Δεν απαιτείται νέο EF model/migration για το Stage 4.

## Όρια και παράδοση στα επόμενα stages

- Μία API instance: registry/group membership είναι προσωρινά. Redis/backplane
  μόνο του δεν αρκεί για distributed revocation· το Stage 6 πρέπει να συντονίζει
  όλες τις συνδέσεις/instances και να διατηρεί τη σειρά authorization/broadcast.
- Δεν υπάρχει outbox, delivery acknowledgment ή exactly-once/offline replay.
  Crash μετά το commit ή αποτυχία transport μπορεί να χάσει event. Ανάκτηση από
  REST με reconnect ή refresh, όχι ανάκτηση από server memory.
- REST history order είναι CreatedAt/Id, όχι σειρά άφιξης events. Μεγάλο gap
  απαιτεί πολλές σελίδες. Backdated imports/clock regression δεν υποστηρίζονται.
- Το conversation lock περιλαμβάνει group removal/broadcast enqueue. Χρειάζεται
  μέτρηση contention πριν από scaling και διατήρηση των race tests σε αλλαγές.
- Stage 5: presence/typing/read-delivery receipts παραμένουν εκτός scope.
  Το durable read state υπάρχει ήδη, αλλά δεν υπάρχουν receipt events.
- Δεν υπάρχουν attachments, edit/delete, offline send queue ή push notifications.
- Ισχύουν οι απαιτήσεις patched runtime, reverse proxy/WebSocket, cookies και
  Data Protection του [infrastructure guide](signalr-infrastructure.md).
  Οι προϋπάρχουσες OpenAPI/dev-tool dependency προειδοποιήσεις δεν διορθώνονται
  μέσω άσχετης αναβάθμισης στο Stage 4.

Η ολοκλήρωση στο dev δεν ενημερώνει αυτόματα τη main.
