# @controlhome/ui

The design system: color tokens, the Tailwind theme, the shadcn-vue primitives (built on Reka UI) and the
theme composables.

```ts
import { Button, Card, Dialog, Toaster, toast, useTheme, cn } from '@controlhome/ui'
// Each component folder also works on its own:
import { Sidebar, SidebarProvider } from '@controlhome/ui/components/ui/sidebar'
```

```css
/* apps/<app>/src/styles/main.css */
@import '@controlhome/ui/styles.css';
@source '../../../../packages/ui/src';
```

## Colors

- `src/styles/tokens.css` is the only file that defines colors (OKLCH, `:root` = light, `.dark` = dark).
- `src/styles/theme.css` removes Tailwind's default palette and maps each token to a utility
  (`bg-primary`, `text-muted-foreground`, `bg-state-on text-state-on-foreground`, `bg-overlay`, ...). It also
  replaces Tailwind's `shadow-*`, `inset-shadow-*`, `drop-shadow-*` and `text-shadow-*` scales with the same
  geometry colored by `--shadow-color-subtle` / `--shadow-color` / `--shadow-color-strong`, so shadows follow the
  theme (they are stronger on dark surfaces).
- `src/styles/tokens.test.ts` checks that every token exists in both themes, stays inside the sRGB gamut, is opaque
  (only `--overlay` and the shadow colors are translucent), and meets WCAG AA: 4.5:1 for each `--x` / `--x-foreground`
  pair and for muted, primary and destructive text on the background, card and popover surfaces (plus muted text on
  sidebar, sidebar-accent, accent and secondary); 3:1 for input borders, focus rings, primary, state and room colors
  on those surfaces. It also checks that the room colors differ clearly from each other.
- `npm run lint` rejects unknown classes (`text-red-500`) and arbitrary colors (`bg-[#fff]`);
  `npm run check:colors` rejects color literals in `.vue`, `.ts` and `.css` files and the black/white utilities
  (`text-white`, `bg-black/80`, `hover:border-white`, ...). A line with the marker `color-ok` is skipped (only for
  tests of color code).

Device-state colors: `state-on` (green), `state-off` (grey), `state-offline` (slate), `state-warn` (orange),
`state-energy` (amber), `state-danger` (red), `state-info` (blue). Each has a `-foreground` for text on it.

Room colors: `room-1` ... `room-8` (blue, sand, sage, rose, teal, coral, olive, violet), calm low-chroma hues
45 degrees apart, ordered so neighbours differ strongly. Each has a `-foreground`. They are the categorical chart
palette too.

Effects: `bg-overlay` is the scrim behind dialogs and sheets; `shadow-sm` ... `shadow-2xl` use the shadow tokens.

## Theme

- `useTheme()` returns `{ mode, resolved, setMode }`. `mode` is `'light' | 'dark' | 'system'` (persisted in
  `localStorage['ch.theme']`, synced across tabs); `resolved` is the theme in effect. It toggles the `dark` class
  on `<html>` and follows `prefers-color-scheme` in `system` mode. One shared state for the whole app; safe without
  a DOM.
- `useThemeTokens()` returns a ref with the current value of every token (`tokens.value['state-on']`), re-read when
  the theme changes. For code that cannot use classes.
- `useChartTheme()` returns a computed ECharts theme object (room palette, text/axis/grid from foreground,
  muted-foreground and border, tooltips like popovers, state colors for visual maps). Colors are converted to
  `#rrggbb[aa]` because ECharts cannot parse OKLCH. Pass it to vue-echarts' `theme` prop.

## Components

alert, alert-dialog, avatar, badge, breadcrumb, button, card, checkbox, dialog, dropdown-menu, form, input, label,
popover, progress, radio-group, scroll-area, select, separator, sheet, sidebar, skeleton, slider, sonner (toasts),
switch, table, tabs, textarea, toggle, toggle-group, tooltip.

They deviate from upstream shadcn-vue on purpose:

- **Tokens only.** No `dark:` overrides (each token already has a dark value), no `bg-black/80` (`bg-overlay`),
  no `bg-white` / `text-white` (`bg-background`, `text-destructive-foreground`, ...), no `var(--...)` in arbitrary
  values. Destructive menu items use a solid `bg-destructive text-destructive-foreground` highlight.
- **Focus rings keep 3:1.** Every focusable primitive uses Button's ring:
  `focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background`
  (instead of `ring-ring/50`). Always pair `ring-offset-*` with a token color: Tailwind's default offset color is
  `#fff`. Invalid fields (`aria-invalid`) use `border-destructive` and a `ring-destructive` focus ring.
- **Switch:** `bg-input` track and `bg-background` thumb when off, `bg-primary` / `bg-primary-foreground` when on.
  **Tabs:** inactive `text-muted-foreground`, active `bg-background text-foreground border-border shadow-sm`.
  **Sidebar** group labels use `text-muted-foreground`; the outline menu button uses `ring-sidebar-border`.
- **Translatable strings.** Screen-reader texts have English defaults and a prop for the translation:
  `DialogContent` / `DialogScrollContent` / `SheetContent` `close-label`, `DialogFooter` `close-label`,
  `Breadcrumb` `label`, `BreadcrumbEllipsis` `label`, `SidebarTrigger` / `SidebarRail` `label`,
  `Sidebar` `mobile-title` / `mobile-description`, `Toaster` `container-aria-label` and
  `toast-options.closeButtonAriaLabel` (vue-sonner props).
- **Toasts.** Render `<Toaster />` once (it follows `useTheme()`), then call `toast(...)` imported from
  `@controlhome/ui`, so the caller shares the Toaster's vue-sonner instance. vue-sonner's stylesheet is imported in
  the `components` layer and its color variables point at the tokens (`rich-colors` uses the state colors). Do not
  use the `invert` option: its colors are hard-coded black/white.
- **Forms.** `Form`, `FormField`, `useForm`, `toTypedSchema` and `z` come from this package (vee-validate + zod), so
  apps share its pinned versions instead of adding their own.
- The sidebar cookie (`sidebar_state`) is written with `SameSite=Lax`.

## Dependency pins

Exact versions only; when adding or bumping one, pick a release older than seven days. The shadcn-vue CLI installs
`^` ranges of the latest release; always re-pin.

| Package             | Version | Why                                                                     |
| ------------------- | ------- | ----------------------------------------------------------------------- |
| `reka-ui`           | 2.11.0  | headless primitives (peer; devDependency for the package itself)        |
| `@vueuse/core`      | 14.4.0  | `useVModel`, `reactiveOmit`, media queries                              |
| `@lucide/vue`       | 1.49.0  | icons (the CLI tries `^1.55.0`)                                         |
| `vee-validate`      | 4.15.1  | form state (`Form`, `FormField`); 5.x is still beta                     |
| `@vee-validate/zod` | 4.15.1  | `toTypedSchema`; peer `zod ^3.24`                                       |
| `zod`               | 3.25.76 | schemas; newest 3.x (4.x is outside `@vee-validate/zod`'s peer range)   |
| `vue-sonner`        | 2.0.9   | toasts                                                                  |
| `tw-animate-css`    | 1.4.0   | `animate-in/out`, `fade-*`, `zoom-*`, `slide-*` used by overlays, menus |

## Adding a shadcn-vue component

Run from this folder (`web/packages/ui`):

```bash
npx --yes shadcn-vue@2.8.2 add <name> --yes
```

The CLI stops at a prompt when a file already exists (button, input, ... are dependencies of several components).
Answer no, or run `yes n | npx --yes shadcn-vue@2.8.2 add <name> --yes`; never overwrite the existing components.
Then:

1. Normalize the classes as described under Components (tokens only, Button's focus ring, label props for
   screen-reader text), then run `npm run lint` and `npm run check:colors` from `web/`.
2. Re-pin the dependencies the CLI added or changed (table above) and run `npm install` from `web/`.
3. Export the component from `src/index.ts` and run `npx prettier --write .` from `web/`.
