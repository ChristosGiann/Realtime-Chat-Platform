# Ανάκληση subscriptions — #63

Το singleton ConversationConnections διατηρεί μόνο προσωρινή αντιστοίχιση
connection → authenticated Identity user → subscribed conversations για μία API
instance. Δεν αποτελεί durable membership ή presence. Disconnect αφαιρεί την εγγραφή.

Subscribe, membership mutation και post-commit broadcast χρησιμοποιούν το ίδιο
PostgreSQL conversation row lock. Η αφαίρεση/αποχώρηση αποθηκεύει την αλλαγή,
αφαιρεί όλα τα ενεργά tabs του χρήστη από το group και μετά κάνει commit.
Αποτυχία group removal ακυρώνει τη συναλλαγή και διακόπτει την προβληματική σύνδεση.
Αν το commit αποτύχει μετά την ανάκληση, η membership παραμένει αλλά απαιτείται
νέο explicit Subscribe: προτιμάται προσωρινή απώλεια ειδοποιήσεων από διαρροή.

Ο broadcaster αποκτά ξανά το row lock μετά το message commit. Αν προηγηθεί η
ανάκληση, δεν παραδίδει στον αφαιρεμένο χρήστη. Αν προηγηθεί το broadcast, το
ήδη queued event μπορεί να φτάσει μετά την HTTP απάντηση αφαίρεσης· bytes που
έχουν ήδη αποσταλεί δεν ανακαλούνται. Κανένα μεταγενέστερο broadcast δεν τον
περιλαμβάνει. Αυτό δεν αλλάζει τη σειρά ή την ανθεκτικότητα των μηνυμάτων στη βάση.

Άλλες συνομιλίες και χρήστες δεν επηρεάζονται. Rejoin δεν επαναφέρει παλιές
subscriptions. Απαιτεί νέο εξουσιοδοτημένο Subscribe. Τα tests καλύπτουν remove,
leave, δύο tabs, concurrent sends, isolation και explicit resubscribe.

Περιορισμός: μία API instance. Redis/backplane και διασύνδεση registries μεταξύ
instances ανήκουν στο Stage 6. Απευθείας SQL αλλαγές membership παρακάμπτουν τη
ροή εφαρμογής και δεν υποστηρίζονται ως live revocation API.
