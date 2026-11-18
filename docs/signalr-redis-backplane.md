# SignalR Redis backplane transport — #98

Το έγγραφο καταγράφει το scope του #98 πριν από τα #99–#103. Η τελική κατάσταση και τα deployment requirements είναι στο [Stage 6 production handoff](stage6-production-handoff.md).

Το #98 προσθέτει το επίσημο `Microsoft.AspNetCore.SignalR.StackExchangeRedis`
**10.0.15**. Η 10.0.13/10.0.14 επηρεάζεται από
[GHSA-wr8v-9x5q-ggcm](https://github.com/advisories/GHSA-wr8v-9x5q-ggcm).
Η έκδοση 10.0.15 είναι η patched γραμμή .NET 10. Δεν αλλάζουν οι άλλες εξαρτήσεις.

`Redis:BackplaneEnabled=true` ενεργοποιεί `AddStackExchangeRedis` μόνο όταν ισχύει
και το υπάρχον `Redis:Enabled=true`. Χρησιμοποιούνται το ίδιο connection string,
operation timeout και το `RedisNamespace.BackplanePrefix` του
[Redis infrastructure #97](redis-infrastructure.md). Το channel prefix περιέχει
application, deployment, protocol και test run. Το database number μόνο του δεν
απομονώνει Redis Pub/Sub. Η default τιμή του BackplaneEnabled είναι false ώστε οι
υπάρχουσες single-instance εκτελέσεις να μην απαιτούν Redis. Το `/hubs/chat`,
Identity cookies, CORS/Origin, session validation, `CloseOnAuthenticationExpiration`
και authorized Subscribe/Unsubscribe διατηρούνται.

**Redis/SignalR backplane solves routing, not authorization.** Το group name είναι
routing primitive. Ακόμη και αν ο client ξέρει το όνομα, δεν μπορεί να καλέσει
server-side group operations. Οι υπάρχοντες publishers δεν τροποποιούνται στο #98.
Ειδικά το υπάρχον `MessageCreated` χρησιμοποιεί group broadcast στο single-instance
Stage 5. Μην θεωρείτε το app production-safe σε δύο instances: χωρίς distributed
registry και fail-closed revocation, ένα παλιό remote group membership μπορεί να
λάβει protected publication. Χρειάζονται current PostgreSQL membership, explicit
recipients, subscription/incarnation fencing και cross-instance revocation στα
#99/#101. Το παρόν PR αποδεικνύει μόνο τη δυνατότητα μεταφοράς.

## Απόδειξη μεταφοράς

Τα [πραγματικά A/B integration tests](../tests/Chat.Api.IntegrationTests/SignalRBackplaneTests.cs)
τρέχουν PostgreSQL, Redis, δύο ανεξάρτητους Kestrel hosts και authenticated
SignalR clients σε διαφορετικές URLs. Δημιουργούν membership με τα κανονικά REST
APIs, κάνουν authorized Subscribe και στέλνουν **test-only** event από
`IHubContext<ChatHub>` στην A προς conversation group που περιέχει connection στην B.
Ελέγχουν επίσης `Clients.Client(connectionId)` A→B και B→A. Outsider Subscribe
απορρίπτεται. Πρόσθετος host με διαφορετικό RunId, ίδια PostgreSQL και ίδια
membership δεν λαμβάνει test event από το namespace του αρχικού run.
Δεν προστίθεται production hub probe method ή business event.

## Outage / recovery

Όταν Redis δεν αποκρίνεται, `/health/realtime` επιστρέφει 503, ενώ
`/health/ready` συνεχίζει να μετρά μόνο PostgreSQL. Το existing message REST
commit συνεχίζει να επιστρέφει 201 αφού το post-commit notification failure
καταγραφεί, και το μήνυμα ανακτάται από authorized REST history. Χαμένο realtime
event δεν μπαίνει σε replay queue. Μετά την ανάκαμψη, νέο test-only targeted event
φτάνει στον client. Reconnect client ακολουθεί την υπάρχουσα REST resync διαδικασία.
Backplane reconnect δεν αποδεικνύει ότι παλιό Redis coordination state είναι έγκυρο.

Η δοκιμή pause/unpause ελέγχει failure του Redis transport, όχι ολοκληρωμένο
CoordinationEpoch/fencing. Τα #99/#101 θα καθορίσουν recovery gating για protected
realtime traffic. Δεν υλοποιούνται εδώ distributed presence, typing, ownership ή
revocation. Αυτά μαζί με restart/partition και πλήρες E2E ανήκουν στα #99–#103.

## Deployment

Χρειάζονται κοινή PostgreSQL/Redis, shared persistent Data Protection keys,
HTTPS/WSS, WebSocket proxy και session affinity. Το frontend κάνει κανονικό
SignalR negotiation με credentials, όπως εξηγεί το [Stage 6 design](stage6-distributed-semantics.md).
Το local Compose Redis είναι development only. Τα tests κάνουν explicit επιλογή
AUrl/BUrl και δεν βασίζονται σε load balancer.
