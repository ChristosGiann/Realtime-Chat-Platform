# Stage 6 — Distributed realtime semantics και failure model (#96)

Κατάσταση: **πρόταση architecture για review, όχι διαθέσιμη υλοποίηση**.
Parent: [#95](https://github.com/ChristosGiann/Realtime-Chat-Platform/issues/95).
Design: [#96](https://github.com/ChristosGiann/Realtime-Chat-Platform/issues/96).
Βάση: ολοκληρωμένο Stage 5, `331be7a1df3d95f1a8ef4e00f667a9795341ed3f`.
Το παρόν αλλάζει μόνο documentation. Τα #97–#103 ξεκινούν μετά το review/merge.

## 1. Authority και failure classes

| Κατηγορία | Authority / πολιτική |
| --- | --- |
| Durable operations | PostgreSQL: Identity, membership/authorization, messages, delivery/read progress. Αποτυχία realtime μετά το commit δεν αναιρεί επιτυχία REST. |
| Ephemeral UX | Redis coordination και SignalR: presence/typing μπορεί να γίνουν unavailable, να χάσουν events και να ανακατασκευαστούν. Unavailable δεν σημαίνει όλοι offline. |
| Security-sensitive coordination | Membership remove/leave: fail closed πριν το commit όταν δεν αποδεικνύεται ανάκληση των απαιτούμενων owners. Δεν είναι απλό best-effort UX event. |

`Sent = PostgreSQL commit`, `Delivered = durable recipient acknowledgement`,
`Read = durable read progress`, **Read ⇒ Delivered ⇒ Sent**. Παραμένουν idempotent
`clientMessageId`, PostgreSQL `(CreatedAt, Id)` ordering, membership incarnation,
CSRF, credentialed CORS, Origin validation και cookie/session checks.
Redis επιτρέπεται μόνο για backplane, ephemeral ownership, presence, typing και
coordination commands/ACKs. Δεν κρατά authoritative membership ή receipts,
message history, durable LastSeen ή business recovery log. Redis loss δεν ζητά DB repair.

## 2. Αφετηρία και απαιτούμενη αλλαγή μοντέλου

Διαβάστηκαν [Stage 5 verification](stage5-verification.md),
[semantics](stage5-semantics.md), [presence](signalr-presence.md),
[typing](signalr-typing.md), [receipts](receipt-events.md),
[revocation](signalr-revocation.md), [subscriptions](signalr-subscriptions.md),
[message events](signalr-message-events.md) και [reconnect](signalr-reconnect.md).

| Υπάρχον implementation | Συνέπεια για Stage 6 |
| --- | --- |
| [ConversationConnections](../src/Chat.Api/Realtime/ConversationConnections.cs) | Local connections/subscriptions, local presence/subscription counters· δεν αποτελούν global registry/order. |
| [ConversationPresence](../src/Chat.Api/Realtime/ConversationPresence.cs) / [ConversationTyping](../src/Chat.Api/Realtime/ConversationTyping.cs) | Current members και recipients ελέγχονται υπό conversation lock, αλλά μόνο local connections είναι γνωστές. |
| [Membership mutations](../src/Chat.Api/Conversations/ConversationMembershipController.cs) | `FOR UPDATE` → permission check → SaveChanges → Revoke → commit → best-effort notifications. SaveChanges δεν είναι commit. |
| [ReceiptBroadcaster](../src/Chat.Api/Realtime/ReceiptBroadcaster.cs) / [MembershipNotifications](../src/Chat.Api/Realtime/MembershipNotifications.cs) | Ξεχωριστό post-commit transaction/lock, current incarnation και local recipients. |
| [MessageBroadcaster](../src/Chat.Api/Realtime/MessageBroadcaster.cs) | Σήμερα χρησιμοποιεί conversation group υπό lock. Δεν αρκεί ασφαλές global routing όταν registry/owners είναι αβέβαιοι. |
| [ChatHub](../src/Chat.Api/Realtime/ChatHub.cs) / [Program](../src/Chat.Api/Program.cs) | Session validation, locked subscribe, ticket expiry, 30s client timeout / 15s keepalive· καμία Redis υποδομή. |

Κάθε protected publication στο Stage 6 θα επιλέγει **ρητούς connection recipients**
από τρέχουσα PostgreSQL membership και matching subscription incarnation υπό το ίδιο
conversation row lock, μέχρι το enqueue. Το backplane δρομολογεί σε αυτούς τους IDs.
Δεν επιτρέπεται blind `Clients.Group` για protected payloads με πιθανώς stale group
membership. Groups παραμένουν routing primitives για τις authorized subscriptions,
όχι authorization boundary. Η αλλαγή όλων των publishers είναι απαίτηση #98/#101,
όχι υλοποίηση αυτού του PR. Μέχρι να περάσει το #101, δεν δηλώνεται ασφαλές production scale-out.

## 3. Instance identity, epochs και namespace

Server-owned `InstanceId`: μοναδικό deployment instance identifier, ποτέ client input.
Κάθε εκκίνηση παράγει νέο random `BootEpoch`. Owner είναι το ζεύγος
`(InstanceId, BootEpoch)`, όχι hostname μόνο. Παλιός process δεν μπορεί να ανανεώσει,
διαγράψει ή κάνει ACK state νέου boot. Concurrent boots δεν μοιράζονται owner identity.

Επιπλέον `CoordinationEpoch` διακρίνει μία ζωή του κοινού Redis coordination namespace.
Είναι random opaque token, όχι χρονική σειρά. Missing/reset state, Redis restart ή
αβεβαιότητα μετά από reconnect προκαλεί conservative reset: atomic αλλαγή epoch,
νέο registration και απόρριψη προηγούμενων operations. Concurrent reset επιτρέπεται
να προκαλέσει επιπλέον reconnect, ποτέ αποδοχή παλιού epoch. Καμία instance δεν
σερβίρει realtime πριν επιβεβαιώσει το κοινό epoch και ολοκληρώσει registration.
Αν Redis επανέλθει από persistence snapshot, τα παλιά records δεν αποκτούν εγκυρότητα:
χρειάζεται νέο epoch και νέα ownership proof. Το deployment απαιτεί ένα κοινό Redis
primary, όχι ανεξάρτητα writable Redis partitions/failover με divergent state.

Conceptual namespace: application + environment/deployment + protocol version,
με χωριστές περιοχές `backplane` και `coordination`. Coordination records/commands
φέρουν epoch, owner, operation ID και όπου χρειάζεται conversation/membership IDs.
Tests έχουν μοναδικό run namespace. Channel prefixes απομονώνουν deployments ακόμη
και αν μοιράζονται Redis· διαφορετικός database number δεν απομονώνει Pub/Sub.
Δεν ορίζονται/δημιουργούνται executable keys εδώ. Exact encoding ανήκει στο #97.
Δεν επιτρέπονται secrets, cookies, drafts ή message bodies στα coordination records.

## 4. Ownership, leases και όρια ζωής

Conceptual record: `connectionId → authenticated userId → (InstanceId, BootEpoch)`
με coordination epoch, ticket expiry και lease deadline. Η σύνδεση ανήκει σε μία
instance· νέα σύνδεση παίρνει νέα identity. User ID προέρχεται από validated principal.
Subscription: `(connection, conversation, MembershipId, SubscriptionGeneration)`.
Generation είναι νέο server token σε κάθε νέα subscribe lifetime. Rejoin, revoke,
unsubscribe και disconnect ακυρώνουν την προηγούμενη generation.

Προτεινόμενα αρχικά budgets προς επαλήθευση στα #97/#102:

| Όριο | Τι σημαίνει |
| --- | --- |
| Owner/connection heartbeat 5s, lease 15s | Refresh μόνο ενεργών local connections από τον ίδιο owner/epoch. Lease δεν υπερβαίνει ticket expiry. |
| Redis operation timeout 2s | Σε uncertainty σταματά η αποδοχή νέων realtime operations και protected publication. Δεν αναμένεται lease expiry για ασφάλεια. |
| Revocation barrier 5s συνολικά | Όχι 5s ανά instance. Timeout πριν το commit → rollback / retryable 503. |
| Presence refresh/cache validity 10s | Χωρίς επιτυχή authorized refresh η UI μεταβαίνει σε unknown, όχι μόνιμο online/offline. |
| Typing lease 5s | Παραμένουν activity throttle 1s, client activity refresh 2s και idle stop 2s. |
| Command/ACK metadata ≤30s | Λήγει και απορρίπτεται μετά το operation deadline· δεν είναι replay log. |

Οι χρόνοι είναι design budgets, όχι αλλαγές configuration ή hard realtime SLA.
Redis deadlines κρίνονται από την ίδια Redis clock/atomic operation· local watchdogs
χρησιμοποιούν monotonic elapsed time. Δεν συγκρίνονται raw monotonic clocks instances.
UTC timestamps χρησιμοποιούνται για logs και υπάρχον ticket/message state.

Indexes δεν διατηρούν νεκρά entries επ' αόριστον: κάθε read φιλτράρει parent lease,
και bounded cleanup αφαιρεί stale references ακόμη και από ενεργά user/conversation
indexes. Empty containers λήγουν. Απαιτούνται caps ανά connection/subscriptions και
bounded batches· overload απορρίπτει νέα ephemeral state αντί για unbounded growth.
Exact caps επιλέγονται/μετρώνται στο #99. Redis expiration notifications δεν είναι
προϋπόθεση ορθότητας. Καμία heartbeat δεν ανασταίνει ληγμένο owner: νέο registration,
clear subscriptions και explicit authorized resubscribe απαιτούνται.

## 5. Presence σε healthy topology

`Online(U)` σημαίνει τουλάχιστον μία known valid active connection σε οποιαδήποτε
healthy registered API instance, με unexpired owner/connection lease και ticket.
Healthy σημαίνει ολοκληρωμένη registration, διαθέσιμο coordination και ενεργό
realtime gate. Διατηρείται ο Stage 5 περιορισμός: idle sockets δεν προσφέρουν
instant detection διοικητικού security-stamp/deletion χωρίς invocation.

Tabs σε A και B μετρούν ως ένας online user. 0→1 δίνει online, 1→0 offline·
1→2/2→1 δεν παράγουν δεύτερη logical transition. Subject δεν χρειάζεται subscription
στη συγκεκριμένη συνομιλία. Observer και subject πρέπει να είναι current members,
και live observer authorized subscriber. Δεν προστίθεται global presence endpoint.

Graceful disconnect αφαιρεί αμέσως contribution. Crash παύει τις ανανεώσεις:
το authoritative ephemeral snapshot αποκλείει owner το αργότερο στη λήξη του 15s
lease από το τελευταίο heartbeat. Δεν σημαίνει ακριβής γνώση της φυσικής πτώσης.
Με λειτουργικό δίκτυο/scheduler, browser refresh δίνει convergence έως 15s+10s από
το τελευταίο heartbeat. Transport silence ζωντανής instance παραμένει το SignalR
30s timeout συν scheduling και refresh. Σε partition ο browser δεν υπόσχεται αυτό
το convergence bound: μετά 10s χωρίς έγκυρο snapshot δείχνει unknown.

Snapshots συνθέτουν current PostgreSQL roster με live lease contributions. Δεν
ανακατασκευάζουν connections από membership rows. Redis loss οδηγεί σε unknown,
όχι μαζικό authoritative offline. Με νέο epoch, ενεργοί clients επανασυνδέονται,
registrations αναδημιουργούνται και νέα snapshots αντικαθιστούν το cache. Paginated
roster απαιτεί ίδια membership/roster context· αλλαγή cohort επανεκκινεί τη λήψη.

## 6. Distributed typing

Lease ανά connection/conversation/generation/incarnation, όχι ανά user μόνο.
Typing(U,C) είναι OR των valid leases όλων των tabs σε όλες τις healthy instances,
με μέγιστο remaining duration ≤5s. Stop στην A δεν αφαιρεί το lease της B.
Απαιτούνται current PostgreSQL membership, valid session και current subscription
σε κάθε accepted activity. Δεν μεταφέρονται draft, keystrokes ή client timestamps.

Start/stop χρησιμοποιούν fenced generation και idempotent operation sequence:
καθυστερημένο start δεν ξεπερνά νεότερο stop, duplicate retry δεν ανανεώνει deadline.
Unsubscribe/revoke/disconnect κάνει τα leases μη επιλέξιμα χωρίς να περιμένει cleanup.
Redis outage δεν ενεργοποιεί local-only fallback ούτε replay παλιών starts.
Reconnect απαιτεί νέα ανθρώπινη δραστηριότητα, όχι heartbeat του παλιού draft.

Snapshot/event μεταφέρει μόνο υπολειπόμενη ζωή. Receiver κρατά monotonic expiry,
το πολύ 5s από αποδοχή νέου valid update· duplicate δεν παρατείνει τη ζωή. Χαμένο
stop δεν κρατά μόνιμη ένδειξη. Καθυστερημένο μη superseded event μπορεί να δώσει
σύντομη stale ένδειξη έως 5s από τη λήψη, όχι εγγύηση 5s από πραγματικό keystroke.
Unknown epoch/generation απαιτεί snapshot, δεν ενεργοποιεί indicator αυτόματα.

## 7. Ordering και bounded revisions

Δεν μετατρέπουμε τα σημερινά local `long version` σε global sequence. Επιλέγουμε
atomic Redis revision ανά aggregate (presence user / typing conversation-user),
μαζί με coordination epoch και aggregate lifetime token. Revision allocation και
aggregate update/snapshot είναι μία atomic πράξη. Η ίδια σύγκριση ισχύει σε A και B.

Revision/tombstone μπορεί να λήξει όταν δεν υπάρχουν contributions. Αναδημιουργία
παίρνει **νέο lifetime token**, ποτέ counter reset μέσα στην ίδια lifetime. Έτσι δεν
χρειάζεται permanent per-user counter. Client συγκρίνει revisions μόνο εντός ίδιων
(epoch, lifetime, membership/synchronization context). Άγνωστο token κάνει invalidate
και authorized snapshot, όχι αυθαίρετο max/lexicographic ordering epochs.
Snapshot supersedes παλιότερα events μόνο για τα entities που περιέχει. Omitted
members αφαιρούνται μόνο από ολοκληρωμένο roster snapshot, όχι από μία σελίδα.

Connect/reconnect δημιουργεί client synchronization generation· responses παλιάς
γενιάς απορρίπτονται. Durable receipts συνεχίζουν να συγκρίνονται με PostgreSQL
anchors εντός ίδιας MembershipId, ανεξάρτητα από Redis revisions. Δεν υπάρχει
global order μεταξύ διαφορετικών conversations, exactly-once ή event replay.

## 8. Backplane και cross-instance revocation

Το SignalR Redis backplane εκτελεί cross-instance connection/group fan-out. Δεν
είναι authority, durable queue, receipt ACK ή distributed presence registry.
Protected data χρησιμοποιεί τους explicit recipients της §2. Το επιτυχές
`SendAsync` είναι enqueue attempt, όχι απόδειξη client observation.

### Remove/leave barrier πριν το commit

1. Πάρε PostgreSQL conversation `FOR UPDATE`, έλεγξε actor/permissions και διάβασε
   το ακριβές removed `MembershipId`. Ίδιο lock χρησιμοποιούν Subscribe, typing,
   membership writes και protected publication σε όλες τις instances.
2. Επαλήθευσε διαθέσιμο κοινό Redis epoch και snapshot όλων των live registered
   owner epochs. Για την αρχική ασφαλή έκδοση στείλε invalidate σε **όλους** αυτούς
   τους owners, όχι μόνο σε πιθανώς ελλιπές user→owner index. Registration precedes
   subscription· νέος owner δεν μπορεί να subscribes σε αυτή τη συνομιλία όσο
   κρατείται το row lock. Apply membership delete μέσω SaveChanges, όχι commit.
3. Command περιέχει operation ID, conversation, user, removed incarnation, expected
   owner/coordination epochs, το σύνολο subscription generations που διαβάστηκε
   υπό το lock και deadline. Handler κάνει atomically invalidate
   local subscription generations/typing, αφαιρεί groups και αποτρέπει επανένταξη
   της παλιάς generation. Duplicate command αφορά μόνο αυτές τις generations,
   ποτέ νέα subscription μετά από rollback/resubscribe. Missing registry metadata
   που εμποδίζει πλήρες target snapshot είναι uncertainty και αποτυγχάνει το barrier.
   ACK μόνο μετά το cleanup. Αν group removal αποτύχει,
   abort των affected sockets και NACK· δεν βαφτίζεται επιτυχία το abort request.
4. Ο handler **δεν αποκτά το ίδιο PostgreSQL row lock**: ο coordinator το κρατά.
   Δεν περιμένει pending Subscribe που περιμένει το lock. Ακυρώνει παλιές generations
   με local fence· μελλοντικό Subscribe θα ελεγχθεί στη βάση μετά την απελευθέρωση.
   Lock order: PostgreSQL conversation → σύντομη local/Redis mutation. Κανένα
   local mutex δεν κρατείται ενώ περιμένει DB/network ACK. Αυτό αποφεύγει deadlock.
5. Όλα τα αναμενόμενα ACKs πρέπει να ταιριάζουν operation/epochs/incarnation εντός
   του συνολικού 5s budget. Pub/Sub subscriber count δεν είναι ACK. Lost command
   μπορεί να ξανασταλεί idempotently εντός deadline· missing/late ACK, NACK, Redis
   uncertainty ή epoch change προκαλεί rollback και retryable 503, όχι success.
   Owner που έληξε **αφού επιλέχθηκε** δεν αφαιρείται σιωπηρά από το ACK set.
6. Μόνο μετά το barrier γίνεται PostgreSQL commit και HTTP success. Ακολουθούν τα
   best-effort `ConversationAccessRevoked` / `ConversationMembershipChanged` UX
   events. Δεν είναι αυτά ο μηχανισμός security revocation. Αν commit αποτύχει,
   membership παραμένει αλλά subscriptions μπορεί να έχουν χαθεί: explicit
   resubscribe, ποτέ rollback των fences σε παλιά generation.

Πριν HTTP success, membership έχει committed ως removed και οι απαιτούμενοι live
owners έχουν ανακαλέσει τις subscriptions της συγκεκριμένης incarnation. Μελλοντικό
publication υπό το lock δεν επιλέγει αυτό το μέλος. Bytes ήδη queued πριν την
ανάκληση μπορεί να φτάσουν αργότερα και δεν ανακαλούνται. Το guarantee αφορά νέα
publication, όχι διαγραφή δεδομένων που ήδη παρέλαβε ο client.

Dead owner που είχε ήδη λήξει πριν το snapshot δεν απαιτεί ACK. Δεν βασίζουμε την
ασφάλεια μόνο σε αυτή την εξαίρεση: expired/uncertain owner αποκλείεται από routing,
κάθε publisher κάνει current PostgreSQL authorization και explicit recipient
selection, και recovery καθαρίζει groups/subscriptions πριν ξαναδεχθεί traffic.
Process που ξυπνά μετά από pause ελέγχει lease/epoch πριν από νέα operation, όχι
μόνο σε background timer. Δεν επιτρέπεται protected blind group broadcast που θα
έφτανε σε ξεχασμένο socket. Missing registry entries μπορεί να χάσουν notification,
όχι να χορηγήσουν membership. Rejoin έχει νέα MembershipId· delayed old revoke
δεν αφαιρεί νέα subscription. No-op removal δεν δημοσιεύει ψευδές membership event.

Αν ο coordinator πεθάνει πριν commit, PostgreSQL rollback απελευθερώνει το lock.
ACK metadata λήγει, μερικά tabs απαιτούν resubscribe. Crash μετά commit πριν HTTP
response είναι ambiguous client outcome: REST membership read/retry αποσαφηνίζει,
όχι rollback committed DB. Η policy θυσιάζει διαθεσιμότητα removal κατά outage,
όπως το σημερινό fail-closed single-instance μοντέλο, αντί να επιστρέφει ψευδή επιτυχία.

## 9. Failure / partition / restart matrix

| Συμβάν | Durable REST | Realtime / security policy |
| --- | --- | --- |
| Redis unavailable πριν operation | Message/read/delivery συνεχίζουν με PostgreSQL authorization· remove/leave που χρειάζεται barrier επιστρέφει 503 χωρίς commit | Reject connect/subscribe/typing/presence snapshot· protected notifications παραλείπονται, UI unknown |
| Redis πέφτει μετά durable commit | Committed message/receipt επιτυχία διατηρείται | Warning/metrics, REST recovery, καμία αυτόματη replay queue |
| Redis πέφτει κατά revocation barrier | Rollback αν δεν έγινε commit· αν χάθηκε μόνο η HTTP απάντηση, read-back | Δεν εξάγεται ACK από timeout ή χαμένο publish |
| Μόνο η A χάνει Redis | PostgreSQL REST παραμένει δυνατό στην A· mutation στην B αποτυγχάνει αν απαιτεί ACK της A | A κλείνει realtime gate, ακυρώνει subscriptions/typing και aborts sockets· B δεν χρησιμοποιεί A μετά lease expiry |
| Redis reconnect / restart / restored old state | Καμία επιδιόρθωση memberships/messages/receipts | Conservative epoch reset, καμία αυτόματη επαναφορά old groups, clear local state και reconnect/resubscribe/snapshots |
| API crash / restart | PostgreSQL transactions commit ή rollback κανονικά | Νέο BootEpoch, old leases λήγουν, clients επανασυνδέονται, typing δεν αναβιώνει |
| PostgreSQL unavailable ενώ Redis healthy | Protected reads/writes αποτυγχάνουν | Redis δεν υποκαθιστά authorization· κανένα νέο protected publication/subscription/snapshot |
| Long process pause / late commands | Καμία μεταβολή durable authority | Check fences στο resume, reject expired operation και old ACK, re-register |

Το realtime gate καλύπτει coordination **και** backplane recovery. Μετά οποιαδήποτε
αβεβαιότητα απαιτείται καθαρό connection lifecycle, ακόμη κι αν ο backplane client
επανασυνδέεται αυτόματα. Ο browser επαναλαμβάνει session validation → authorized
subscribe → fresh ephemeral snapshots → REST history/receipt resync. Διατηρείται
πλήρης prefix recovery πριν automatic delivery ACK, χωρίς false Delivered/Read.

Δεν υπάρχει consensus protocol ούτε εγγύηση availability σε arbitrary partition.
Η αρχιτεκτονική υποθέτει κοινή authoritative PostgreSQL και ένα Redis coordination
history. Απώλεια/restore Redis state θεωρείται epoch reset, όχι ασφαλές failover
χωρίς revalidation. Clock pauses/δίκτυο δεν επιτρέπουν ακριβή physical online/offline
γνώση. Κανένα finite timeout δεν αποδεικνύει ότι remote process πέθανε· για αυτό
TTL δεν αντικαθιστά ACK σε in-progress barrier και Redis δεν αρκεί για authorization.

## 10. Deployment assumptions

- Reverse proxy/load balancer με WebSocket upgrade και επαρκή idle timeouts για
  keepalive. HTTPS/WSS εξωτερικά, trusted forwarded headers και σωστό scheme/host.
- Το [τρέχον frontend](../frontend/src/messages/RealtimeSession.ts) κάνει negotiation
  με `withCredentials: true`, όχι WebSockets-only/skipNegotiation. Επομένως απαιτείται
  **session affinity** για όλα τα requests μιας SignalR connection. Η εξαίρεση
  WebSockets-only + skipNegotiation δεν υιοθετείται σιωπηρά σε αυτό το design.
  Δείτε [Microsoft scaling guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale?view=aspnetcore-10.0).
- Κοινό προστατευμένο, persistent Data Protection key ring και ίδιο application
  discriminator, συμβατά cookie scheme/name/path/domain/SameSite σε όλες τις
  instances. Το ephemeral Redis coordination store δεν φιλοξενεί αυτά τα keys.
  Keys survive restart και προστατεύονται σε storage/access. Δείτε
  [Data Protection configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).
- Κοινή PostgreSQL, κοινό Redis κοντά στις APIs, ίδιο namespace/protocol. Redis
  restricted network/ACL/TLS όπου απαιτείται· πρόσβαση coordination είναι trusted
  server-only. Επιλογή eviction δεν επιτρέπεται να χάνει σιωπηρά active fence state:
  missing state προκαλεί unavailable/reset. Δεν απαιτείται Redis durability.
- Τα allowed frontend origins, credentials, CSRF και WebSocket Origin checks
  παραμένουν αυστηρά. Cookie session δεν μετατρέπεται σε client-supplied actor ID
  ούτε σε ASP.NET session-state dependency. Affinity δεν αντικαθιστά shared keys.
- Deployment όλων των instances με συμβατό protocol· Stage 5/6 mixed serving δεν
  θεωρείται ασφαλές. Drain παλιών connections πριν ενεργοποίηση scale-out. Αλλαγή
  protocol namespace απαιτεί coordinated drain/reset, όχι δύο ανεξάρτητα registries.

Το [Microsoft Redis backplane guide](https://learn.microsoft.com/en-us/aspnet/core/signalr/redis-backplane?view=aspnetcore-10.0)
τεκμηριώνει prefix, affinity και απώλεια events σε outage. Το
[Redis Pub/Sub contract](https://redis.io/docs/latest/develop/pubsub/) δεν παρέχει
replay για χαμένους subscribers. Τα παραπάνω ACK/fencing rules είναι δική μας
απαίτηση εφαρμογής, όχι εγγύηση που παρέχουν αυτές οι βιβλιοθήκες.

## 11. Verification plan και επόμενα issues

| Issue | Υλοποίηση / απαιτούμενη απόδειξη |
| --- | --- |
| #97 | Redis infrastructure/config, isolation namespaces, shared keys, δύο πραγματικές APIs και κοινή PostgreSQL/Redis· κανένα accidental cross-test routing |
| #98 | Backplane fan-out σε A/B, explicit authorized targets για protected events, outage μετά commit δεν αναιρεί REST success |
| #99 | Ownership/heartbeat/expiry, cross-instance multi-tab presence, crash/ghost cleanup, paginated authorized snapshots, bounded index/revision lifetime |
| #100 | Tabs σε A/B, lost stop, duplicate/delayed start, throttle, generation invalidation, 5s expiry και reset χωρίς resurrection |
| #101 | Barrier με όλα τα owners, lost/NACK/late ACK, partition, concurrent subscribe/remove/rejoin/send/receipt/presence/typing, rollback και κανένα νέο protected event μετά success |
| #102 | Redis/API restart, restored stale state, process pause, lock/ACK deadlocks, coordinator crash πριν/μετά commit, contention/time budgets και PostgreSQL outage |
| #103 | Πραγματικοί browsers σε διαφορετικές APIs, shared cookies, reconnect REST gaps/receipts, όλη Stage 1–5 regression suite και CI |

Τα concurrency tests χρειάζονται controlled barriers και ανεξάρτητες DB reads,
όχι μόνο sleeps ή assertions πάνω σε mocks. Τα time budgets δοκιμάζονται με
ελεγχόμενο clock και πραγματικά failure scenarios. Δεν προστίθενται fake prose tests.

## 12. Σημεία review και όρια του παρόντος PR

Οι παραπάνω επιλογές είναι συγκεκριμένη προτεινόμενη βάση, όχι ήδη υλοποιημένες
εγγυήσεις. Πριν προχωρήσει το #97 χρειάζεται architecture review ιδίως για:

1. Αποδοχή του fail-closed remove/leave και της προσωρινής απώλειας subscriptions
   σε rollback· all-owner barrier έχει κόστος που αυξάνει με τις instances.
2. Budgets 5s heartbeat / 15s lease / 2s operation / 5s barrier / 10s refresh και
   contention του PostgreSQL lock. Η ασφάλεια δεν βασίζεται στην ακρίβεια scheduler.
3. Conservative cluster epoch reset μετά reconnect και απαίτηση κοινής Redis history.
   HA/Sentinel/Cluster failover και seamless rolling upgrades δεν δηλώνονται λυμένα.
4. Exact cap/key/revision encoding και Data Protection storage provider στα #97/#99,
   με αποδείξεις atomicity/fencing πριν οποιοδήποτε optimization του barrier.
5. Wire compatibility για epoch/lifetime tokens και unknown/refresh UX στα #99/#100/#103.
   Οι σημερινοί Stage 5 local-version clients δεν είναι αυτόματα distributed-safe.

Δεν προστίθενται packages, containers, config, health checks, Redis keys, production
code, tests ή migrations. Δεν αλλάζει main/dev και δεν γίνεται merge από αυτό το PR.
Το #88 παραμένει deferred/final feature. Δεν ξεκινά το #97 πριν review/merge του #96.
