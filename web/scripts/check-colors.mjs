#!/usr/bin/env node
// Fails when a color literal appears outside the design tokens (packages/ui/src/styles/tokens.css).
// Colors must come from tokens through semantic Tailwind classes (bg-primary, text-state-on, ...).
//
// Detects, in packages/*/src and apps/*/src (*.vue, *.ts, *.css):
//   - hex colors (#fff, #ffffff80) in CSS, <style>, template attributes and script strings
//   - color functions: rgb(), rgba(), hsl(), hsla(), hwb(), lab(), lch(), oklab(), oklch(), color-mix(), color(<space> ...)
//   - CSS named colors (red, white, ...) in color declarations, style objects and SVG paint attributes
//   - Tailwind black/white utilities (text-white, bg-black/80, hover:border-white, ...): they bypass the
//     semantic pairs (use text-primary-foreground, bg-overlay, bg-background, ...)
// A line that contains the marker `color-ok` (e.g. `/* color-ok */`) is skipped.
//
// Usage: node scripts/check-colors.mjs            (exit 1 and file:line list on violations)

import { readFileSync, readdirSync } from 'node:fs'
import { join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = fileURLToPath(new URL('..', import.meta.url))
const SCAN_ROOTS = ['packages', 'apps']
const EXTENSIONS = new Set(['.vue', '.ts', '.css'])
const ALLOWED_FILES = new Set(['packages/ui/src/styles/tokens.css'])
const SKIPPED_DIRS = new Set(['node_modules', 'dist', 'coverage', 'generated'])
const ALLOW_MARKER = 'color-ok'

const HEX = /(?<![\w&.#/\\-])#(?:[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{3,4})(?![\w-])/gi
const COLOR_FUNCTION =
  /(?<![\w$.-])(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch|color-mix|color(?=\(\s*(?:srgb|srgb-linear|display-p3|a98-rgb|prophoto-rgb|rec2020|xyz|xyz-d50|xyz-d65)\b))\s*\(/gi

const NAMED_COLORS = new Set(
  (
    'aliceblue antiquewhite aqua aquamarine azure beige bisque black blanchedalmond blue blueviolet brown ' +
    'burlywood cadetblue chartreuse chocolate coral cornflowerblue cornsilk crimson cyan darkblue darkcyan ' +
    'darkgoldenrod darkgray darkgreen darkgrey darkkhaki darkmagenta darkolivegreen darkorange darkorchid ' +
    'darkred darksalmon darkseagreen darkslateblue darkslategray darkslategrey darkturquoise darkviolet ' +
    'deeppink deepskyblue dimgray dimgrey dodgerblue firebrick floralwhite forestgreen fuchsia gainsboro ' +
    'ghostwhite gold goldenrod gray green greenyellow grey honeydew hotpink indianred indigo ivory khaki ' +
    'lavender lavenderblush lawngreen lemonchiffon lightblue lightcoral lightcyan lightgoldenrodyellow ' +
    'lightgray lightgreen lightgrey lightpink lightsalmon lightseagreen lightskyblue lightslategray ' +
    'lightslategrey lightsteelblue lightyellow lime limegreen linen magenta maroon mediumaquamarine ' +
    'mediumblue mediumorchid mediumpurple mediumseagreen mediumslateblue mediumspringgreen mediumturquoise ' +
    'mediumvioletred midnightblue mintcream mistyrose moccasin navajowhite navy oldlace olive olivedrab ' +
    'orange orangered orchid palegoldenrod palegreen paleturquoise palevioletred papayawhip peachpuff peru ' +
    'pink plum powderblue purple rebeccapurple red rosybrown royalblue saddlebrown salmon sandybrown ' +
    'seagreen seashell sienna silver skyblue slateblue slategray slategrey snow springgreen steelblue tan ' +
    'teal thistle tomato turquoise violet wheat white whitesmoke yellow yellowgreen'
  ).split(' '),
)

// Properties whose value is (or contains) a color.
const COLOR_PROPERTY =
  'color|background(?:-color)?|border(?:-(?:top|right|bottom|left|block|inline)(?:-(?:start|end))?)?(?:-color)?|' +
  'outline(?:-color)?|fill|stroke|stop-color|flood-color|lighting-color|caret-color|accent-color|' +
  'text-decoration(?:-color)?|column-rule(?:-color)?|box-shadow|text-shadow|backgroundColor|borderColor|' +
  'stopColor|floodColor|caretColor|accentColor|textDecorationColor|boxShadow|textShadow'
// CSS declaration (`color: red`) or style-object entry (`color: 'red'`). Custom properties are skipped.
// In scripts only quoted values count: `color: Tone.Red` is code, `color: 'red'` is a literal.
const COLOR_DECLARATION = new RegExp(
  `(?<![\\w-])(?:${COLOR_PROPERTY})\\s*:\\s*(['"\`]?)([^;{}'"\`]+)\\1`,
  'g',
)
// Static SVG paint attributes in templates: fill="red". Bound attributes (:fill) are runtime values.
const PAINT_ATTRIBUTE =
  /(?<![\w:@.-])(?:fill|stroke|stop-color|flood-color|lighting-color|color)\s*=\s*"([^"]*)"/g
// Template attributes whose `#...` value is a URL fragment, not a color.
const FRAGMENT_ATTRIBUTE = /(?:href|xlink:href|to)\s*=\s*["']$/
// Tailwind utilities that take a color, used with black/white and an optional opacity (`bg-black/80`,
// `text-white/[0.9]`). Variant prefixes (`hover:`, `dark:`, `data-[x]:`) are added to the report below.
const COLOR_UTILITY =
  'bg|text|border(?:-[xytrblse])?|outline|ring|ring-offset|inset-ring|divide|fill|stroke|accent|caret|' +
  'decoration|placeholder|shadow|inset-shadow|drop-shadow|text-shadow|from|via|to'
const BLACK_WHITE_UTILITY = new RegExp(
  `(?<![\\w-])!?(?:${COLOR_UTILITY})-(?:black|white)(?:\\/(?:\\d+(?:\\.\\d+)?|\\[[^\\]\\s]+\\]|\\([^)\\s]+\\)))?!?(?![\\w-])`,
  'g',
)
// Characters that can belong to a class token, used to extend a match back over its variant prefixes.
const CLASS_TOKEN_CHAR = /[^\s'"`(),{};]/

function namedColorIn(value) {
  const words = value
    .replace(/\b(?:var|url)\([^)]*\)/gi, ' ')
    .toLowerCase()
    .split(/[^a-z-]+/)
  return words.find((word) => NAMED_COLORS.has(word))
}

/** Replaces comments with spaces, keeping line numbers and columns stable. */
function blankComments(text, kind) {
  const blank = (match) => match.replace(/[^\n]/g, ' ')
  let result = text.replace(/\/\*[\s\S]*?\*\//g, blank)
  if (kind === 'script')
    result = result.replace(/(^|[^:\\])\/\/.*$/gm, (m, lead) => lead + blank(m.slice(lead.length)))
  if (kind === 'template') result = result.replace(/<!--[\s\S]*?-->/g, blank)
  return result
}

/** Splits a source file into regions ('css' | 'template' | 'script') with their starting line. */
function regionsOf(fileName, text) {
  if (fileName.endsWith('.css')) return [{ kind: 'css', start: 0, text }]
  if (fileName.endsWith('.ts')) return [{ kind: 'script', start: 0, text }]
  const regions = []
  for (const match of text.matchAll(/<(script|style)(\s[^>]*)?>([\s\S]*?)<\/\1>/g)) {
    const offset = match.index + match[0].indexOf('>') + 1
    regions.push({ kind: match[1] === 'style' ? 'css' : 'script', offset, text: match[3] })
  }
  const template = text.match(/<template(\s[^>]*)?>([\s\S]*)<\/template>/)
  if (template) {
    const offset = template.index + template[0].indexOf('>') + 1
    regions.push({ kind: 'template', offset, text: template[2] })
  }
  return regions.map((region) => ({
    kind: region.kind,
    start: text.slice(0, region.offset).split('\n').length - 1,
    text: region.text,
  }))
}

/**
 * Returns the color literals found in one source file.
 * @param {string} fileName path used for the file type and in messages
 * @param {string} text file contents
 * @returns {{ line: number, literal: string }[]} 1-based line numbers
 */
export function findColorLiterals(fileName, text) {
  const findings = []
  for (const region of regionsOf(fileName, text)) {
    const lines = blankComments(region.text, region.kind).split('\n')
    const rawLines = region.text.split('\n')
    lines.forEach((line, index) => {
      if (rawLines[index]?.includes(ALLOW_MARKER)) return
      const report = (literal) => findings.push({ line: region.start + index + 1, literal: literal.trim() })

      for (const match of line.matchAll(HEX)) {
        if (region.kind === 'template' && FRAGMENT_ATTRIBUTE.test(line.slice(0, match.index))) continue
        report(match[0])
      }
      for (const match of line.matchAll(COLOR_FUNCTION)) report(match[0])
      for (const match of line.matchAll(BLACK_WHITE_UTILITY)) {
        let start = match.index
        while (start > 0 && CLASS_TOKEN_CHAR.test(line[start - 1])) start -= 1
        report(line.slice(start, match.index + match[0].length))
      }
      for (const match of line.matchAll(COLOR_DECLARATION)) {
        if (region.kind === 'script' && !match[1]) continue
        const named = namedColorIn(match[2])
        if (named) report(`${match[0].split(':')[0]}: ${named}`)
      }
      if (region.kind === 'template') {
        for (const match of line.matchAll(PAINT_ATTRIBUTE)) {
          const named = namedColorIn(match[1])
          if (named) report(match[0])
        }
      }
    })
  }
  return findings.sort((a, b) => a.line - b.line)
}

function* sourceFiles(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!SKIPPED_DIRS.has(entry.name)) yield* sourceFiles(join(dir, entry.name))
    } else if (EXTENSIONS.has(entry.name.slice(entry.name.lastIndexOf('.')))) {
      yield join(dir, entry.name)
    }
  }
}

function* scannedFiles() {
  for (const scanRoot of SCAN_ROOTS) {
    let packages = []
    try {
      packages = readdirSync(join(ROOT, scanRoot), { withFileTypes: true }).filter((e) => e.isDirectory())
    } catch {
      continue
    }
    for (const pkg of packages) {
      const src = join(ROOT, scanRoot, pkg.name, 'src')
      try {
        yield* sourceFiles(src)
      } catch (error) {
        if (error.code !== 'ENOENT') throw error
      }
    }
  }
}

function main() {
  let files = 0
  const violations = []
  for (const file of scannedFiles()) {
    const path = relative(ROOT, file).split(sep).join('/')
    if (ALLOWED_FILES.has(path)) continue
    files += 1
    for (const finding of findColorLiterals(path, readFileSync(file, 'utf8'))) {
      violations.push(`${path}:${finding.line}  ${finding.literal}`)
    }
  }
  if (violations.length > 0) {
    console.error(`check:colors found ${violations.length} color literal(s) outside tokens.css:`)
    for (const violation of violations) console.error(`  ${violation}`)
    console.error(
      'Use a semantic class (bg-primary, text-state-on, ...) or add a token to packages/ui/src/styles/tokens.css.',
    )
    process.exit(1)
  }
  console.log(`check:colors: ${files} files clean.`)
}

// Run when executed (`node scripts/check-colors.mjs`), not when imported by the tests.
// Compared case-insensitively: Windows paths may differ only in drive-letter case.
const invokedPath = process.argv[1] ? resolve(process.argv[1]).toLowerCase() : ''
if (invokedPath === fileURLToPath(import.meta.url).toLowerCase()) main()
