import { defineConfig, devices } from '@playwright/test'

if (!process.env.E2E_CONNECTION_STRING || !process.env.E2E_REDIS_CONNECTION_STRING)
  throw new Error('Stage 6 E2E requires dedicated PostgreSQL and Redis endpoints.')

export default defineConfig({
  testDir: './e2e-stage6',
  fullyParallel: false,
  workers: 1,
  timeout: 90000,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: 'list',
  use: { baseURL: 'http://localhost:5174', trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    { command: 'node scripts/start-stage6-apis.mjs', url: 'http://127.0.0.1:5090/health', timeout: 180000 },
    { command: 'npm run dev -- --host localhost --port 5174 --strictPort', url: 'http://localhost:5174',
      env: { VITE_API_BASE_URL: 'http://localhost:5081' } },
    { command: 'npm run dev -- --host localhost --port 5175 --strictPort', url: 'http://localhost:5175',
      env: { VITE_API_BASE_URL: 'http://localhost:5082' } },
  ],
})
