# Distributed connection registry και presence — #99

Το έγγραφο καταγράφει το scope του #99. Η τελική Stage 6 κατάσταση περιγράφεται στο [production handoff](stage6-production-handoff.md).

Όταν `Redis:Enabled=true` και `Redis:BackplaneEnabled=true`, το hub χρησιμοποιεί
Redis για προσωρινό connection ownership και presence σε περισσότερες από μία API
instances. Το προηγούμενο local registry παραμένει για single-instance λειτουργία.
Η PostgreSQL εξακολουθεί να καθορίζει identities, memberships, μηνύματα και
receipts. Δεν υπάρχει νέα migration ή durable Redis state.

## Registry και όρια

Κάθε hub connection παίρνει server-generated connection ID, instance ID και
τυχαίο boot epoch. Redis κρατά connection hash με validated user ID, owner,
coordination epoch και ticket expiry. Το user sorted set δείχνει τις συνδέσεις
με lease deadline. Lua χρησιμοποιεί **Redis TIME** για register, heartbeat και
remove, ώστε οι διαφορετικές τοπικές ώρες instances να μη συγκρίνονται.
Heartbeat κάθε 5s ανανεώνει lease έως 15s, ποτέ μετά το authentication ticket.
Ληγμένο lease δεν ανασταίνεται: η instance κάνει abort το socket και ο client
πρέπει να επανασυνδεθεί. Έως 128 connections ανά user και 256 subscriptions ανά
connection γίνονται δεκτές. Stale user references καθαρίζονται σε read/register,
και τα indexes λήγουν. Η σύνδεση φέρει subscriptions με conversation ID,
MembershipId, generation και owner/boot epoch. Το MembershipId φιλτράρεται ξανά
στην PostgreSQL πριν από παρουσία που στέλνεται σε subscriber.

Το namespace περιλαμβάνει application/deployment/protocol/run ID, όπως στο
backplane. Redis coordination epoch επαναδημιουργείται μετά από reset· instances
που διαπιστώνουν αλλαγή κάνουν abort τα παλιά sockets και χρειάζονται νέες
subscriptions. Κάθε user presence lifetime έχει τυχαίο token και revision που
αυξάνει ατομικά σε 0→1 και 1→0. `PresenceChanged` μεταφέρει `epoch`, `lifetime`
και `version`; κάθε snapshot member έχει `revision` και `lifetime`. Οι αριθμοί
συγκρίνονται μόνο μέσα στο ίδιο epoch/lifetime. Η UI ζητά ξανά authorized
snapshot όταν λαμβάνει event, απορρίπτει σελίδες διαφορετικού epoch και θεωρεί
την παρουσία άγνωστη έπειτα από 10s χωρίς επιτυχή ανανέωση. Οι paginated
σελίδες μεταφέρουν επίσης PostgreSQL `rosterVersion` από MembershipId και η UI
απορρίπτει ανάμειξη διαφορετικών roster incarnations.

## Εξουσιοδότηση και αποτυχίες

`GetPresence` διαβάζει roster από PostgreSQL κάτω από το conversation row lock
και επιτρέπει μόνο current member. Online είναι OR των μη ληγμένων contributions
σε όλο το namespace. Δεν απαιτείται subscription του subject. `PresenceChanged`
στέλνεται σε explicit connection IDs των τρεχόντων members με τρέχον
MembershipId, και μόνο μετά από νέα ανάγνωση του PostgreSQL roster. SignalR
groups είναι routing hint, όχι authorization boundary.

Σε Redis outage το `GetPresence` αποτυγχάνει και η UI δείχνει «άγνωστη»· δεν
υποκαθίσταται με local-only offline. Το heartbeat κάνει abort αβέβαιες συνδέσεις
για επανασύνδεση. Η PostgreSQL και τα durable REST endpoints συνεχίζουν να
λειτουργούν. Χαμένα presence events δεν αναπαράγονται. Graceful disconnect
αφαιρεί αμέσως το contribution. Σε process crash χωρίς disconnect, το lease
λήγει το πολύ 15s μετά το τελευταίο heartbeat· ο browser μπορεί να χρειαστεί
νέο snapshot. Ένα `SendAsync` δεν αποδεικνύει παραλαβή.

## Όρια αυτού του issue

Το #99 δεν υλοποιεί distributed typing (#100), cross-instance revocation
barrier/recipient fencing (#101), ούτε τελική production-safe multi-instance
εγγύηση. Το local typing παραμένει local και δεν πρέπει να παρουσιαστεί ως
global. Η πλήρης αντιμετώπιση race ανάμεσα σε membership mutation και άλλες
protected δημοσιεύσεις ανήκει στο #101. Redis δεν αντικαθιστά PostgreSQL και
δεν αποτελεί durable queue ή backplane authorization.

Για deployment απαιτούνται κοινό Redis namespace, backplane, κοινά Data
Protection keys, συμβατή cookie/CORS ρύθμιση και sticky routing για transports
όπως περιγράφεται στο `signalr-redis-backplane.md`. Το `/health/realtime`
ελέγχει Redis connectivity. Ο έλεγχος αυτός δεν αποτελεί πλήρη readiness gate
για όλες τις μελλοντικές protected multi-instance λειτουργίες.
