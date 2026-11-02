# Frontend συνομιλιών — #31

Η ενότητα «Συνομιλίες» προστίθεται στην authenticated πλοήγηση, χωρίς αλλαγή των
Stage 1 routes. Το #/conversations δείχνει λίστα/κενή κατάσταση, το /new ροή
δημιουργίας και το /{id} details. Η λίστα και τα μέλη φορτώνουν επόμενες σελίδες
με ρητά κουμπιά. Η διάταξη γίνεται μονή στήλη σε μικρές οθόνες.

Direct: αναζήτηση χρήστη και επιλογή ανοίγει τη νέα ή υπάρχουσα συνομιλία.
Group: επιλεγμένα διακριτά μέλη και υποχρεωτικό όνομα έως 100 χαρακτήρες.
Ο ίδιος ο χρήστης εξαιρείται από το picker· ο backend ορίζει τον Owner.
Owner βλέπει add/remove για Members, όχι αφαίρεση του εαυτού του. Member βλέπει
leave. Direct δεν εμφανίζει group mutation actions. Η UI ορατότητα είναι βοήθεια,
όχι security boundary: όλες οι πράξεις ελέγχονται ξανά από τον backend.

Τα APIs επαναχρησιμοποιούν request/mutate του Stage 1: cookie credentials,
no-store, φρέσκο CSRF token σε κάθε mutation και session-expired event για 401.
Conversation-specific 403/404/409 εμφανίζουν κατάλληλα ελληνικά μηνύματα αντί
για τα identity/profile messages. Errors δεν εμφανίζουν server stack traces.
Μετά από mutation ανανεώνονται list/details. Μετά από leave κλείνουν τα details.
Σε απώλεια δικαιωμάτων mutation, τα protected controls κρύβονται μέχρι refresh.

Queries ακυρώνονται στο unmount και σε νέα αναζήτηση/φόρτωση, ώστε καθυστερημένες
απαντήσεις να μην αντικαθιστούν νεότερο state. Pending mutations απενεργοποιούν
κουμπιά. Names εμφανίζονται ως React text, χωρίς raw HTML. Missing profile
εμφανίζει user ID. Δεν εμφανίζονται private emails στα conversation DTOs/UI.

Playwright browser tests καλύπτουν loading/empty/error/retry, Direct και Group
creation, CSRF και request payloads, validation, Owner add/remove, Member leave,
pagination, missing profile, safe text, mobile overflow, anonymous και expired
session. Η πραγματική browser/API/PostgreSQL ολοκλήρωση ακολουθεί στο #32.

Δεν προστίθενται message composer/history, realtime updates ή τελικό visual polish.
Οι Direct λίστες εμφανίζουν γενικό τίτλο· τα ονόματα συμμετεχόντων εμφανίζονται
στα details. Δεν υπάρχει αυτόματη ενημέρωση: χρησιμοποιείται refresh μετά από
δικές μας mutations ή τα διαθέσιμα κουμπιά ανανέωσης.
