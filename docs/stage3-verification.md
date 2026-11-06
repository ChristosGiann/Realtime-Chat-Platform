# Stage 3 — Durable messaging και παράδοση στο Stage 4

Τα #40–#46 καλύπτουν Message persistence, authenticated send, client idempotency,
keyset history, membership read progress/unread και React REST UI. Η PostgreSQL 18
παραμένει source of truth. Δεν υπάρχει SignalR ή Redis σε αυτό το stage.

## Συμβόλαια

- [Persistence και διατήρηση ιστορικού](message-persistence.md)
- [Αποστολή και authorization](message-creation.md)
- [Κλειδιά και ασφαλή retries](message-idempotency.md)
- [Προστατευμένα history cursors](message-history.md)
- [Read state και unread](message-read-state.md)
- [React εμπειρία και περιορισμοί](rest-messaging-ui.md)

Μηνύματα επιβιώνουν από reload και leave/removal. Το sender προκύπτει από το
session. Αποστολή επιβεβαιώνεται μόνο μετά το database commit. Unique client key
προστατεύει από duplicates και ασύμβατο retry δίνει 409. Η σειρά είναι UTC
CreatedAt/Id, όχι global commit sequence. Τα read positions είναι prefix αυτής
της σειράς, με ανεξάρτητη πρόοδο ανά membership και χωρίς οπισθοδρόμηση.

## Έλεγχοι και εκτέλεση

Με Docker Desktop ενεργό:

```powershell
dotnet build RealtimeChat.sln --configuration Release
dotnet test RealtimeChat.sln --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project src/Chat.Api --configuration Release --no-build
cd frontend
npm ci
npm run lint
npm run build
npx playwright install chromium
npm run test:ui
$env:E2E_CONNECTION_STRING = 'Host=localhost;Port=55436;Database=stage3_e2e;Username=stage3_test;Password=stage3_test_only'
npm run test:e2e
```

Το E2E connection πρέπει να δείχνει σε αποκλειστική αναλώσιμη PostgreSQL, ποτέ
production. Ο runner κάνει build, έλεγχο model και εφαρμόζει όλες τις committed
migrations πριν ξεκινήσει το πραγματικό API και το production frontend preview.
Το CI χρησιμοποιεί καθαρό PostgreSQL service ανά run. Τα Testcontainers tests
ελέγχουν επίσης clean initialization και upgrade/downgrade με παλιά δεδομένα.

Το integrated backend flow ενώνει send/retry, history, ανεξάρτητα unread και
removal/rejoin, με τελικές επαληθεύσεις απευθείας στη βάση. Το stage3.spec.ts
χρησιμοποιεί πραγματικούς χρήστες και ξεχωριστά browser contexts, χωρίς API mocks:
UI send → reload → persisted history, αμφίδρομη επικοινωνία, concurrent retries,
API/UI pagination, monotonic read updates, outsider rejection και group revocation.
Τα Stage 1/2 E2E και όλα τα προηγούμενα backend/browser regressions παραμένουν ενεργά.

## Όρια και απαιτήσεις για Stage 4

- Χειροκίνητη ανανέωση, χωρίς polling/live delivery, presence, typing ή receipts.
- Pending retry κρατιέται μόνο στο ανοιχτό component, όχι σε offline queue.
- Cursors απαιτούν σταθερά/shared Data Protection keys σε πολλαπλά instances.
- Send/read/membership mutations χρησιμοποιούν κοινό conversation row lock.
  Το GET read-state επίσης κλειδώνει για συνεπή εικόνα: χρειάζεται αξιολόγηση φόρτου
  πριν από μεγαλύτερη κλίμακα, όχι αφαίρεση του lock χωρίς αντίστοιχους ελέγχους.
- Νέα/επανενταγμένα μέλη βλέπουν όλο το ιστορικό και ξεκινούν με null read anchor.
- Δεν υποστηρίζονται edit/delete/attachments. FK RESTRICT διατηρεί sender,
  conversation και read anchors μέχρι να οριστεί πολιτική διαγραφής.
- Backdated imports ή clock regression απαιτούν νέα πολιτική για unread/order.
- Το Stage 4 πρέπει να εκπέμπει συμβάντα μόνο για committed δεδομένα, να διατηρεί
  idempotency και authorization, να χειρίζεται reconnect με durable history και
  να μην θεωρεί το SignalR ή τη μνήμη server πηγή αλήθειας.

Παραμένει η προϋπάρχουσα NU1903 προειδοποίηση Microsoft.OpenApi 2.0.0· δεν έγινε
αναβάθμιση εξαρτήσεων εκτός scope αυτών των issues. Η main ενημερώνεται μόνο με
ξεχωριστή εντολή, ανεξάρτητα από την ολοκλήρωση του Stage 3 στο dev.
