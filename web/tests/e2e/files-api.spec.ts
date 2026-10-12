import { crc32, deflateSync } from 'node:zlib'
import { expect, test, type APIRequestContext } from '@playwright/test'
import { completeSetup, mutationHeaders, resetApp } from './fixtures'

// Image uploads against the real container (Task 1.1-A): SkiaSharp's native library must load in the chiseled runtime
// image, which the .NET integration tests (SDK image) cannot prove. API only, no pages: desktop project only.

const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])

function chunk(type: string, data: Buffer): Buffer {
  const length = Buffer.alloc(4)
  length.writeUInt32BE(data.length)
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data])
  const crc = Buffer.alloc(4)
  crc.writeUInt32BE(crc32(body))
  return Buffer.concat([length, body, crc])
}

/** An 8-bit RGB PNG: the left half red, the right half blue. */
function png(width: number, height: number): Buffer {
  const header = Buffer.alloc(13)
  header.writeUInt32BE(width, 0)
  header.writeUInt32BE(height, 4)
  header[8] = 8 // bit depth
  header[9] = 2 // RGB
  const row = Buffer.alloc(1 + width * 3) // filter byte 0 (none), then pixels
  for (let x = 0; x < width; x++) row[1 + x * 3 + (x < width / 2 ? 0 : 2)] = 255
  const pixels = Buffer.concat(Array.from({ length: height }, () => row))
  return Buffer.concat([
    PNG_SIGNATURE,
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(pixels)),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

async function upload(request: APIRequestContext, name: string, mimeType: string, buffer: Buffer) {
  return request.post('/api/files', {
    headers: mutationHeaders(),
    multipart: { file: { name, mimeType, buffer }, purpose: 'floor-background' },
  })
}

test.beforeEach(async ({ request }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'API only: one project is enough.')
  await resetApp(request)
  await completeSetup(request)
})

test('a PNG is stored as WebP, scaled to 4096 px, and served with immutable caching', async ({ request }) => {
  const response = await upload(request, 'plan.png', 'image/png', png(5000, 100))
  expect(response.status(), await response.text()).toBe(201)
  const file = (await response.json()) as {
    id: string
    contentType: string
    widthPx: number
    heightPx: number
  }
  expect(file).toMatchObject({ contentType: 'image/webp', widthPx: 4096, heightPx: 82 })

  const download = await request.get(`/api/files/${file.id}`)
  expect(download.status()).toBe(200)
  expect(download.headers()['content-type']).toBe('image/webp')
  expect(download.headers()['cache-control']).toBe('private, max-age=31536000, immutable')
  expect(download.headers()['x-content-type-options']).toBe('nosniff')
  const body = await download.body()
  expect(body.subarray(0, 4).toString('ascii')).toBe('RIFF')
  expect(body.subarray(8, 12).toString('ascii')).toBe('WEBP')
})

test('SVG, HTML disguised as PNG and corrupt images are rejected', async ({ request }) => {
  const svg = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>')
  const html = Buffer.from('<!doctype html><script>alert(document.cookie)</script>')
  const corrupt = Buffer.concat([PNG_SIGNATURE, Buffer.from('not really a png')])

  for (const [name, mimeType, buffer, code] of [
    ['plan.svg', 'image/svg+xml', svg, 'files.unsupported_type'],
    ['plan.png', 'image/png', html, 'files.unsupported_type'],
    ['broken.png', 'image/png', corrupt, 'files.invalid_image'],
  ] as const) {
    const response = await upload(request, name, mimeType, buffer)
    expect(response.status(), name).toBe(400)
    expect(((await response.json()) as { code: string }).code, name).toBe(code)
  }
})
