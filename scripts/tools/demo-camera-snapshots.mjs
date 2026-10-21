// Draws the four synthetic demo-camera scenes (no real footage) as 640x360 PNGs.
// Usage (from the repository root): node scripts/tools/demo-camera-snapshots.mjs src/ControlHome.Connectors.Simulated/Resources
import { writeFileSync, mkdirSync } from 'node:fs'
import { join } from 'node:path'
import zlib from 'node:zlib'

const W = 640
const H = 360

function canvas() {
  const px = new Uint8Array(W * H * 3)
  const set = (x, y, [r, g, b], a = 1) => {
    x = Math.round(x); y = Math.round(y)
    if (x < 0 || y < 0 || x >= W || y >= H) return
    const i = (y * W + x) * 3
    px[i] = Math.round(px[i] * (1 - a) + r * a)
    px[i + 1] = Math.round(px[i + 1] * (1 - a) + g * a)
    px[i + 2] = Math.round(px[i + 2] * (1 - a) + b * a)
  }
  const rect = (x0, y0, x1, y1, c, a = 1) => {
    for (let y = Math.max(0, Math.floor(y0)); y < Math.min(H, Math.ceil(y1)); y++)
      for (let x = Math.max(0, Math.floor(x0)); x < Math.min(W, Math.ceil(x1)); x++) set(x, y, c, a)
  }
  const vgrad = (y0, y1, c0, c1) => {
    for (let y = y0; y < y1; y++) {
      const t = (y - y0) / Math.max(1, y1 - y0 - 1)
      const c = c0.map((v, k) => Math.round(v + (c1[k] - v) * t))
      rect(0, y, W, y + 1, c)
    }
  }
  const poly = (pts, c, a = 1) => {
    const ys = pts.map((p) => p[1])
    for (let y = Math.max(0, Math.floor(Math.min(...ys))); y <= Math.min(H - 1, Math.ceil(Math.max(...ys))); y++) {
      const xs = []
      for (let i = 0; i < pts.length; i++) {
        const [x1, y1] = pts[i]
        const [x2, y2] = pts[(i + 1) % pts.length]
        if ((y1 <= y && y2 > y) || (y2 <= y && y1 > y)) xs.push(x1 + ((y - y1) * (x2 - x1)) / (y2 - y1))
      }
      xs.sort((a, b) => a - b)
      for (let k = 0; k + 1 < xs.length; k += 2) for (let x = Math.ceil(xs[k]); x <= Math.floor(xs[k + 1]); x++) set(x, y, c, a)
    }
  }
  const circle = (cx, cy, r, c, a = 1) => {
    for (let y = Math.floor(cy - r); y <= Math.ceil(cy + r); y++)
      for (let x = Math.floor(cx - r); x <= Math.ceil(cx + r); x++)
        if ((x - cx) ** 2 + (y - cy) ** 2 <= r * r) set(x, y, c, a)
  }
  return { px, set, rect, vgrad, poly, circle }
}

// 5x7 bitmap font (uppercase, digits, a few symbols).
const FONT = {
  A: ['01110', '10001', '10001', '11111', '10001', '10001', '10001'],
  B: ['11110', '10001', '10001', '11110', '10001', '10001', '11110'],
  C: ['01111', '10000', '10000', '10000', '10000', '10000', '01111'],
  D: ['11110', '10001', '10001', '10001', '10001', '10001', '11110'],
  E: ['11111', '10000', '10000', '11110', '10000', '10000', '11111'],
  F: ['11111', '10000', '10000', '11110', '10000', '10000', '10000'],
  G: ['01111', '10000', '10000', '10011', '10001', '10001', '01111'],
  H: ['10001', '10001', '10001', '11111', '10001', '10001', '10001'],
  I: ['11111', '00100', '00100', '00100', '00100', '00100', '11111'],
  K: ['10001', '10010', '10100', '11000', '10100', '10010', '10001'],
  M: ['10001', '11011', '10101', '10101', '10001', '10001', '10001'],
  N: ['10001', '11001', '10101', '10011', '10001', '10001', '10001'],
  O: ['01110', '10001', '10001', '10001', '10001', '10001', '01110'],
  P: ['11110', '10001', '10001', '11110', '10000', '10000', '10000'],
  R: ['11110', '10001', '10001', '11110', '10100', '10010', '10001'],
  S: ['01111', '10000', '10000', '01110', '00001', '00001', '11110'],
  T: ['11111', '00100', '00100', '00100', '00100', '00100', '00100'],
  U: ['10001', '10001', '10001', '10001', '10001', '10001', '01110'],
  Y: ['10001', '10001', '01010', '00100', '00100', '00100', '00100'],
  0: ['01110', '10011', '10101', '10101', '10101', '11001', '01110'],
  1: ['00100', '01100', '00100', '00100', '00100', '00100', '01110'],
  2: ['01110', '10001', '00001', '00010', '00100', '01000', '11111'],
  3: ['11110', '00001', '00001', '01110', '00001', '00001', '11110'],
  4: ['00010', '00110', '01010', '10010', '11111', '00010', '00010'],
  5: ['11111', '10000', '11110', '00001', '00001', '10001', '01110'],
  6: ['01110', '10000', '10000', '11110', '10001', '10001', '01110'],
  7: ['11111', '00001', '00010', '00100', '01000', '01000', '01000'],
  8: ['01110', '10001', '10001', '01110', '10001', '10001', '01110'],
  9: ['01110', '10001', '10001', '01111', '00001', '00001', '01110'],
  '-': ['00000', '00000', '00000', '11111', '00000', '00000', '00000'],
  ':': ['00000', '00100', '00100', '00000', '00100', '00100', '00000'],
  ' ': ['00000', '00000', '00000', '00000', '00000', '00000', '00000'],
}

function text(c, x, y, s, color, scale = 2) {
  for (const ch of s) {
    const glyph = FONT[ch] ?? FONT[' ']
    glyph.forEach((row, gy) => [...row].forEach((bit, gx) => {
      if (bit === '1') c.rect(x + gx * scale, y + gy * scale, x + (gx + 1) * scale, y + (gy + 1) * scale, color)
    }))
    x += 6 * scale
  }
}

function overlay(c, label, time) {
  c.rect(0, 0, W, 26, [0, 0, 0], 0.55)
  text(c, 8, 6, label, [255, 255, 255])
  text(c, W - 8 - time.length * 12, 6, time, [255, 255, 255])
  c.circle(W - 8 - time.length * 12 - 14, 13, 5, [220, 40, 40])
}

function tree(c, x, ground, h) {
  c.rect(x - 5, ground - h * 0.45, x + 5, ground, [92, 64, 40])
  c.circle(x, ground - h * 0.6, h * 0.32, [46, 112, 52])
  c.circle(x - h * 0.18, ground - h * 0.5, h * 0.22, [56, 128, 60])
  c.circle(x + h * 0.2, ground - h * 0.52, h * 0.24, [40, 100, 48])
}

function gate() {
  const c = canvas()
  c.vgrad(0, 200, [150, 196, 232], [214, 232, 244])
  tree(c, 520, 200, 150)
  tree(c, 80, 200, 120)
  c.rect(0, 200, W, H, [120, 122, 126])
  c.poly([[180, H], [460, H], [380, 200], [260, 200]], [150, 150, 152])
  // wall and pillars
  c.rect(0, 120, 140, 230, [222, 214, 196]); c.rect(500, 120, W, 230, [222, 214, 196])
  c.rect(130, 100, 160, 235, [200, 190, 170]); c.rect(480, 100, 510, 235, [200, 190, 170])
  // sliding gate bars
  c.rect(160, 120, 480, 128, [60, 62, 66]); c.rect(160, 222, 480, 230, [60, 62, 66])
  for (let x = 166; x < 480; x += 16) c.rect(x, 120, x + 6, 230, [70, 72, 78])
  c.rect(290, 170, 350, 176, [70, 72, 78])
  overlay(c, 'CAM 1 GATE', '2026-10-21 14:32:07')
  return c
}

function garage() {
  const c = canvas()
  c.vgrad(0, 210, [214, 206, 188], [196, 188, 170])
  c.vgrad(210, H, [150, 150, 150], [110, 110, 112])
  // shelves
  c.rect(30, 70, 150, 200, [130, 100, 70]); for (let y = 90; y < 200; y += 36) c.rect(30, y, 150, y + 5, [96, 72, 50])
  c.rect(45, 66, 75, 88, [200, 60, 50]); c.rect(85, 102, 125, 124, [60, 110, 170]); c.rect(50, 140, 80, 160, [220, 180, 60])
  // door frame
  c.rect(520, 60, 610, 210, [170, 160, 140]); c.rect(530, 70, 600, 210, [140, 104, 72]); c.circle(590, 140, 4, [230, 200, 90])
  // car
  c.poly([[200, 280], [210, 230], [270, 190], [400, 190], [450, 230], [480, 240], [490, 280]], [52, 92, 150])
  c.poly([[280, 198], [390, 198], [430, 232], [250, 232]], [180, 210, 230])
  c.rect(338, 196, 344, 232, [52, 92, 150])
  c.circle(260, 284, 24, [30, 30, 32]); c.circle(260, 284, 10, [150, 150, 150])
  c.circle(430, 284, 24, [30, 30, 32]); c.circle(430, 284, 10, [150, 150, 150])
  c.rect(470, 250, 486, 262, [250, 230, 160])
  overlay(c, 'CAM 2 GARAGE', '2026-10-21 14:32:07')
  return c
}

function backyard() {
  const c = canvas()
  c.vgrad(0, 160, [130, 184, 228], [200, 226, 242])
  tree(c, 120, 170, 160)
  tree(c, 560, 165, 130)
  // fence
  c.rect(0, 110, W, 170, [168, 128, 88])
  for (let x = 0; x < W; x += 22) c.rect(x, 110, x + 3, 170, [140, 104, 70])
  c.rect(0, 128, W, 132, [140, 104, 70]); c.rect(0, 152, W, 156, [140, 104, 70])
  c.vgrad(170, H, [96, 160, 72], [70, 132, 54])
  // pool
  c.poly([[230, 230], [520, 230], [580, 330], [170, 330]], [236, 236, 230])
  c.poly([[245, 240], [505, 240], [560, 320], [190, 320]], [70, 170, 210])
  for (let y = 252; y < 318; y += 14) c.rect(260, y, 520, y + 2, [130, 200, 230], 0.6)
  // lounger
  c.poly([[40, 300], [150, 280], [160, 300], [50, 322]], [240, 240, 240])
  overlay(c, 'CAM 3 BACKYARD', '2026-10-21 14:32:07')
  return c
}

function porch() {
  const c = canvas()
  c.vgrad(0, 120, [150, 196, 232], [210, 230, 244])
  // facade and roof
  c.rect(0, 80, W, 300, [236, 226, 206])
  c.poly([[-20, 90], [W + 20, 90], [W - 40, 40], [40, 40]], [150, 70, 56])
  // window
  c.rect(60, 120, 200, 220, [110, 90, 70]); c.rect(68, 128, 192, 212, [170, 200, 220]); c.rect(128, 128, 132, 212, [110, 90, 70])
  // door
  c.rect(270, 110, 380, 290, [120, 80, 50]); c.rect(282, 124, 368, 200, [104, 68, 42]); c.circle(362, 210, 4, [230, 200, 90])
  c.rect(392, 160, 404, 180, [240, 230, 160])
  // lawn, steps and mat
  c.vgrad(300, H, [104, 166, 78], [76, 138, 58])
  c.rect(240, 290, 410, 310, [190, 186, 180]); c.rect(220, 310, 430, H, [170, 166, 160])
  c.rect(285, 292, 365, 306, [150, 110, 60])
  // plant pots
  c.rect(440, 250, 480, 300, [170, 90, 60]); c.circle(460, 235, 26, [60, 130, 60])
  c.rect(170, 260, 205, 300, [170, 90, 60]); c.circle(187, 246, 22, [70, 140, 64])
  overlay(c, 'CAM 4 FRONT PORCH', '2026-10-21 14:32:07')
  return c
}

function png({ px }) {
  const raw = Buffer.alloc((W * 3 + 1) * H)
  for (let y = 0; y < H; y++) {
    raw[y * (W * 3 + 1)] = 0
    Buffer.from(px.buffer, y * W * 3, W * 3).copy(raw, y * (W * 3 + 1) + 1)
  }
  const chunk = (type, data) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length)
    const td = Buffer.concat([Buffer.from(type, 'ascii'), data])
    const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(td) >>> 0)
    return Buffer.concat([len, td, crc])
  }
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(W, 0); ihdr.writeUInt32BE(H, 4); ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

const out = process.argv[2]
mkdirSync(out, { recursive: true })
;[gate, garage, backyard, porch].forEach((scene, i) => {
  const file = join(out, `camera-${i + 1}.png`)
  const data = png(scene())
  writeFileSync(file, data)
  console.log(file, data.length)
})
