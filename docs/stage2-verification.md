# Stage 2 — επαλήθευση και παράδοση στο Stage 3

Το Stage 2 καλύπτει συνομιλίες, συμμετοχές, εξουσιοδότηση και browser flows πάνω
στο υπάρχον Identity/Profile/session architecture. Δεν περιλαμβάνει messaging.
Το parent #24 μπορεί να κλείσει μετά το merge του #32 και επιτυχή CI· το άνοιγμα
του PR από μόνο του δεν ολοκληρώνει την ενσωμάτωση.

## Χάρτης υλοποίησης

| Issue | Συμπεριφορά / τεκμηρίωση |
| --- | --- |
| #25 | [Conversation/Member, EF, constraints και migration](conversation-persistence.md) |
| #26 | [Direct creation και reuse canonical pair](direct-conversations.md) |
| #27 | [Group creation με Owner και αρχικά μέλη](group-conversations.md) |
| #28 | [Group add/remove/leave και transactions](conversation-membership.md) |
| #29 | [Κοινή εξουσιοδότηση και προστατευμένα queries](conversation-authorization.md) |
| #30 | [List/details, δημόσια profiles και pagination](conversation-queries.md) |
| #31 | [React browsing/creation/membership flows](conversations-frontend.md) |
| #32 | Πραγματικό browser/API/PostgreSQL E2E και συνολικός έλεγχος |

## Αναπαραγωγή ελέγχων

Χρειάζονται .NET SDK του global.json, Node.js 24, Docker με Linux containers και
Chromium του Playwright. Τα backend tests χρησιμοποιούν δικές τους PostgreSQL 18
Testcontainers databases. Τα real E2E απαιτούν ξεχωριστή αναλώσιμη βάση:

```powershell
dotnet tool restore
dotnet restore RealtimeChat.sln
dotnet build RealtimeChat.sln --configuration Release --no-restore
dotnet test RealtimeChat.sln --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project src/Chat.Api --configuration Release --no-build
cd frontend
npm ci
npm run lint
npm run build
npx playwright install chromium
npm run test:ui
# Ορίστε E2E_CONNECTION_STRING προς αποκλειστικά προσωρινή PostgreSQL 18.
npm run test:e2e
```

Ο E2E runner εκκινεί πραγματικό API και production frontend preview. Πριν το API
εκτελεί tool restore, Release build, has-pending-model-changes και database update.
Το CI δημιουργεί καθαρό PostgreSQL service ανά job, άρα η αρχικοποίηση ελέγχεται
από όλες τις committed migrations. Δεν γίνεται αυτόματο migrate στην κανονική
εκκίνηση του προϊόντος και δεν χρησιμοποιείται development/production βάση.

Τα ports 5080/5173 πρέπει να είναι διαθέσιμα. Ο runner δεν επαναχρησιμοποιεί ήδη
ενεργό API/frontend. Τα E2E δημιουργούν μοναδικούς λογαριασμούς και δεν διαγράφουν
υπάρχοντα δεδομένα. Η προσωρινή βάση αφαιρείται από τον caller μετά το τέλος.

## Κάλυψη πραγματικών ροών

- Stage 1 registration/login/profile/search/logout και αρνητικά σενάρια παραμένουν.
- Direct από UI με user search, ασφαλή details, πρόσβαση και των δύο χρηστών,
  12 παράλληλα αμφίδρομα reuse requests με ίδιο ID, ακριβώς δύο memberships.
- Group από UI με Owner/αρχικό Member, Owner add/remove από UI, Member leave από UI.
- Outsider read/mutation 404, Member owner-only mutation 403, anonymous 401,
  missing CSRF 400, Owner leave/removal και Direct mutation 409.
- List μόνο των συμμετοχών, άμεση ανάκληση read/list μετά από removal/leave,
  πραγματική pagination 21 συνομιλιών και session-expiry που αφαιρεί protected UI.
- Desktop/mobile screenshots και έλεγχος οριζόντιας υπερχείλισης.

Τα PostgreSQL backend tests καλύπτουν επιπλέον schema/FKs/indexes/UTC, migration
upgrade/downgrade, concurrent creation/membership changes και forced rollback.
Τα mocked UI tests ελέγχουν loading/error/retry/validation χωρίς εξάρτηση από
network timing. Δεν αντικαθιστούν τα real E2E.

## Γνωστοί περιορισμοί και συμβόλαια Stage 3

Τοπική επαλήθευση 2 Νοεμβρίου 2026: Release build, 190 backend tests, frontend
lint/build, 22 UI browser tests και 6 real E2E tests (3 Stage 1 + 3 Stage 2)
πέρασαν. Δεν υπάρχουν pending model changes. Το GitHub CI επαναλαμβάνει τους
ελέγχους για κάθε PR προς dev/main.

- PostgreSQL είναι source of truth. Δεν υπάρχουν Redis, SignalR, message tables,
  history, composer, unread counts, last-message preview ή presence.
- Membership mutations αποκτούν conversation row lock πριν το authorization και
  κρατούν transaction μέχρι commit. Μελλοντικές συναφείς μεταβολές πρέπει να
  ακολουθούν το ίδιο πρωτόκολλο και να επανελέγχουν δικαιώματα.
- ReadableConversations επιβάλλει membership στο ίδιο SQL statement. Το Read
  permission δεν αποτελεί αυτόματα SendMessage permission: το Stage 3 πρέπει
  να ορίσει το δικό του durable write/authorization contract.
- Owner δεν αποχωρεί/αφαιρείται. Δεν υπάρχουν ownership transfer, invitations,
  blocking, soft delete ή γενική πολιτική διαγραφής λογαριασμού με memberships.
- Group creation δεν είναι idempotent· ίδια σύνθεση/όνομα μπορεί να δώσει νέο group.
  Direct pair είναι μοναδικό στη βάση. Τα Group names δεν είναι unique.
- Offset pagination δεν εγγυάται snapshot μεταξύ σελίδων υπό concurrent αλλαγές.
  UI έχει manual refresh και refresh μετά από δικά του mutations, όχι realtime.
- Direct list έχει γενικό τίτλο και profile που λείπει εμφανίζεται ως user ID.
- Database row constraints δεν εγγυώνται όλους τους cross-row domain κανόνες για
  αυθαίρετα SQL writes. Οι υποστηριζόμενες API ροές τους επιβάλλουν transactional.
- Παραμένει η προϋπάρχουσα NU1903 για Microsoft.OpenApi 2.0.0. Δεν έγινε αναβάθμιση
  εξάρτησης στο scope αυτού του stage integration.

Η main ενημερώνεται μόνο με ξεχωριστή εντολή. Το #32 δεν κάνει merge ή deployment
από μόνο του και δεν κλείνει το parent πριν ενσωματωθεί.
