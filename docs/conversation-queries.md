# Queries συνομιλιών — #30

GET /api/conversations επιστρέφει μόνο συμμετοχές του τρέχοντος χρήστη μέσω του
ConversationAuthorization. GET /api/conversations/{id} επιστρέφει προστατευμένα
details και δημόσια profile summaries μελών. Non-member/missing/deleted resource
επιστρέφει 404, anonymous/deleted Identity user 401. Οι αποκρίσεις είναι no-store.

Και τα δύο endpoints δέχονται offset (από 0) και limit (1–50, default 20).
Η λίστα ταξινομείται CreatedAt DESC, Id ASC. Τα μέλη στα details UserId ASC.
Το nextOffset είναι null στο τέλος. Στα details σελιδοποιούνται τα members,
όχι η ίδια η συνομιλία. Η λίστα δεν φορτώνει μέλη για κάθε row: επιστρέφει
id/type/name/createdAt/myRole/memberCount. Τα details προσθέτουν members με
userId/role/joinedAt/profile. Profile περιέχει μόνο τα ήδη δημόσια
userId/displayName/bio/avatarUrl· αν λείπει είναι null.

Η σελιδοποίηση είναι offset-based και deterministic για αμετάβλητο dataset.
Concurrent creations/removals μπορεί να μετακινήσουν σελίδες· ο client κάνει
refresh μετά από mutations. Δεν υπόσχεται snapshot όλων των σελίδων ή ordering
ανά πρόσφατο μήνυμα. Μεγάλες offset τιμές μπορεί να κοστίζουν περισσότερο· cursor
pagination και πρόσθετα indexes θα αξιολογηθούν με πραγματικό query workload.

Οι DTO projections εκτελούνται server-side, χωρίς foreach database lookups ή
φόρτωση Identity entities. Count/profile/member projections είναι μέρος του
query, όχι N+1. Το membership φίλτρο του #29 παραμένει στην τελική ανάγνωση,
ώστε ένα προηγούμενο authorization check να μην αρκεί μετά από removal.

PostgreSQL/Testcontainers tests καλύπτουν πολλούς χρήστες, Direct/Group, ίσα
timestamps με σταθερό tie-break, διαδοχικές σελίδες, profile privacy/null fallback,
removal, deleted resources, anonymous/deleted user και pagination validation.
Δεν αλλάζει schema και δεν απαιτείται migration. Last-message, unread, history
και presence παραμένουν εκτός scope.
