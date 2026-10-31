# React frontend

Ελληνική διεπαφή εγγραφής, σύνδεσης, προσωπικών/δημόσιων προφίλ και αναζήτησης.

```powershell
npm ci
npm run dev -- --host localhost --port 5173
```

Το API αναμένεται στο http://localhost:5080. Προσαρμόζεται με VITE_API_BASE_URL
στο .env.local. Απαιτούνται συμβατές CORS/cookie ρυθμίσεις· η τιμή δεν είναι secret.

- npm run lint: ESLint.
- npm run build: TypeScript και production Vite build.
- npm run test:ui: 12 Chromium tests με mocked API.
- npm run test:e2e: πραγματικό API/PostgreSQL, με ρητό E2E_CONNECTION_STRING.

Πριν τα browser tests εκτέλεσε npx playwright install chromium. Για το E2E χρειάζεται
production build και αποκλειστική δοκιμαστική βάση. Αναλυτικές εντολές και όρια:
[Επαλήθευση Stage 1](../docs/stage1-verification.md).
