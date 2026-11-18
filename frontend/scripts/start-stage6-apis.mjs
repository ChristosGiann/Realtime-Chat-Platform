import { spawn, spawnSync } from 'node:child_process'
import { createServer } from 'node:http'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { randomUUID } from 'node:crypto'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../../', import.meta.url))
const apiDirectory = join(root, 'src', 'Chat.Api')
const database = process.env.E2E_CONNECTION_STRING
const redis = process.env.E2E_REDIS_CONNECTION_STRING
if (!database || !redis) throw new Error('Dedicated Stage 6 PostgreSQL and Redis endpoints are required.')
const keyPath = mkdtempSync(join(tmpdir(), 'chat-stage6-browser-'))
const shared = {
  ...process.env,
  ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
  ConnectionStrings__ChatDatabase: database,
  Redis__Enabled: 'true', Redis__BackplaneEnabled: 'true', Redis__ConnectionString: redis,
  Redis__ApplicationName: 'chat-stage6-e2e', Redis__Deployment: 'integration',
  Redis__ProtocolVersion: 'v1', Redis__RunId: randomUUID().replaceAll('-', ''),
  SharedDataProtection__KeyRingPath: keyPath,
  SharedDataProtection__ApplicationName: 'chat-stage6-browser',
  Cors__AllowedOrigins__0: 'http://localhost:5174',
  Cors__AllowedOrigins__1: 'http://localhost:5175',
}
for (const args of [
  ['tool', 'restore'],
  ['build', 'src/Chat.Api/Chat.Api.csproj', '--configuration', 'Release'],
  ['ef', 'migrations', 'has-pending-model-changes', '--project', 'src/Chat.Api', '--configuration', 'Release', '--no-build'],
  ['ef', 'database', 'update', '--project', 'src/Chat.Api', '--configuration', 'Release', '--no-build'],
]) {
  const result = spawnSync('dotnet', args, { cwd: root, env: shared, stdio: 'inherit' })
  if (result.error) throw result.error
  if (result.status !== 0) process.exit(result.status ?? 1)
}

const hosts = new Map()
function startHost(name, port) {
  const child = spawn('dotnet', ['bin/Release/net10.0/Chat.Api.dll', '--urls', `http://localhost:${port}`],
    { cwd: apiDirectory, env: { ...shared, RealtimeInstance__InstanceId: `stage6-${name}` }, stdio: 'inherit' })
  hosts.set(name, child)
  child.on('error', error => { console.error(error); process.exit(1) })
  return child
}
async function healthy(port) {
  for (const endpoint of ['ready', 'realtime']) {
    const deadline = Date.now() + 30000
    let ready = false
    while (Date.now() < deadline) {
      try { ready = (await fetch(`http://localhost:${port}/health/${endpoint}`)).ok }
      catch { ready = false }
      if (ready) break
      await new Promise(resolve => setTimeout(resolve, 100))
    }
    if (!ready) throw new Error(`API ${port} /health/${endpoint} did not recover.`)
  }
}
async function stopped(child) {
  if (child.exitCode !== null || child.signalCode !== null) return
  await new Promise(resolve => { child.once('exit', resolve); child.kill('SIGKILL') })
}
async function restartA() {
  const old = hosts.get('a')
  const previousPid = old.pid
  await stopped(old)
  const fresh = startHost('a', 5081)
  await healthy(5081)
  return { previousPid, pid: fresh.pid }
}
async function stopA() {
  const old = hosts.get('a')
  await stopped(old)
  return { previousPid: old.pid }
}
async function resumeA() {
  const fresh = startHost('a', 5081)
  await healthy(5081)
  return { pid: fresh.pid }
}

startHost('a', 5081)
await healthy(5081)
if (!(await fetch('http://localhost:5081/api/auth/csrf')).ok)
  throw new Error('API A could not initialize the shared Data Protection key.')
startHost('b', 5082)
await healthy(5082)
const control = createServer(async (request, response) => {
  if (request.method === 'GET' && request.url === '/health') {
    response.writeHead(200).end('ready'); return
  }
  if (request.method === 'POST' && ['/restart-a', '/stop-a', '/resume-a'].includes(request.url)) {
    try {
      const result = request.url === '/stop-a' ? await stopA()
        : request.url === '/resume-a' ? await resumeA() : await restartA()
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(result))
    } catch (error) { response.writeHead(500).end(String(error)) }
    return
  }
  response.writeHead(404).end()
})
control.listen(5090, '127.0.0.1')
function shutdown() {
  control.close()
  for (const child of hosts.values()) child.kill()
  setTimeout(() => rmSync(keyPath, { recursive: true, force: true }), 500)
}
process.once('SIGINT', shutdown)
process.once('SIGTERM', shutdown)
