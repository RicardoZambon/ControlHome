# @controlhome/ui

The design system: color tokens, the Tailwind theme and the shadcn-vue primitives (built on Reka UI).

```ts
import { Button, Card, cn } from '@controlhome/ui'
```

```css
/* apps/<app>/src/styles/main.css */
@import '@controlhome/ui/styles.css';
@source '../../../../packages/ui/src';
```

## Colors

- `src/styles/tokens.css` is the only file that defines colors (OKLCH, `:root` = light, `.dark` = dark).
- `src/styles/theme.css` removes Tailwind's default palette and maps each token to a utility
  (`bg-primary`, `text-muted-foreground`, `bg-state-on text-state-on-foreground`, ...).
- `src/styles/tokens.test.ts` checks that every token exists in both themes, stays inside the sRGB gamut,
  and meets WCAG AA: 4.5:1 for each `--x` / `--x-foreground` pair and for muted, primary and destructive text on
  the page and card surfaces; 3:1 for input borders, focus rings and state colors on those surfaces.
- `npm run lint` rejects unknown classes (`text-red-500`) and arbitrary colors (`bg-[#fff]`);
  `npm run check:colors` rejects color literals in `.vue`, `.ts` and `.css` files.

Device-state colors: `state-on` (green), `state-off` (grey), `state-offline` (slate), `state-warn` (orange),
`state-energy` (amber), `state-danger` (red), `state-info` (blue). Each has a `-foreground` for text on it.

## Adding a shadcn-vue component

Run from this folder (`web/packages/ui`):

```bash
npx --yes shadcn-vue@2.8.2 add <name> --yes
```

Then:

1. Replace colors that bypass the semantic pairs (`text-white` on a fill, `dark:bg-destructive/60`, ...) with the
   token classes (`text-destructive-foreground`, ...), then run `npm run lint` and `npm run check:colors` from
   `web/`. `black`/`white` exist only for overlays and similar effects.
2. The CLI installs dependencies at their latest version with `^` ranges. Pin them to exact versions in
   `package.json` (pick a release older than seven days) and run `npm install` from `web/`.
3. Export the component from `src/index.ts` and run `npx prettier --write .` from `web/`.

Button deviates from upstream on purpose: its focus ring is a solid `ring-ring` with an offset (instead of
`ring-ring/50`) so it keeps 3:1 contrast, and the destructive variant uses `text-destructive-foreground`.
