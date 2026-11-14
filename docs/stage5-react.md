# Presence, typing και receipt UX — #83

Το React χρησιμοποιεί την κοινή authenticated σύνδεση ανά tab/session του #78.
Η οθόνη συνομιλίας συνδυάζει authorized presence, typing leases και durable
per-member receipts, χωρίς να αλλάζει REST sends, idempotency ή explicit read.

## Τι δείχνει

- Presence: αριθμός άλλων current members online/offline. Offline transport
  εμφανίζει «άγνωστη / επανασύνδεση», όχι ότι όλοι αποσυνδέθηκαν.
- Typing: μόνο άλλα μέλη του τρέχοντος receipt cohort, με το expiry του #80.
  Membership invalidation καθαρίζει το cache και ξαναφορτώνει authorized views.
- Direct, δικά μου committed messages: **Sent → Delivered → Read** σύμφωνα με
  τον R/D anchor του άλλου current member. SendAsync δεν δίνει Delivered.
- Group: `Delivered x/N · Read y/N`, όπου N τα άλλα current members.
  Νέο membership με null progress μετρά ανεξάρτητα. N=0 εμφανίζει «χωρίς άλλα
  τρέχοντα μέλη», ποτέ «διαβάστηκε από όλους».
- Anchor comparisons κρατούν PostgreSQL microseconds και UUID ordering.
  Τα anchor timestamps δεν εμφανίζονται ως ανθρώπινοι χρόνοι ανάγνωσης.

## Cumulative ACK χωρίς κενά

Μετά από authorized Subscribe και σε reconnect, ο client διαβάζει το persisted
delivery floor του actor. Διασχίζει REST history pages μέχρι αυτό το anchor ή
το τέλος του retained ιστορικού όταν D=null. Μόνο μετά από επιτυχή ολοκλήρωση
επιβεβαιώνει το νεότερο observed message με `messageId + membershipId`.
Live event προκαλεί την ίδια gap recovery, όχι τυφλό ACK του event ID.
Η διαδρομή observation κρατά μόνο μία σελίδα στη μνήμη, δεν αλλάζει το visible
history pagination και δεν σημαίνει ανθρώπινο Read. Το read button παραμένει
ρητό και στέλνει τη current membership incarnation.

Δεν υπάρχει local durable database ή offline send queue. «Observation» σημαίνει
επιτυχή client συγχρονισμό και durable server ACK, όπως στο contract #77.
Πρώτη είσοδος σε μεγάλο ιστορικό μπορεί να απαιτήσει πολλές σελίδες πριν το ACK.

## Reconciliation και lifecycle

Receipt/presence events είναι invalidations, όχι authoritative client mutations.
Ένας serialized worker ανά conversation/transport συνενώνει αιτήματα σε pending
flags και ξαναδιαβάζει PostgreSQL receipts και authorized presence snapshot.
Έτσι duplicates/out-of-order payloads δεν μπορούν να υποβαθμίσουν durable state.
Πολλαπλές receipt pages συνδυάζονται μόνο με ίδιο rosterVersion/myMembershipId·
μετά από τρεις διαδοχικές cohort αλλαγές επαναλαμβάνεται αργότερα η λήψη.
Τα events που φτάνουν κατά τη λήψη προκαλούν νέο κύκλο ώστε να μην χαθούν.

Navigation/logout/reconnect ακυρώνουν το προηγούμενο generation και τα fetches.
Own revocation καθαρίζει protected message/presence/typing/receipt views και
ξαναφορτώνει conversation details. Delayed revoke προηγούμενης incarnation
δεν αφαιρεί νέα membership. Αν snapshot εντοπίσει διαφορετική own incarnation,
η παλιά προβολή ακυρώνεται και απαιτείται νέο άνοιγμα της συνομιλίας.
Multiple tabs μοιράζονται την persisted πρόοδο χωρίς να μοιράζονται JS caches.

Best-effort invalidation μπορεί να χαθεί: reconnect, επόμενο event ή authorized
REST refresh επαναφέρει την κατάσταση. Δεν υποσχόμαστε durable event replay,
global last-seen, multi-instance παρουσία ή ανθρώπινη απόδειξη ανάγνωσης.

## Επαλήθευση

UI tests ελέγχουν direct transitions από snapshots, αγνόηση stale payloads,
reconnect recovery, ACK μόνο μετά από πλήρες prefix, explicit read, group counts,
N=0, incarnation replacement και own revocation. Τα προηγούμενα history/retry,
typing, navigation, session και responsive checks διατηρούνται.
Πραγματικό browser/API/PostgreSQL/SignalR E2E επιβεβαιώνει Sent → Delivered → Read
και persisted Read ⇒ Delivered, μαζί με τα Stage 1–4 regressions.
