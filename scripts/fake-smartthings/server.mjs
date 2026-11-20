// A fake SmartThings cloud for local development: serves the connector's committed test fixtures
// (tests/ControlHome.Connectors.Tests/Fixtures/SmartThings, scrubbed Home Assistant data, see SOURCE.md) so the real
// app can import Samsung appliances without a Samsung account. Any bearer token is accepted; nothing leaves this machine.
//
//   node scripts/fake-smartthings/server.mjs [port]        (default 18090)
//
// Then run the API in Development with Development__SmartThingsApiUrl=http://localhost:18090/ and add a SmartThings
// integration: in "Personal access token" mode with any token (e.g. "fake-token"), or with "Let ControlHome create your
// app". There, copy the state from each sign-in link's address (nothing to sign in to) and paste
// http://localhost:61973/finish?code=fake&state=<that state> (the CLI sign-in; the fake then creates an app), then
// https://httpbin.org/get?code=fake&state=<the next state> (or <ControlHome address>/oauth/callback?… with an address).
import { createServer } from 'node:http'
import { readFileSync, readdirSync, existsSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'tests', 'ControlHome.Connectors.Tests', 'Fixtures', 'SmartThings')
const port = Number(process.argv[2] ?? 18090)
const read = (...parts) => JSON.parse(readFileSync(join(root, ...parts), 'utf8'))

const locations = read('locations.json')
const locationId = locations.items[0].locationId
const rooms = read('rooms.json')

// One listing item per fixture: the device with its status embedded per capability, plus a health state (as the
// connector tests compose it).
const devices = readdirSync(join(root, 'devices'))
  .filter((file) => file.endsWith('.json'))
  .flatMap((file) => {
    const status = read('device_status', file)
    return read('devices', file).items.map((device) => {
      device.locationId = locationId
      device.healthState = { state: 'ONLINE', lastUpdatedDate: new Date().toISOString() }
      for (const component of device.components ?? []) {
        for (const capability of component.capabilities ?? []) {
          const attributes = status.components?.[component.id]?.[capability.id]
          if (attributes) capability.status = attributes
        }
      }
      return { device, status }
    })
  })

const json = (res, code, body) => {
  res.writeHead(code, { 'Content-Type': 'application/json' })
  res.end(JSON.stringify(body))
}

createServer((req, res) => {
  const url = new URL(req.url, `http://localhost:${port}`)
  const path = url.pathname.replace(/^\/v1/, '')
  console.log(req.method, url.pathname + url.search)

  // OAuth code exchange and refresh: the household's app (HTTP Basic client authentication) or the SmartThings CLI's
  // sign-in (a public client sending a PKCE code_verifier). Any client and code are accepted.
  if (req.method === 'POST' && path === '/oauth/token') {
    let body = ''
    req.on('data', (chunk) => (body += chunk))
    req.on('end', () => {
      const form = new URLSearchParams(body)
      const basic = req.headers.authorization?.startsWith('Basic ')
      if (!basic && !form.get('code_verifier')) return json(res, 401, { error: 'invalid_client' })
      const stamp = Date.now()
      json(res, 200, {
        access_token: `fake-${basic ? 'access' : 'cli'}-${stamp}`,
        token_type: 'bearer',
        refresh_token: `fake-refresh-${stamp}`,
        expires_in: 86399,
        scope: basic ? 'r:locations:* r:devices:* x:devices:*' : 'controller:stCli',
      })
    })
    return
  }

  if (!req.headers.authorization?.startsWith('Bearer ')) return json(res, 401, { error: { code: 'Unauthorized' } })
  // "Let ControlHome create your app": creates an API_ONLY app and returns its OAuth client.
  if (req.method === 'POST' && (path === '/apps' || path === '/smartapps')) {
    return json(res, 200, {
      app: { appId: '00000000-0000-4000-8000-00000000fa4e', appName: 'controlhome-fake' },
      oauthClientId: '00000000-0000-4000-8000-00000000c11e',
      oauthClientSecret: 'fake-client-secret',
    })
  }

  if (req.method === 'GET' && path === '/locations') return json(res, 200, locations)
  if (req.method === 'GET' && /^\/locations\/[^/]+\/rooms$/.test(path)) return json(res, 200, rooms)
  if (req.method === 'GET' && path === '/devices') return json(res, 200, { items: devices.map((d) => d.device), _links: {} })

  let match = /^\/devices\/([^/]+)\/status$/.exec(path)
  if (req.method === 'GET' && match) {
    const found = devices.find((d) => d.device.deviceId === match[1])
    return found ? json(res, 200, found.status) : json(res, 404, { error: { code: 'NotFound' } })
  }

  match = /^\/devices\/([^/]+)\/commands$/.exec(path)
  if (req.method === 'POST' && match) {
    let body = ''
    req.on('data', (chunk) => (body += chunk))
    req.on('end', () => {
      const commands = JSON.parse(body || '{}').commands ?? []
      json(res, 200, { results: commands.map((_, i) => ({ id: String(i), status: 'ACCEPTED' })) })
    })
    return
  }

  match = /^\/capabilities\/([^/]+)\/(\d+)$/.exec(path)
  if (req.method === 'GET' && match) {
    const file = join(root, 'capabilities', `${match[1]}.json`)
    return existsSync(file) ? json(res, 200, read('capabilities', `${match[1]}.json`)) : json(res, 404, { error: { code: 'NotFound' } })
  }

  json(res, 404, { error: { code: 'NotFound' } })
}).listen(port, () => console.log(`Fake SmartThings on http://localhost:${port} (${devices.length} devices)`))
