# Stage 5 — Presence, Typing & Receipts: σημασιολογία και συμβόλαια (#77)

Κατάσταση: **πρόταση για review**, όχι υλοποιημένη λειτουργικότητα.
Parent: [#76](https://github.com/ChristosGiann/Realtime-Chat-Platform/issues/76).
Design issue: [#77](https://github.com/ChristosGiann/Realtime-Chat-Platform/issues/77).
Βάση ελέγχου: Stage 4, commit `e8587270697b6077e53d00a016b6461482b95ac5`.
Το παρόν PR αλλάζει μόνο documentation. Τα #78–#84 υλοποιούν τα παρακάτω μετά το review.

## 1. Αμετάβλητες αρχές και πραγματική αφετηρία

PostgreSQL είναι source of truth για messages, memberships και durable progress.
SignalR είναι ephemeral transport: `SendAsync` completion **δεν σημαίνει Delivered**.
Διατηρούνται Identity Guid IDs, HttpOnly cookies, current-user από server principal,
CSRF στα REST mutations, credentialed CORS/Origin validation και το session-expired flow.
Δεν αλλάζουν message idempotency, ιστορικό, UTC timestamps ή cursor ordering.

| Υπάρχον σημείο | Παρατήρηση από το implementation | Συνέπεια για Stage 5 |
| --- | --- | --- |
| [ConversationConnections](../src/Chat.Api/Realtime/ConversationConnections.cs) | Single-instance connection → user → subscriptions, όχι presence API | Μπορεί να υποστηρίξει aggregation, δεν είναι durable state |
| [ChatHub](../src/Chat.Api/Realtime/ChatHub.cs) | Session validation σε connect/invocations, membership πριν από Subscribe | Οι νέες πράξεις επαναχρησιμοποιούν την ίδια server-side authority |
| [ReadStateController](../src/Chat.Api/Messages/ReadStateController.cs) | Monotonic read prefix με `(CreatedAt, Id)` και conversation lock | Το read δεν αντικαθίσταται από UI flags ή SignalR events |
| [ConversationMember](../src/Chat.Api/Conversations/Conversation.cs) | Composite membership key, nullable LastReadMessageId, JoinedAt | Delivery θα ανήκει στην ίδια membership, όχι σε global user/message flag |
| [liveConversation.ts στο Stage 4](https://github.com/ChristosGiann/Realtime-Chat-Platform/blob/e8587270697b6077e53d00a016b6461482b95ac5/frontend/src/messages/liveConversation.ts) | Σύνδεση ανά mounted συνομιλία, retry/REST recovery | Shared authenticated σύνδεση ανά tab/session ανήκει αποκλειστικά στο #78 |
| [MessagingPanel](../frontend/src/messages/MessagingPanel.tsx) | Explicit mark-as-read, REST send, durable-ID deduplication | Η live λήψη δεν σημαίνει αυτόματα Read |

Το σημερινό `LastReadAt` είναι το **CreatedAt του anchor message**, όχι χρόνος
ανάγνωσης από άνθρωπο. Δεν επαναχρησιμοποιείται ως «διαβάστηκε στις…». Αν προκύψει
ανάγκη πραγματικού acknowledgement timestamp, απαιτείται ξεχωριστή απόφαση/πεδίο.
Δεν υπάρχει σήμερα LastDeliveredMessageId, presence/typing API ή receipt broadcast.

Σχετικές εγγυήσεις: [Stage 4](stage4-verification.md),
[authentication](signalr-infrastructure.md), [subscriptions](signalr-subscriptions.md),
[post-commit events](signalr-message-events.md), [revocation](signalr-revocation.md),
[React](signalr-react.md), [reconnect](signalr-reconnect.md),
[durable read state](message-read-state.md).

## 2. Presence: online σημαίνει ενεργή σύνδεση στην instance

`Online(U)` ⇔ υπάρχει τουλάχιστον μία ενεργή authenticated SignalR connection του U
στη συγκεκριμένη API instance, η οποία έχει περάσει το υπάρχον session validation,
δεν έχει λήξει το ticket και δεν έχει παρατηρηθεί invalidation/disconnect.
Δεν σημαίνει ότι ο άνθρωπος κοιτάζει την οθόνη, ότι έχει ανοικτή συγκεκριμένη συνομιλία,
ότι πληκτρολογεί ή ότι έχει λάβει/διαβάσει κάποιο μήνυμα.

| Μετάβαση αριθμού έγκυρων ενεργών connections | Λογικό αποτέλεσμα |
| --- | --- |
| 0 → 1 | Online |
| 1 → 2 ή 2 → 1 | Παραμένει online, χωρίς δεύτερο logical online/offline event |
| 1 → 0 | Offline όταν ο server παρατηρήσει την τελευταία αποσύνδεση |

Η παρουσία μετρά connections, όχι subscriptions. Μέλος που βρίσκεται σε άλλο
screen/συνομιλία παραμένει online μέσω της μελλοντικής shared connection του #78.
Το unfocus/background tab δεν ισοδυναμεί με offline. Κλείσιμο ενός tab δεν κάνει
offline τον χρήστη αν άλλο valid connection παραμένει. Logout κλείνει τη σύνδεση
του tab· δεν προστίθεται υπόσχεση global sign-out ή άμεσης ανάκλησης άλλων tabs.

**Grace policy:** κανένα πρόσθετο application grace period. Μετά από γνωστό
disconnect/expiry/invalidity αφαιρείται αμέσως η connection από την presence μέτρηση.
Abrupt network loss μπορεί να παραμείνει προσωρινά online μέχρι την ανίχνευση
του transport. Το #79 πρέπει να τεκμηριώσει/δοκιμάσει το πραγματικό timeout της
ρύθμισης, όχι να παρουσιάσει offline ως ακριβή στιγμή φυσικής αποσύνδεσης.
Reconnect χωρίς άλλη ενεργή σύνδεση μπορεί να εμφανίσει offline → online.
Process restart χάνει όλο το presence state. Δεν υπάρχει PostgreSQL LastSeen.

**Όριο εγκυρότητας:** το Stage 4 δεν ελέγχει συνεχώς security stamp/deletion ενός
idle WebSocket. Η παρουσία είναι γνώση βάσει της τελευταίας επιτυχούς επικύρωσης,
όχι εγγύηση άμεσης ανίχνευσης διοικητικής αλλαγής. Το #79 πρέπει να αποκλείει κάθε
γνωστή invalid/expired connection και να διατηρεί τους υπάρχοντες ελέγχους.
Συνεχής revalidation/global revocation είναι χωριστό architectural ερώτημα, όχι
σιωπηρά ήδη διαθέσιμη δυνατότητα του registry.

**Conversation projection:** παρατηρητής και εμφανιζόμενος χρήστης πρέπει να είναι
τρέχοντα μέλη της ίδιας συνομιλίας. Live παρατηρητής χρειάζεται authorized subscription.
Ένα authorized snapshot δείχνει online/offline μόνο για αυτά τα μέλη, με pagination
αν χρειάζεται. Δεν απαιτεί ο εμφανιζόμενος χρήστης να είναι subscribed εκεί.
Δεν εκτίθενται global online lists, connection counts/IDs, παρουσία στο search/profile
ή ιστορικό παρουσίας. Ο client που έχασε σύνδεση εμφανίζει «άγνωστο/επανασύνδεση»,
όχι ότι όλοι οι άλλοι είναι offline. Νέο snapshot αντικαθιστά το παλιό ephemeral cache.

## 3. Typing: εξουσιοδοτημένη, αυτοληγόμενη ένδειξη

Η πράξη περιέχει μόνο conversation ID και start/activity ή stop. Actor προκύπτει
από authenticated principal και connection από τον server. Απαιτούνται valid session,
τρέχουσα membership και subscription της συγκεκριμένης connection. Δεν μεταφέρονται
draft text, keystrokes, μήκος κειμένου ή client-supplied user IDs/timestamps ως authority.

Προτεινόμενες σταθερές του contract, προς υλοποίηση/τεστ στο #80:

- Πρώτη δραστηριότητα: άμεσο start. Όσο υπάρχει πραγματική δραστηριότητα, activity
  refresh το πολύ μία φορά ανά **2 δευτερόλεπτα** ανά connection/conversation.
- Client inactivity debounce **2 δευτερόλεπτα**: best-effort stop. Stop επίσης
  μετά από successful send, καθαρισμό composer, blur, αλλαγή συνομιλίας ή unsubscribe.
  Δεν δημιουργείται idle heartbeat που διατηρεί typing χωρίς νέα δραστηριότητα.
- Server lease **5 δευτερόλεπτα** από τελευταία αποδεκτή δραστηριότητα, με monotonic
  clock για deadlines και ελεγχόμενο clock στα tests. Activity περιορίζεται server-side
  σε το πολύ μία αποδεκτή ανανέωση ανά δευτερόλεπτο ανά connection/conversation.
  Απορριφθέν spam δεν ανανεώνει lease. Idempotent stop δεν εμποδίζεται από αυτό το όριο.
- Receiver έχει ανεξάρτητο expiry: κανένα cached typing indicator δεν επιβιώνει πάνω
  από **5 δευτερόλεπτα** χωρίς νεότερη έγκυρη ενημέρωση. Snapshot μεταφέρει μόνο το
  υπολειπόμενο lease, όχι νέο πλήρες lease. Ο χαμένος stop δεν αφήνει μόνιμη ένδειξη.

Ephemeral state ανά `(connection, conversation)`· εμφανιζόμενο typing ανά
`(conversation, user)` είναι OR των μη ληγμένων leases των tabs. Stop ενός tab
δεν σταματά άλλο ενεργό tab. Η UI εμφανίζει κάθε user μία φορά και κρύβει τον δικό
της user σε όλα τα tabs. Disconnect αφαιρεί τα leases της connection. Unsubscribe
και revocation αφαιρούν τα σχετικά leases, χωρίς να επηρεάζουν άλλες συνομιλίες.

Το #80 οφείλει να διατηρήσει τη σειρά των ephemeral transitions με server-owned
revision/epoch ή ισοδύναμη σειριοποίηση: παλαιό activity δεν αναιρεί νεότερο stop.
Τα scopes ενός παλιού connection/process epoch απορρίπτονται μετά από reconnect.
Καθυστερημένο έγκυρο event μπορεί να προκαλέσει σύντομη stale ένδειξη, περιορισμένη
από το receiver expiry· δεν υπόσχεται ακριβή παρακολούθηση ανθρώπινης δραστηριότητας.
Ο τρόπος wire encoding παραμένει στο #80, όχι η υποχρέωση λήξης/ordering.

## 4. Sent / Delivered / Read και cumulative progress

Για μήνυμα M, `P(M) = (CreatedAt UTC, Id)` με **PostgreSQL ordering**, όχι σειρά
άφιξης events, UUID μόνο του ή JavaScript Date που χάνει microseconds.
Null anchor σημαίνει καμία acknowledged πρόοδο και βρίσκεται πριν από κάθε μήνυμα.

| Κατάσταση | Ακριβές νόημα | Δεν αρκεί |
| --- | --- | --- |
| Sent(M) | Το message transaction έχει committed στην PostgreSQL | Optimistic UI, HTTP request που ξεκίνησε |
| Delivered(U,M) | Η τρέχουσα membership του recipient U έχει committed delivery anchor D με P(D) ≥ P(M), μετά από client acknowledgement ή explicit read-prefix acknowledgement | Online, group membership, SendAsync completion, HTTP GET που ο server απλώς έστειλε |
| Read(U,M) | Η membership του U έχει committed read anchor R με P(R) ≥ P(M) | Λήψη event, background tab, απόδοση μηνύματος χωρίς read action |

Invariant: **Read ⇒ Delivered ⇒ Sent**, ανά τρέχουσα membership.
Επιπλέον, `D_new ≥ D_old` και `R_new ≥ R_old` εντός της ίδιας membership lifetime.
Πρόκειται για acknowledgements του client, όχι απόδειξη φυσικής/ανθρώπινης ανάγνωσης
ή εγγύηση ότι ο client αποθήκευσε τα δεδομένα μόνιμα στη συσκευή.

### Delivery acknowledgement και κενά

Ο recipient client επιβεβαιώνει durable observation αφού αποδεχθεί το μήνυμα στο
τοπικό μοντέλο από `MessageCreated` ή επιτυχή REST ανάκτηση/resync. Το ACK πρέπει να
committed πριν ο sender δει Delivered. Pending ACK, χαμένη ACK απάντηση ή αποτυχία
transport δεν επιτρέπουν optimistic Delivered στον sender· επιτρέπεται safe retry.

Το anchor είναι **cumulative prefix assertion**, όχι sparse «είδα μόνο το M».
Αυτό είναι συνειδητός περιορισμός του μοντέλου χωρίς per-message receipt rows.
Παράδειγμα: αν D=M1 και φτάσει M3 ενώ λείπει M2, ο client δεν στέλνει αυτόματα ACK M3.
Κλείνει το κενό με REST cursors και μετά επιβεβαιώνει το prefix έως M3.
Χωρίς προηγούμενο anchor, αυτό μπορεί να απαιτεί traversal μέχρι την αρχή, όχι
μόνο την τελευταία σελίδα. Άφιξη event μόνο της τελευταίας σελίδας δεν αποδεικνύει
πλήρες observation. Concurrent/out-of-order live events συγχωνεύονται με durable IDs.

Το υπάρχον explicit mark-as-read είναι διαφορετική, ρητή δήλωση χρήστη για ολόκληρο
prefix, ακόμη και για παλαιότερα μη προβληθέντα μηνύματα. Η read πράξη περιέχει και
cumulative delivery acknowledgement για αυτό το prefix. Δεν αλλάζει σε αυτόματο
read κατά το fetch/receipt. Έτσι διατηρείται η σημερινή λειτουργία χωρίς να αποδίδεται
ψευδής ισχυρισμός ότι μετρήθηκε η προβολή κάθε μηνύματος.

### Persistence contract του μελλοντικού #81

- Nullable `LastDeliveredMessageId` ή ισοδύναμος anchor στη ConversationMember,
  με same-conversation FK και delete behavior αντίστοιχο του read anchor.
- Μόνο ο current user προχωρά το δικό του progress. Έγκυρο target πρέπει να είναι
  committed μήνυμα της ίδιας συνομιλίας. Server validation, conversation row lock,
  UTC/ordering και CSRF για REST mutation, όχι client-supplied actor/clock.
- Delivery ACK X: `D := max(D, X)`, χωρίς αλλαγή R ή unread count.
- Read ACK X: **στην ίδια transaction** `R := max(R, X)` και `D := max(D, R)`.
  Ακόμη και παλαιότερο/no-op read request δεν επιτρέπεται να αφήνει `D < R`.
  Το send δεν μετακινεί read/delivery anchors και ο sender δεν μετρά ως δικός του recipient.
- Ίδια/παλαιότερα acknowledgements είναι idempotent και επιστρέφουν την τελική
  persisted κατάσταση. Δύο tabs προχωρούν μία κοινή membership, όχι δύο receipts.
- Migration του #81 πρέπει να κάνει backfill `D := R` όπου υπάρχει R, αλλιώς null,
  ώστε τα προϋπάρχοντα read δεδομένα να ικανοποιούν αμέσως το invariant.
  Δεν βαφτίζει delivered όλα τα υπάρχοντα messages/μέλη χωρίς read evidence.
- Το same-conversation FK μπορεί να επιβληθεί στη βάση. Η σύγκριση anchor order
  που απαιτεί Messages rows δεν είναι απλό CHECK στο membership: χρειάζεται
  transaction/service invariant και πραγματικά PostgreSQL concurrency tests.

Δεν υλοποιείται εδώ πεδίο, migration, endpoint ή αλλαγή του υπάρχοντος read API.

## 5. Direct και Group receipts

Στη Direct, για εξερχόμενο committed μήνυμα του A παρατηρείται μόνο η membership
του B: Read αν R_B καλύπτει M, αλλιώς Delivered αν D_B καλύπτει M, αλλιώς Sent.
Τα tabs/devices του B δεν αυξάνουν τον αριθμό recipients. Λείπον/μη ανακτημένο
receipt snapshot σημαίνει «άγνωστο μέχρι resync», όχι απόδειξη μη παράδοσης.
Δεν εμφανίζεται receipt του sender ως επιβεβαίωση από τον άλλον χρήστη.

Στη Group, backend/domain/API κρατούν **per-member anchors**. Δεν υπάρχει
`message.IsRead`, global delivered boolean ή πρόοδος που γράφει ένας χρήστης για όλους.
Το UI μπορεί αργότερα να παράγει aggregates από authorized snapshot:

`E(M) = τρέχοντα μέλη της συνομιλίας εκτός του SenderId του M`.
`N = |E(M)|`, `DeliveredCount = count(U ∈ E: D_U ≥ M)`,
`ReadCount = count(U ∈ E: R_U ≥ M)`.
Ισχύει `0 ≤ ReadCount ≤ DeliveredCount ≤ N` στο ίδιο συνεπές snapshot.

**Επιλογή membership cohort:** τρέχοντα memberships, όχι ιστορικοί recipients
τη στιγμή αποστολής. Άρα αφαίρεση αλλάζει denominator και νέα μέλη προστίθενται
με null progress, ακόμη και για παλιά μηνύματα που επιτρέπεται να διαβάσουν.
Παράδειγμα: 3/4 μπορεί να γίνει 2/3 μετά από removal ενός delivered μέλους ή 3/5
μετά από νέο join. Αυτό δεν είναι regression κάποιου anchor.
Η UI πρέπει να το ονομάζει π.χ. «Παραδόθηκε σε 3/4 τρέχοντα άλλα μέλη».
Για N=0 δεν εμφανίζεται «όλοι διάβασαν» από κενό σύνολο· δείχνει «χωρίς άλλους
τρέχοντες παραλήπτες»/παραλείπει aggregate. Counts/denominator δεν αναμιγνύονται
από διαφορετικές membership versions ή ελλιπείς σελίδες snapshot.

Ιστορικό audience-at-send, διατήρηση receipts πρώην μελών και per-message audience
tables είναι ξεχωριστή μελλοντική απαίτηση. Δεν υπονοούνται από το Stage 5.

## 6. Membership lifecycle, privacy και races

| Συμβάν | Ephemeral αποτέλεσμα | Durable αποτέλεσμα |
| --- | --- | --- |
| Subscribe | Authorized presence snapshot, φρέσκο typing context | Καμία αυτόματη μεταβολή anchors |
| Unsubscribe / switch | Παύση έκθεσης προς τη connection, clear typing lease | Anchors διατηρούνται· shared connection μπορεί να παραμένει online |
| Remove / leave | Ανάκληση όλων των tabs στο conversation, clear typing/observation cache | Διαγραφή membership μαζί με R και D |
| Rejoin | Καμία αυτόματη επαναφορά subscriptions/typing· νέο authorized subscribe | Νέα membership, R=null και D=null, όλο το ιστορικό ορατό όπως ήδη ισχύει |
| Disconnect / API restart | Connections/presence/typing χάνονται | Messages και progress παραμένουν |

Removal αφορά και τον **παρατηρητή** και το **υποκείμενο** των πληροφοριών:
το πρώην μέλος δεν λαμβάνει μελλοντικά protected updates και τα τρέχοντα μέλη
δεν συνεχίζουν να βλέπουν presence/typing/receipt entries του ως τρέχοντος μέλους.
Μπορεί να παραμένει online σε άλλη κοινή συνομιλία· δεν εκπέμπεται global offline.
Read/delivery snapshot επιτρέπεται μόνο σε current authorized members και αφορά
current memberships. User search/public profile δεν αποκτούν online/read πληροφορίες.

Οι μελλοντικοί publishers επανελέγχουν το current authorized context και
συντονίζονται με το υπάρχον conversation lock/revocation. Για receipts ισχύει
commit → publish, όχι το αντίστροφο. Failed/rolled-back writes δεν εκπέμπουν
receipt advance. No-op ACK δεν παράγει ψευδή νέα πρόοδο. Read που προχωρά και D
πρέπει να ανακοινώνει συνεπές ζεύγος anchors ώστε κανένα snapshot/UI να δείχνει R>D.
Failure publish μετά το commit δεν αναιρεί επιτυχή durable πρόοδο.

Όπως στο Stage 4, ήδη queued bytes δεν ανακαλούνται. Removal/rejoin πρέπει να
ακυρώνει τα παλιά client caches και in-flight receipt application: **δεν κάνουμε
max παλιού και νέου membership progress**. Το #82 χρειάζεται server-owned
membership-generation/version discriminator ή ισοδύναμη snapshot gating στρατηγική
για να απορρίπτει stale events μετά από rejoin. Το υπάρχον composite key δεν
διακρίνει membership incarnations· το JoinedAt δεν θεωρείται αυτόματα μοναδικό token.
Η τελική αναπαράσταση πρέπει να συμφωνηθεί στα #81/#82 πριν την υλοποίηση wire contracts.

Current-user mutations ελέγχουν την membership που υπάρχει **όταν εκτελούνται**.
Το υπάρχον read request `{messageId}` δεν κωδικοποιεί membership incarnation· δεν
υποσχόμαστε ανίχνευση παλιού HTTP request που έφτασε μετά από removal και rejoin.
Ο νέος client ακυρώνει παλιές προσπάθειες/queues και δεν επαναχρησιμοποιεί παλιά ACKs
σε νέο membership context. Αν απαιτηθεί ισχυρή server-side precondition και για
legacy read requests, χρειάζεται ρητός σχεδιασμός συμβατότητας στα #81/#82.

## 7. Reconnect, snapshots και συμβόλαια πληροφορίας

Η shared connection του #78 ανήκει σε authenticated tab/session, όχι στο active
MessagingPanel. Logout/account change τη σταματά και καθαρίζει state. Αποτυχημένη
subscription σε μία συνομιλία δεν πρέπει να ακυρώνει άλλες εξουσιοδοτημένες.
Αυτό είναι απαίτηση σχεδιασμού, όχι refactor σε αυτό το PR.

Μετά από reconnect: revalidate session → authorized resubscribe → φρέσκα
ephemeral snapshots → REST message recovery και durable receipt snapshot.
Διατηρείται το υπάρχον gap recovery, με πλήρες cumulative observation πριν από νέο
automatic delivery ACK. Δεν επαναπαίζονται παλιά typing starts. Νέα δραστηριότητα
απαιτεί νέο lease. Receipt events που χάθηκαν ανακατασκευάζονται από PostgreSQL.

Το #82 οφείλει να παρέχει REST snapshot με per-member R/D και τις order coordinates
των anchors ώστε να συγκρίνονται ακόμη κι αν το μήνυμα δεν είναι στην τοπική σελίδα.
Duplicate/out-of-order events εντός ίδιας membership δεν μειώνουν progress.
Snapshot/membership version και client synchronization generation αποτρέπουν εφαρμογή
αποτελεσμάτων από παλιό conversation/account/reconnect context. API failure δεν
ισοδυναμεί με μηδενικό receipt state ή με επιτυχή ανάκτηση.

| Λογικό συμβόλαιο (όχι νέα endpoints σε αυτό το PR) | Ελάχιστη πληροφορία / εξουσιοδότηση |
| --- | --- |
| Presence snapshot/update | Conversation, current member user ID, online flag, ephemeral ordering/snapshot context· μόνο authorized observers |
| Typing activity/stop | Conversation + activity/stop· actor/connection server-derived |
| Typing snapshot/update | Conversation, actor ID, typing flag/υπολειπόμενο lease, ordering context· χωρίς draft/connection IDs |
| Delivery ACK | Conversation + durable target message ID για current user, όχι actor ID· cumulative assertion |
| Read ACK | Υπάρχον explicit prefix contract, με atomic delivery floor στο #81 |
| Receipt snapshot/update | Conversation, current member ID, R/D anchor IDs και durable ordering, membership/snapshot context· όχι Identity entities/emails |

Αυτά ορίζουν νόημα και υποχρεώσεις. Ακριβή route/event names, JSON encoding,
pagination και version representation οριστικοποιούνται στα αντίστοιχα implementation
issues με tests· δεν αποτελούν διαθέσιμα APIs του Stage 4.

## 8. Ελέγξιμα σενάρια και κατανομή εργασιών

| Σενάριο αποδοχής | Αναμενόμενο | Issue |
| --- | --- | --- |
| Login χωρίς mounted conversation, αλλαγή οθόνης, logout | Μία shared connection ανά tab/session, cleanup χωρίς account leakage | #78 |
| Δύο tabs, κλείσιμο πρώτου/τελευταίου, abrupt loss | Ένας online user, offline μόνο στο τελευταίο observed disconnect, χωρίς LastSeen | #79 |
| Invalid/expired connection, outsider query, removed member | Καμία νέα protected παρουσία για μη εξουσιοδοτημένο context | #79 |
| Activity spam, χαμένο stop, δύο tabs, disconnect/revoke | Throttle, bounded expiry, μία ένδειξη user, κανένα draft content | #80 |
| ACK M3 πριν ACK M2 από δύο tabs, ξένο message ID | Monotonic D, cross-conversation rejection, independent memberships | #81 |
| Read με D=null/παλιότερο, migration υπάρχοντος R | Atomic D≥R και σωστό backfill χωρίς αλλαγή unread semantics | #81 |
| Αποτυχία commit, no-op ACK, failed publisher | Κανένα false event· durable success αντέχει σε transport failure | #82 |
| Stale receipt μετά από rejoin, out-of-order events | Νέα membership δεν κληρονομεί παλιό progress, REST αποκαθιστά αλήθεια | #81/#82 |
| Direct και Group με join/remove/N=0 | Σωστό recipient state και current-membership aggregates | #83 |
| M1 γνωστό, M3 live, M2 λείπει· gap > page | Κάλυψη κενού από REST πριν από cumulative delivery ACK, όχι false Delivered | #83/#84 |
| Offline receipts/read advance → reconnect | REST snapshot συγκλίνει χωρίς replay ή διπλή πρόοδο | #82–#84 |
| Πλήρης ροή με πραγματικούς browsers/PostgreSQL | Presence, typing, receipts και όλα τα Stage 1–4 regressions | #84 |

Το #77 παραδίδει μόνο το design και README link. Δεν προσθέτει tests που απλώς
ελέγχουν στατικές προτάσεις· τα παραπάνω είναι απαιτήσεις των μελλοντικών behavior tests.

## 9. Όρια και ανοιχτές αποφάσεις υλοποίησης

Οι ορισμοί online, typing lease, Sent/Delivered/Read, cumulative prefix,
current-membership group cohort και null-on-rejoin είναι οι προτεινόμενες αποφάσεις
του παρόντος design για review. Δεν μεταβάλλεται runtime συμπεριφορά μέχρι τα επόμενα issues.

Πριν κλείσουν τα αντίστοιχα implementation issues πρέπει να απαντηθούν:

1. #79: ακριβές transport disconnect timeout και αν ζητείται επιπλέον περιοδική
   Identity revalidation. Η άμεση global session revocation δεν τεκμαίρεται από online.
2. #80: bounded registry cleanup και ephemeral revision/epoch encoding, με tests
   στα 2s/5s όρια· καμία durable typing αποθήκευση.
3. #81/#82: membership-incarnation/snapshot token, συμβατότητα legacy read requests,
   REST snapshot pagination και συμβόλαιο συνεπούς ζεύγους R/D. Η ανάγκη αποφυγής
   stale progress είναι δεσμευτική· η αναπαράσταση παραμένει ανοικτή.

Stage 6: Redis/backplane, multi-instance presence aggregation και distributed
revocation/ordering. Backplane μόνο του δεν λύνει cross-instance authorization races.
Δεν υποσχόμαστε cross-instance/device presence guarantees. Παραμένουν εκτός scope
durable LastSeen, push notifications, offline send queue, exactly-once delivery,
per-message receipt/audience audit, attachments και message edit/delete.
