import { defineConfigWithVueTs, vueTsConfigs } from '@vue/eslint-config-typescript'
import prettier from 'eslint-config-prettier/flat'
import betterTailwindcss from 'eslint-plugin-better-tailwindcss'
import pluginVue from 'eslint-plugin-vue'
import globals from 'globals'

// Utilities whose value can carry a color (bg-[#fff], text-[rgb(...)], fill-[oklch(...)], ...).
const colorUtilities =
  'bg|text|border(?:-[xytrblse])?|outline|ring|ring-offset|divide|fill|stroke|accent|caret|decoration|placeholder|shadow|inset-shadow|drop-shadow|from|via|to'

export default defineConfigWithVueTs(
  {
    name: 'controlhome/ignores',
    ignores: ['**/dist/**', '**/coverage/**', '**/node_modules/**', '**/src/generated/**'],
  },
  pluginVue.configs['flat/recommended'],
  vueTsConfigs.recommended,
  {
    name: 'controlhome/vue',
    files: ['**/*.vue'],
    rules: {
      // Optional props are `undefined` by design with type-based defineProps.
      'vue/require-default-prop': 'off',
    },
  },
  {
    name: 'controlhome/ui-primitives',
    files: ['packages/ui/src/components/ui/**/*.vue'],
    rules: {
      // shadcn-vue primitives use single-word names (Button, Card, Input).
      'vue/multi-word-component-names': 'off',
    },
  },
  {
    name: 'controlhome/node-scripts',
    files: ['scripts/**/*.{js,mjs}', '*.config.{js,ts}', '**/vite.config.ts', '**/vitest.config.ts'],
    languageOptions: { globals: globals.node },
  },
  {
    name: 'controlhome/design-system',
    files: ['**/*.{vue,ts}'],
    plugins: { 'better-tailwindcss': betterTailwindcss },
    settings: {
      'better-tailwindcss': { entryPoint: 'packages/ui/src/styles/index.css' },
    },
    rules: {
      // Only classes that exist in our Tailwind theme: the default palette is removed in theme.css,
      // so `text-red-500` and friends are unknown here.
      'better-tailwindcss/no-unknown-classes': 'error',
      'better-tailwindcss/no-conflicting-classes': 'error',
      'better-tailwindcss/no-duplicate-classes': 'error',
      'better-tailwindcss/no-deprecated-classes': 'error',
      'better-tailwindcss/no-restricted-classes': [
        'error',
        {
          restrict: [
            {
              pattern: `^(?:[^:]*:)*!?(?:${colorUtilities})-\\[(?:color:)?(?:#|(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch|color|color-mix)\\()`,
              message:
                'Arbitrary color values are not allowed; add a token to packages/ui/src/styles/tokens.css and use its semantic class.',
            },
          ],
        },
      ],
    },
  },
  prettier,
)
