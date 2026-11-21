# Apothiki frontend design system (#115)

Το **Apothiki** είναι το όνομα που εμφανίζεται στο frontend και στον τίτλο του browser. Το repository, τα API routes και τα identifiers υποδομής διατηρούνται ως έχουν. Το wordmark είναι τυπογραφικό· θέση για μελλοντικό εικονίδιο μπορεί να προστεθεί χωρίς αλλαγή ονόματος. Δεν έχει οριστεί μόνιμο logo ή εικονίδιο.

## Tokens

Το `frontend/src/styles/tokens.css` είναι η μοναδική πηγή για χρώματα, αποστάσεις, ακτίνες, σκιά, βασική γραμματοσειρά, ύψος controls και σύντομη διάρκεια κίνησης. Τα ονόματα είναι σημασιολογικά (`--surface-panel`, `--text-secondary`, `--action-primary`, `--focus-ring`), ώστε ένα component να μη γνωρίζει ποια παλέτα χρησιμοποιείται. Το `index.css` ορίζει τα κοινά controls, focus και reduced-motion κανόνες· το `App.css` εφαρμόζει τα tokens στα υπάρχοντα panels, φόρμες και λίστες.

Σε πλάτος πάνω από 700px η βάση είναι σκούρο ανθρακί/navy με διακριτικό violet accent. Μέχρι 700px η βάση είναι off-white/navy με cobalt accent. Η επιλογή γίνεται από το viewport, χωρίς user-facing theme switcher. Τα status χρώματα υπάρχουν ως tokens για επόμενα components, αλλά η σημασία κατάστασης πρέπει να δηλώνεται και με κείμενο/ARIA, ποτέ μόνο με χρώμα.

## Κανόνες χρήσης

- Χρησιμοποιούμε semantic tokens αντί για νέα hard-coded χρώματα στα Stage 7 screens.
- Διατηρούμε τα υπάρχοντα semantic HTML στοιχεία, labels, roles και προσβάσιμα ονόματα. Το visible brand link ονομάζεται `Apothiki`.
- Τα interactive controls έχουν ελάχιστο ύψος 44px, ορατό `:focus-visible`, hover και disabled states.
- Το `prefers-reduced-motion` αφαιρεί πρακτικά transitions/animations. Δεν απαιτείται εξωτερική γραμματοσειρά ή theme persistence.
- Η τελική διάταξη desktop και mobile ανήκει στα #116–#120. Το #115 παρέχει την κοινή βάση και κρατά την υπάρχουσα δομή οθονών.

## Desktop messaging shell (#116)

Στις συνομιλίες, πάνω από 700px, το `App` γίνεται σταθερού ύψους frame με αριστερή πλοήγηση. Το `ConversationsPage` χωρίζει τη λίστα συνομιλιών από το ενεργό workspace. Το workspace κρατά header και composer στη θέση τους, ενώ `.conversation-list-scroll` και `.message-history` έχουν ανεξάρτητο scroll. Τα δεδομένα λίστας δίνουν τίτλο, direct/group τύπο και πλήθος μελών· δεν περιέχουν preview, unread count ή presence ανά συνομιλία, συνεπώς δεν εμφανίζονται επινοημένες τιμές.

Τα υπάρχοντα στοιχεία μελών και οι ενέργειες διαχείρισης βρίσκονται σε προαιρετικό utility panel. Σε πλάτος τουλάχιστον 1200px εμφανίζεται ως τρίτη στήλη· σε μικρότερο desktop πλάτος ανοίγει ως συρόμενη επιφάνεια πάνω από το workspace, με δικό της κουμπί κλεισίματος. Σε νέα φόρτωση είναι κλειστό στα 701–1199px. Το mobile <=700px διατηρεί τη ροή της προηγούμενης διάταξης έως το #119. Η τελική εμφάνιση μηνυμάτων/composer και οι λεπτομέρειες ομάδας ανήκουν στα #117 και #118 αντίστοιχα.

Τα επιπλέον semantic tokens `--surface-rail`, `--surface-selected`, `--surface-hover` και `--border-accent` ορίζουν τις καταστάσεις πλοήγησης και ενεργής συνομιλίας και στις δύο παλέτες. Τα `aria-current`, headings, landmarks, ονόματα κουμπιών και υπάρχουσες realtime αναγγελίες παραμένουν διαθέσιμα σε πληκτρολόγιο και screen reader.

## Chat presentation (#117)

Το `MessagingPanel` κρατά αμετάβλητα το durable ordering, pagination, idempotent send, receipt snapshot και reconnect/REST recovery. Η παρουσίαση διαχωρίζει εισερχόμενα/εξερχόμενα bubbles και μειώνει το κενό ανάμεσα σε διαδοχικά μηνύματα του ίδιου αποστολέα εντός πέντε λεπτών. Κάθε μήνυμα παραμένει ξεχωριστό `<li>` με το υπάρχον `data-message-id`. Σε group εμφανίζεται το όνομα του άλλου αποστολέα στην αρχή μιας οπτικής ακολουθίας· στα δικά μας και στα direct μηνύματα το όνομα παραμένει διαθέσιμο σε screen readers. Το `<time dateTime>` κρατά το πλήρες timestamp, ενώ η ορατή μορφή δείχνει ώρα για σήμερα και ημερομηνία/ώρα για παλιότερα μηνύματα.

Τα `--surface-message-own`, `--surface-message-incoming`, `--border-message-own`, `--surface-composer`, `--text-receipt` και `--surface-status-danger` καλύπτουν και τις δύο παλέτες. Τα receipt labels παραμένουν ακριβώς τα υπάρχοντα durable `Sent`/`Delivered`/`Read` και τα per-member group aggregates. Το χρώμα και οι διακοσμητικές ενδείξεις δεν αντικαθιστούν το κείμενο. Unknown presence δεν παρουσιάζεται ως offline. Το typing slot κρατά σταθερό ύψος πάνω από τον composer χωρίς νέα animation ή αλλαγή lease. Το textarea μεγαλώνει έως όριο και ο χειροκίνητος τρόπος αποστολής/ανάγνωσης δεν αλλάζει.

Η ιστορία συνεχίζει να κυλά ανεξάρτητα, με ρητό κουμπί για παλιότερα μηνύματα και χωρίς αυτόματο scroll που θα μετακινούσε τον χρήστη από παλιότερη θέση. Το unread count προέρχεται από το durable read state. Δεν προστίθεται unread separator επειδή το paginated history δεν εγγυάται ότι το πραγματικό όριο βρίσκεται στα φορτωμένα μηνύματα. Previews ή unread counts στη λίστα απαιτούν ξεχωριστά διαθέσιμα δεδομένα. Το πλήρες group/details UI και το mobile navigation μένουν στα #118 και #119.

## Conversation details (#118)

Το προαιρετικό utility panel παρουσιάζει τα υπάρχοντα στοιχεία direct/group, τον ρόλο του τρέχοντος μέλους και τη σελιδοποιημένη λίστα μελών. Οι ρόλοι εμφανίζονται με κείμενο και οι ελλείπουσες δημόσιες πληροφορίες προφίλ αντικαθίστανται από το user ID. Η προσθήκη και αφαίρεση παραμένουν διαθέσιμες μόνο στον ιδιοκτήτη ομάδας· η αποχώρηση μόνο σε απλό μέλος ομάδας. Η αφαίρεση και η αποχώρηση ζητούν ρητή επιβεβαίωση με το όνομα του στόχου ή την ομάδα πριν καλέσουν τα υπάρχοντα endpoints. Το focus μετακινείται στην επιβεβαίωση και επιστρέφει στο αρχικό κουμπί μετά την ακύρωση.

Το API εξακολουθεί να αποφασίζει την εξουσιοδότηση. Σε 403/404 τα στοιχεία και οι ενέργειες κρύβονται, ενώ σε 409 εμφανίζεται σφάλμα χωρίς αισιόδοξη αλλαγή μελών. Οι σελίδες μελών συγχωνεύονται ανά user ID. Υπάρχουν διακριτές ενδείξεις φόρτωσης, αποθήκευσης, κενού αποτελέσματος και μη διαθέσιμης συνομιλίας. Το panel δεν προσθέτει παρουσία ανά μέλος: το υπάρχον realtime presence δεν εκτίθεται εδώ ως αξιόπιστο snapshot, επομένως δεν επινοείται κατάσταση online/offline. Η συνομιλία και τα durable δεδομένα παραμένουν στην PostgreSQL· το #118 δεν αλλάζει API, SignalR ή migration. Η τελική mobile πλοήγηση ανήκει στο #119.

## Mobile messaging flow (#119)

Στα έως 700px οι routes `#/conversations`, `#/conversations/:id` και `#/conversations/:id/details` παρουσιάζουν αντίστοιχα μία οθόνη λίστας, chat και στοιχείων. Το details sub-route υποστηρίζει refresh και browser navigation. Το υπάρχον `ConversationDetailsPanel` και οι ενέργειές του χρησιμοποιούνται ξανά· το chat παραμένει mounted όταν εμφανίζονται τα στοιχεία, χωρίς αλλαγή σε subscriptions ή durable state. Τα ρητά back links οδηγούν από στοιχεία στο chat και από chat στη λίστα. Το focus μετακινείται στο panel και επιστρέφει στο κουμπί στοιχείων κατά την επιστροφή. Από 701px και πάνω ισχύει η υπάρχουσα desktop δομή λίστας/workspace/drawer ή τρίτης στήλης.

Το mobile frame χρησιμοποιεί `100dvh`, εσωτερικό flex layout και `min-height: 0`. Η ιστορία είναι το κύριο scroll area· header, status και composer μένουν μέσα στο διαθέσιμο ύψος, ακόμη και σε κοντό viewport. Το composer χρησιμοποιεί safe-area bottom padding χωρίς absolute positioning ή keyboard JavaScript. Η πλοήγηση, τα controls μελών και οι ενέργειες chat έχουν touch area περίπου 44px. Τα σύντομα ορατά labels σε μικρό πλάτος κρατούν τα πλήρη προσβάσιμα ονόματα. Presence, reconnect, unread και receipts δεν αποκρύπτονται. Η λίστα εμφανίζει μόνο τίτλο, τύπο και πλήθος μελών που παρέχει το API. Δεν εισάγονται preview, avatar ή unread badge λίστας. Το τελικό accessibility audit και η συνολική UI τελειοποίηση ανήκουν στο #120.
