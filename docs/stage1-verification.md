# Έλεγχος ολοκλήρωσης Stage 1 — issue #11

Η επαλήθευση της 30/10/2026 καλύπτει τα #3–#10 ως ενιαία ροή. Η υλοποίηση βρίσκεται
στη dev· η μεταφορά στη main γίνεται μόνο με ξεχωριστή εντολή.

## Αποτελέσματα και κάλυψη

| Απαίτηση | Απόδειξη |
| --- | --- |
| Εγγραφή, σύνδεση, me, προφίλ, αναζήτηση, logout | Stage1FlowTests και πραγματικό Chromium E2E |
| Duplicate email/username | RegistrationTests και πραγματικό E2E με 409 |
| Invalid credentials και anonymous protected access | SessionTests, AuthorizationTests και E2E με 401 |
| Απόρριψη μεταβολής ξένου προφίλ | ProfileTests και πραγματικό E2E, με αμετάβλητο προφίλ δεύτερου χρήστη |
| Δημόσια DTO χωρίς Identity secrets | ProfileTests, UserSearchTests, Stage1FlowTests και E2E με ακριβή λίστα πεδίων |
| Καθαρή PostgreSQL και committed migrations | Νέα PostgreSQL 18, EF migration 20261024121917_InitialIdentityProfiles, έλεγχος μηδενικών pending migrations |
| Πραγματική εκκίνηση εφαρμογής | E2E startup μετά από migrations, readiness /health/ready |
| Cookie/CSRF στο React | Πραγματικό login, HttpOnly cookie, me, reload, PUT profile, logout και επόμενο me 401 |
| Σελιδοποίηση | 21 πραγματικοί χρήστες σε 20+1 αποτελέσματα, χωρίς διπλότυπα |
| Responsive εμφάνιση | Έλεγχος screenshots σε 390px και 1280px, χωρίς οριζόντια υπερχείλιση σε 390px |

Τοπικά πέρασαν Release build, 91 backend tests, frontend lint/build, 12 browser
UI tests με mocked API και 3 πραγματικά end-to-end tests χωρίς mocked API.
Ο οπτικός έλεγχος των screenshots επιβεβαίωσε αναγνώσιμα labels/πεδία, ορατή
επιβεβαίωση αποθήκευσης, σωστή αναδίπλωση πλοήγησης και καθαρή λίστα αποτελεσμάτων.
Δεν αποτελεί πλήρη accessibility audit ή έλεγχο όλων των browsers.

Το CI εκτελεί τρία ανεξάρτητα jobs: backend build/tests, frontend lint/build/UI
και πραγματικό Stage 1 E2E με προσωρινό PostgreSQL service. Το τελευταίο χτίζει
και εκκινεί API, εφαρμόζει migrations και χρησιμοποιεί το production frontend build.

## Επανάληψη των ελέγχων

Απαιτούνται .NET SDK από global.json, Node.js 24, Docker και διαθέσιμες θύρες 5080/5173.
Από τη ρίζα του αποθετηρίου:

```powershell
dotnet tool restore
dotnet build RealtimeChat.sln --configuration Release
dotnet test RealtimeChat.sln --configuration Release --no-build
npm --prefix frontend ci
npm --prefix frontend run lint
npm --prefix frontend run build
Push-Location frontend
npx playwright install chromium
Pop-Location
npm --prefix frontend run test:ui
```

Για πραγματικό E2E χρησιμοποίησε **ξεχωριστή προσωρινή βάση**, ποτέ βάση με χρήσιμα
δεδομένα. Οι δοκιμές δημιουργούν λογαριασμούς και αλλάζουν προφίλ. Παράδειγμα:

```powershell
docker run --detach --name chat-stage1-test --publish 127.0.0.1:55439:5432 --env POSTGRES_USER=stage1_test --env POSTGRES_PASSWORD=stage1_test_only --env POSTGRES_DB=stage1_e2e postgres:18
docker exec chat-stage1-test pg_isready -U stage1_test -d stage1_e2e
$env:E2E_CONNECTION_STRING='Host=localhost;Port=55439;Database=stage1_e2e;Username=stage1_test;Password=stage1_test_only'
npm --prefix frontend run test:e2e
docker rm --force --volumes chat-stage1-test
Remove-Item Env:E2E_CONNECTION_STRING
```

Περίμενε το pg_isready να επιβεβαιώσει ετοιμότητα πριν από την εκτέλεση. Τα credentials
του παραδείγματος είναι αποκλειστικά προσωρινά test fixtures. Ο runner απαιτεί ρητό
E2E_CONNECTION_STRING, δεν κάνει reset/drop βάσης και δεν επαναχρησιμοποιεί υπάρχοντα
web servers. Η βάση του CI είναι νέα σε κάθε job. Οι τοπικές επαναλήψεις έχουν μοναδικά
ονόματα χρηστών, αλλά για επαλήθευση καθαρών migrations χρειάζεται καινούρια βάση.

Το scripts/start-e2e-api.mjs κάνει tool restore, Release build, EF database update
και εκκίνηση με --no-launch-profile στο http://localhost:5080. Το Playwright σταματά
τους web servers στο τέλος. Screenshots/traces αποθηκεύονται στο frontend/test-results
και αγνοούνται από το Git. Τα traces αποτυχίας περιέχουν δοκιμαστικά στοιχεία/requests
και δεν πρέπει να δημιουργούνται χρησιμοποιώντας πραγματικούς λογαριασμούς.

## Χειροκίνητη περιήγηση

Με ήδη ρυθμισμένη τοπική βάση, όρισε ConnectionStrings__ChatDatabase στο shell,
εφάρμοσε migrations και εκκίνησε:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet ef database update --project src/Chat.Api
dotnet run --project src/Chat.Api --no-launch-profile --urls http://localhost:5080
```

Σε δεύτερο shell εκτέλεσε npm --prefix frontend run dev -- --host localhost --port 5173
και άνοιξε http://localhost:5173. Το υπάρχον launch profile χρησιμοποιεί 5086,
γι' αυτό οι παραπάνω εντολές ορίζουν ρητά 5080, όπως περιμένει ο frontend client.
Εναλλακτικά ρύθμισε VITE_API_BASE_URL και την CORS allowlist με συνέπεια.

1. Εγγραφή νέου λογαριασμού, μετά σύνδεση και reload.
2. Επεξεργασία προφίλ με έγκυρα και άκυρα στοιχεία· έλεγχος αποθήκευσης/validation.
3. Δημόσια προβολή σε ιδιωτικό παράθυρο, χωρίς email ή άλλα private fields.
4. Αναζήτηση άλλου χρήστη, κενό αποτέλεσμα και επόμενη σελίδα όπου υπάρχει.
5. Logout και άμεση πρόσβαση σε /#/profile: εμφανίζεται σύνδεση.

## Γνωστοί περιορισμοί πριν το Stage 2

- Το backend συνεχίζει να αναφέρει NU1903 για Microsoft.OpenApi 2.0.0
  (GHSA-v5pm-xwqc-g5wc). Το npm audit αναφέρει 5 high findings στις εξαρτήσεις
  ανάπτυξης brace-expansion, nanoid, source-map-js, vite και zod. Δεν διορθώθηκαν
  με αυθαίρετη μαζική αναβάθμιση στο παρόν test issue· απαιτείται ξεχωριστή συντήρηση.
- Το browser E2E ελέγχει Chromium και Development HTTP στο localhost. Τα production
  cookie flags/CORS ελέγχονται στα backend tests, αλλά δεν έχει γίνει deployment
  ή πραγματικός HTTPS έλεγχος με production domain/reverse proxy.
- Παραγωγή: HTTPS, σωστή CORS allowlist και αποθηκευμένα κοινά Data Protection keys.
  Το SameSite Lax απαιτεί συμβατή αρχιτεκτονική origins. Δεν υπάρχει συγχρονισμός
  συνεδρίας μεταξύ tabs πριν από επόμενο API request/reload.
- Logout αφαιρεί το browser cookie· δεν υπάρχει μηχανισμός ανάκλησης κλεμμένου
  cookie ή logout όλων των συσκευών. Δεν προστέθηκε καθολικό rate limiting.
- Αναζήτηση υποσυμβολοσειράς μπορεί να κάνει scan, χωρίς snapshot μεταξύ σελίδων.
  Τα όρια και η μελλοντική αξιολόγηση pg_trgm περιγράφονται στο user-search.md.
- Avatar ως HTTPS metadata link, χωρίς upload/έλεγχο περιεχομένου εικόνας.
- Server validation messages στα πεδία μπορεί να είναι αγγλικά· η κύρια διεπαφή
  και η τεκμηρίωση είναι ελληνικές.
- Password reset, email confirmation UI, social login, conversations, SignalR,
  blocking και Redis παραμένουν εκτός Stage 1.

Με επιτυχή CI και merge του #11 στη dev, τα κριτήρια του parent #2 καλύπτονται
για το Stage 1. Αυτό δεν ισοδυναμεί με πιστοποίηση ετοιμότητας παραγωγής.
