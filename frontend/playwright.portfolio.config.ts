import { defineConfig, devices } from '@playwright/test'

export default defineConfig({
  testDir: './portfolio',
  testMatch: '**/*.spec.ts',
  reporter: 'list',
  use: { baseURL: 'http://localhost:5173' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: { command: 'npm run dev -- --host localhost --port 5173 --strictPort', url: 'http://localhost:5173', reuseExistingServer: false },
})
