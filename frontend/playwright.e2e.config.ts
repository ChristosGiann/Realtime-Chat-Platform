import { defineConfig, devices } from '@playwright/test'
const connection = process.env.E2E_CONNECTION_STRING
if (!connection) throw new Error('Set E2E_CONNECTION_STRING to a dedicated disposable PostgreSQL database.')
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  timeout: 60000,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: 'list',
  use: { baseURL: 'http://localhost:5173', trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    { command: 'node scripts/start-e2e-api.mjs', url: 'http://localhost:5080/health/ready', timeout: 180000,
      env: { ConnectionStrings__ChatDatabase: connection, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development' } },
    { command: 'npm run preview -- --host localhost --port 5173 --strictPort', url: 'http://localhost:5173' },
  ],
})
