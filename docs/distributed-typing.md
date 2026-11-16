# Distributed typing leases — #100

Όταν είναι ενεργά `Redis:Enabled` και `Redis:BackplaneEnabled`, το typing
συντονίζεται στο Redis. Το Stage 5 local implementation παραμένει για
single-instance λειτουργία χωρίς backplane. Η PostgreSQL είναι authority για
Identity, membership και durable δεδομένα· **δεν** γράφεται typing στη βάση.
Δεν αποθηκεύονται draft, keystrokes, μήκος κειμένου ή client timestamp.

## Redis state

Όλα τα keys χρησιμοποιούν το κοινό `coordination` namespace του #99.

| Key | Περιεχόμενο και ζωή |
| --- | --- |
| `typing:{conversation}:{user}` | ZSET από exact `connectionId + MembershipId + subscription generation + InstanceId/BootEpoch + typing activity epoch` προς Redis lease deadline· λήγει 5s από την τελευταία accepted activity. |
| `typing-throttle:{connection}:{conversation}:{generation}` | Redis-time τελευταίου accepted start, TTL 5s· έως ένα start ανά 1s. Stop δεν περιορίζεται. |
| `typing-sequence:{connection}:{conversation}:{generation}` | Μεγαλύτερη server-owned operation sequence, TTL 30s· καθυστερημένη μικρότερη sequence απορρίπτεται. |
| `typing-operation:{connection}:{conversation}:{generation}:{activityId}` | Deduplication του ίδιου client activity ID για 30s· δεν είναι user ID ή περιεχόμενο πληκτρολόγησης. |
| `typing-revision`, `typing-lifetime`, `typing-state` ανά conversation/user | Atomic revision, τυχαίο lifetime token και προηγούμενη logical κατάσταση, TTL 1h. Το aggregate key λήγει σε 5s· τα metadata λήγουν αργότερα ώστε να μην επαναχρησιμοποιείται counter μέσα στην ίδια lifetime. |
| `typing-epoch` | Κοινό activity epoch. Σε διαπιστωμένη Redis αβεβαιότητα αλλάζει με CAS μία φορά κατά recovery, αποκλείοντας παλιές leases χωρίς replay. |

Κάθε Lua operation χρησιμοποιεί `Redis TIME`. Start/stop επαληθεύει atomically
το current coordination epoch, owner/boot epoch, connection lease και το **exact**
subscription value. Snapshot αφαιρεί expired entries και entries των οποίων το
connection ή η subscription δεν είναι πια current. Το aggregate είναι
`conversation + user`, ενώ οι leases είναι ανά tab/connection/generation. Το
μεγαλύτερο υπόλοιπο lease εμφανίζεται μία φορά ανά χρήστη, έως 5000ms. Stop
ενός tab αφαιρεί μόνο τη δική του lease. Το ZSET δέχεται έως 256 entries και
καθαρίζει stale references σε κάθε ανάγνωση/μεταβολή. Redis expiration
notifications δεν απαιτούνται για ορθότητα.

Κάθε νέο authorized Subscribe αποκτά νέα server-owned generation. Unsubscribe,
disconnect, ticket expiry, membership incarnation change και νέο BootEpoch κάνουν
την παλιά lease μη επιλέξιμη αμέσως σε snapshot. Process crash χωρίς cleanup
αφήνει μόνο lease που λήγει το πολύ 5s μετά το τελευταίο start. Δεν υπάρχει
background replay του προηγούμενου draft. Ο client χρειάζεται νέα πραγματική
δραστηριότητα μετά από reconnect.

Το παλιό `SetTyping(conversationId, active)` παραμένει για Stage 5 clients.
Το React client χρησιμοποιεί `SetTypingActivity(conversationId, active, activityId)`
με νέο τυχαίο ID ανά πραγματική ενέργεια. Επανάληψη του ίδιου ID δεν ανανεώνει
το lease· η server-owned sequence απορρίπτει καθυστερημένα operations.

## Εξουσιοδότηση και publication

`SetTyping`, `SetTypingActivity` και `GetTyping` απαιτούν valid Identity session, active connection,
current subscription και current PostgreSQL membership κάτω από το conversation
row lock. Οι snapshots επιστρέφουν μόνο τρέχοντα μέλη και υπόλοιπο lease. Τα
events πηγαίνουν σε explicit connection IDs των current members με current
MembershipId από το registry του #99. SignalR group δεν είναι authorization
boundary. Event μεταφέρει `epoch`, `lifetime`, per-aggregate `version` και
remaining milliseconds. Ο browser ζητά authorized snapshot όταν λάβει
distributed event και απορρίπτει παλιές responses της προηγούμενης transport
generation. Το τοπικό Stage 5 version contract παραμένει για single-instance.

Σε Redis outage το typing είναι unavailable/unknown· δεν υπάρχει local-only
fallback ή ψευδές authoritative stop. Μετά την ανάκαμψη το νέο activity epoch
αποκλείει τα παλιά leases. Χαμένα events δεν επαναπαίζονται. Ακόμη και χωρίς
stop event ο browser λήγει την ένδειξη από το bounded remaining lease.

## Όριο #100

Το #101 θα προσθέσει all-owner revocation barrier και πλήρες protected-publication
fencing για όλα τα realtime events. Το #100 επανελέγχει PostgreSQL membership
και explicit recipients, αλλά δεν ισχυρίζεται ακόμη production-safe
multi-instance authorization υπό κάθε race/partition. Το Redis είναι ephemeral
coordination, όχι durable queue ή source of truth.
