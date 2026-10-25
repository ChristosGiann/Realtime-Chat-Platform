# Browser sessions — issue #5

## API και ροή browser

1. GET /api/auth/csrf με credentials: include. Η απόκριση δίνει requestToken και θέτει HttpOnly antiforgery cookie.
2. POST /api/auth/login με JSON email/password, credentials: include και X-CSRF-TOKEN από το προηγούμενο βήμα. Επιτυχία: 204 και authentication cookie.
3. GET /api/auth/me με credentials: include. Επιτυχία: 200 με id, userName, email, displayName, bio και avatarUrl.
4. Μετά το login, ανανέωσε το CSRF token μέσω GET /api/auth/csrf επειδή συνδέεται με την τρέχουσα ταυτότητα.
5. POST /api/auth/logout με credentials: include και το νέο X-CSRF-TOKEN. Επιτυχία: 204 και διαγραφή του cookie. Το επόμενο /me επιστρέφει 401.

Το login γίνεται με email, με το normalization του Identity. Άγνωστο email, λάθος password,
lockout και μη επιτρεπόμενη σύνδεση έχουν κοινό 401 ProblemDetails. Τα αποτυχημένα passwords
ενεργοποιούν το υπάρχον lockout του Identity. Invalid JSON/validation και αποτυχίες CSRF
επιστρέφουν 400. Τα προστατευμένα endpoints επιστρέφουν 401/403 αντί για redirects.
Τα responses session χρησιμοποιούν Cache-Control: no-store.

## Cookies και deployment

Το authentication cookie είναι HttpOnly, SameSite=Lax, Path=/ και χωρίς Domain.
Στην παραγωγή ονομάζεται __Host-Chat.Session και είναι πάντα Secure. Το ticket λήγει
σε 8 ώρες χωρίς sliding renewal. Δεν ενεργοποιείται remember-me: το browser cookie
είναι session cookie και δεν έχει persistent Expires.

Το antiforgery cookie είναι HttpOnly, SameSite=Strict και στην παραγωγή
__Host-Chat.Csrf με Secure. Το request token επιστρέφεται μόνο στο JSON και αποστέλλεται
ως X-CSRF-TOKEN. Μην αποθηκεύεις authentication cookie ή password σε localStorage.

Μόνο στο Development τα cookies ονομάζονται Chat.Session/Chat.Csrf και χρησιμοποιούν
SameAsRequest, επιτρέποντας τοπικό HTTP. Η προεπιλεγμένη επιτρεπόμενη CORS origin είναι
http://localhost:5173 με credentials. Στην παραγωγή δεν επιτρέπεται cross-origin
πρόσβαση από προεπιλογή. Για διαφορετική origin του ίδιου site ορίζεται
Cors:AllowedOrigins ως λίστα συγκεκριμένων trusted origins. Τα wildcard origins δεν
είναι συμβατά με credentialed cookies. Το SameSite δεν επιτρέπει αυθαίρετο cross-site hosting.

Η παραγωγή απαιτεί HTTPS και σωστή ρύθμιση trusted reverse proxy όταν χρησιμοποιείται.
Σε πολλαπλά instances πρέπει να διαμοιράζονται και να διατηρούνται τα Data Protection keys.
Δεν απαιτείται νέα migration ή JWT/refresh tokens.

Το logout ακολουθεί το τυπικό SignInManager.SignOutAsync: διαγράφει το session cookie
του συγκεκριμένου browser. Δεν είναι server-side revoke αποθηκευμένου αντιγράφου cookie
ούτε sign-out-everywhere. Η εγκυρότητα παλιών tickets περιορίζεται από τη λήξη τους
και τον έλεγχο security stamp του Identity. Server-side session registry δεν περιλαμβάνεται.

## Δοκιμές

Τα SessionTests ελέγχουν login/me/logout, anonymous και αλλοιωμένο cookie,
κοινές αποκρίσεις για invalid credentials και lockout, CSRF, malformed input,
cookie flags παραγωγής, τοπικό HTTP και credentialed CORS.
Χρησιμοποιούν πραγματική PostgreSQL σε Testcontainers.

    dotnet test RealtimeChat.sln --configuration Release

## Αναφορές

- https://learn.microsoft.com/aspnet/core/security/anti-request-forgery
- https://learn.microsoft.com/aspnet/core/security/authentication/identity-configuration
