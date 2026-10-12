import { describe, expect, it } from 'vitest'
import { findColorLiterals } from './check-colors.mjs'

const literals = (fileName, text) => findColorLiterals(fileName, text).map((f) => f.literal)

describe('findColorLiterals', () => {
  it.each([
    ['a.css', '.x { color: #ff0000; }', '#ff0000'],
    ['a.css', '.x { background: rgb(0 0 0 / 50%); }', 'rgb('],
    ['a.css', '.x { border-color: oklch(0.5 0.1 200); }', 'oklch('],
    ['a.css', '.x { color: color-mix(in oklab, var(--a), var(--b)); }', 'color-mix('],
    ['a.css', '.x { color: color(display-p3 1 0 0); }', 'color('],
    ['a.css', '.x { border: 1px solid red; }', 'border: red'],
    ['a.ts', "const c = '#abc'", '#abc'],
    ['a.ts', "const c = 'hsla(0, 0%, 0%, 0.5)'", 'hsla('],
    ['a.ts', "const style = { color: 'white' }", 'color: white'],
    ['a.ts', "const s = 'box-shadow: 0 0 2px #000c'", '#000c'],
    ['a.vue', '<template><div class="bg-[#123456]" /></template>', '#123456'],
    ['a.vue', '<template><div style="color: #f00" /></template>', '#f00'],
    ['a.vue', '<template><svg><path fill="tomato" /></svg></template>', 'fill="tomato"'],
    ['a.vue', '<template><div :style="{ backgroundColor: \'navy\' }" /></template>', 'backgroundColor: navy'],
    ['a.vue', '<style scoped>\n.x { color: #fff; }\n</style>', '#fff'],
  ])('flags %s: %s', (file, text, expected) => {
    expect(literals(file, text)).toContain(expected)
  })

  it.each([
    ['a.css', '.x { color: var(--primary); --color-black: var(--black); }'],
    ['a.css', '/* #fff and rgb(0 0 0) in a comment */'],
    ['a.css', '.x { fill: currentColor; stroke: none; background: transparent; }'],
    ['a.ts', 'class A { #cache = 1; read() { return this.#cache } }'],
    ['a.ts', '// see issue #123\nconst a = 1'],
    ['a.ts', 'const tone = { color: Tone.Red }'],
    ['a.ts', 'const v = getColor(x) + toLinearSrgb(y) + parseOklch(z)'],
    ['a.ts', "const url = 'https://example.test/docs#abc'"],
    ['a.vue', '<template><a href="#add">x</a><span>&#123;</span></template>'],
    ['a.vue', '<template><svg><path fill="currentColor" stroke="none" /></svg></template>'],
    ['a.vue', '<template><!-- #fff --><div class="bg-primary text-primary-foreground" /></template>'],
    ['a.css', '.x { color: #fff; } /* color-ok */'],
  ])('ignores %s: %s', (file, text) => {
    expect(literals(file, text)).toEqual([])
  })

  it('reports 1-based line numbers inside single-file components', () => {
    const sfc = [
      '<script setup lang="ts">',
      "const a = 'rgb(1 2 3)'",
      '</script>',
      '',
      '<template>',
      '  <div',
      '    style="color: #abcdef"',
      '  />',
      '</template>',
    ].join('\n')
    expect(findColorLiterals('a.vue', sfc)).toEqual([
      { line: 2, literal: 'rgb(' },
      { line: 7, literal: '#abcdef' },
    ])
  })
})
