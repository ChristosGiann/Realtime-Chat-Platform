# Εξουσιοδοτημένες subscriptions — #61

Το authenticated `/hubs/chat` προσθέτει `Subscribe(conversationId)` και
`Unsubscribe(conversationId)`. Οι μέθοδοι δεν δέχονται actor ID, connection ID ή
group name. Ο actor προκύπτει αποκλειστικά από το principal της σύνδεσης, αφού
επανελεγχθεί Identity/security stamp/lockout σε κάθε invocation.

Το Subscribe χρησιμοποιεί το ίδιο ConversationAuthorization read predicate με
τα REST endpoints (Group Owner/Member και Direct Member). Το membership ελέγχεται
στην PostgreSQL υπό το κοινό conversation row lock μέχρι την ένταξη στο group.
Μη μέλος, ανύπαρκτη συνομιλία και κενό Guid δίνουν γενικό HubException
«Conversation not available.», χωρίς διαρροή ύπαρξης συνομιλίας.

Το εσωτερικό group είναι `conversation:{conversationId:N}`. Η γνώση του ονόματος
δεν δίνει δικαίωμα πρόσβασης. Μόνο ο server καλεί Groups.AddToGroupAsync για το
Context.ConnectionId. Repeated subscribe/unsubscribe είναι idempotent. Το
Unsubscribe επιτρέπεται και χωρίς πλέον membership, γιατί αφαιρεί μόνο την ίδια
σύνδεση. Αποσύνδεση χάνει όλα τα groups· νέα σύνδεση δεν αποκαθιστά subscriptions.

Δεν αποθηκεύεται durable subscription state ούτε αλλάζει το REST messaging.
Δεν υπάρχουν production broadcasts σε αυτό το issue. Τα tests χρησιμοποιούν
server-side probes και barriers για να αποδείξουν πραγματική group isolation,
χωρίς να προσθέτουν test methods στο production hub. Ελέγχονται Direct/Group,
owner/member/outsider, actor spoofing, επαναλήψεις, unsubscribe, reconnect χωρίς
αυτόματη επανένταξη, concurrent removal και deleted Identity σε ανοιχτό socket.

Ανάκληση ήδη υπάρχουσας subscription μετά από membership αλλαγή παραμένει στο #63.
Το #62 προσθέτει post-commit broadcasts. Live frontend/resync/E2E ανήκουν στα
#64–#66. Η main δεν επηρεάζεται από το παρόν branch.
