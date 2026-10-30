import { spawn, spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
const cwd = fileURLToPath(new URL('../../', import.meta.url))
if (!process.env.E2E_CONNECTION_STRING) throw new Error('A dedicated E2E database is required.')
const env = { ...process.env, ConnectionStrings__ChatDatabase: process.env.E2E_CONNECTION_STRING }
for (const args of [
  ['tool', 'restore'],
  ['build', 'src/Chat.Api/Chat.Api.csproj', '--configuration', 'Release'],
  ['ef', 'database', 'update', '--project', 'src/Chat.Api', '--configuration', 'Release', '--no-build'],
]) {
  const result = spawnSync('dotnet', args, { cwd, env, stdio: 'inherit' })
  if (result.error) throw result.error
  if (result.status !== 0) process.exit(result.status ?? 1)
}
const child = spawn('dotnet', ['run', '--project', 'src/Chat.Api', '--configuration', 'Release', '--no-build',
  '--no-launch-profile', '--urls', 'http://localhost:5080'], { cwd, env, stdio: 'inherit' })
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal))
child.on('error', error => { console.error(error.message); process.exit(1) })
child.on('exit', code => process.exit(code ?? 1))
