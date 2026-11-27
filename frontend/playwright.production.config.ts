import { defineConfig, devices } from '@playwright/test'

if (!process.env.PRODUCTION_BASE_URL) throw new Error('Set PRODUCTION_BASE_URL to the local production-container URL.')

export default defineConfig({
  testDir: './e2e-production',
  outputDir: './test-results-production',
  workers: 1,
  timeout: 90000,
  expect: { timeout: 15000 },
  reporter: 'list',
  use: { baseURL: process.env.PRODUCTION_BASE_URL, ignoreHTTPSErrors: true, trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
