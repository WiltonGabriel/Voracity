# Credits & Third-Party Asset Attributions — Voracity

All visual and typographic assets bundled with **Voracity** are free and open-source or permissively licensed.

## 1. Typography (SIL Open Font License 1.1)

| Font Family | Files | Designer / Author | License | Source |
| :--- | :--- | :--- | :--- | :--- |
| **Cinzel** | `src/Voracity.Client/Assets/Fonts/Cinzel-Variable.ttf` | Natanael Gama | SIL OFL 1.1 | [Google Fonts — Cinzel](https://fonts.google.com/specimen/Cinzel) |
| **Inter** | `src/Voracity.Client/Assets/Fonts/Inter-Variable.ttf` | Rasmus Andersson | SIL OFL 1.1 | [rsms.me/inter](https://rsms.me/inter/) / [Google Fonts — Inter](https://fonts.google.com/specimen/Inter) |

---

## 2. UI Control Icons — Lucide (ISC License)

The vector UI icons under `src/Voracity.Client/Assets/Icons/Ui/` are from **Lucide Icons** (`https://lucide.dev`), licensed under the **ISC License** (derived in part from Feather Icons, MIT License):

- `play.svg`, `pause.svg`, `settings.svg`, `x.svg`, `chevron-down.svg`, `chevron-right.svg`, `lock.svg`, `unlock.svg`, `search.svg`, `filter.svg`, `map.svg`, `layers.svg`, `menu.svg`, `save.svg`, `triangle-alert.svg`, `plus.svg`, `info.svg`, `bug.svg`.
- **Adaptation note:** `stroke="currentColor"` was replaced with `stroke="#ffffff"` for native Godot 4 vector import and dynamic runtime tinting (`SelfModulate`).

```text
ISC License

Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2026 as part of Feather (MIT).
All other copyright (c) for Lucide are held by Lucide Contributors.

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.
```

---

## 3. Domain & Strategy Icons — Game-Icons.net (CC BY 3.0)

The grand-strategy & domain SVG icons under `src/Voracity.Client/Assets/Icons/Domain/` are sourced from [Game-Icons.net](https://game-icons.net/) (`https://github.com/game-icons/icons`) and are licensed under [Creative Commons Attribution 3.0 Unported (CC BY 3.0)](https://creativecommons.org/licenses/by/3.0/).

- **Adaptation note:** The solid black rectangular background `<path d="M0 0h512v512H0z"/>` was removed and explicit `width="64" height="64"` attributes were added alongside `viewBox="0 0 512 512"` so each icon renders with a transparent background and can be tinted cleanly in Godot 4.

### Individual Author Attributions (CC BY 3.0)

| File (`src/Voracity.Client/Assets/Icons/Domain/`) | Original Icon | Author |
| :--- | :--- | :--- |
| `crown.svg` | Crown (`lorc/crown.svg`) | **Lorc** ([game-icons.net](https://game-icons.net/)) |
| `wheat.svg` | Wheat (`lorc/wheat.svg`) | **Lorc** |
| `scales.svg` | Scales (`lorc/scales.svg`) | **Lorc** |
| `compass.svg` | Compass (`lorc/compass.svg`) | **Lorc** |
| `anvil.svg` | Anvil (`lorc/anvil.svg`) | **Lorc** |
| `scroll.svg` | Scroll Unfurled (`lorc/scroll-unfurled.svg`) | **Lorc** |
| `mountains.svg` | Mountains (`lorc/mountains.svg`) | **Lorc** |
| `galleon.svg` | Galleon (`lorc/galleon.svg`) | **Lorc** |
| `population.svg` | Two Shadows (`lorc/two-shadows.svg`) | **Lorc** |
| `mining.svg` | Mining (`lorc/mining.svg`) | **Lorc** |
| `laurel-crown.svg` | Laurel Crown (`lorc/laurel-crown.svg`) | **Lorc** |
| `coins.svg` | Coins (`delapouite/coins.svg`) | **Delapouite** ([game-icons.net](https://game-icons.net/)) |
| `castle.svg` | Castle (`delapouite/castle.svg`) | **Delapouite** |
| `road.svg` | Road (`delapouite/road.svg`) | **Delapouite** |
| `village.svg` | Village (`delapouite/village.svg`) | **Delapouite** |
| `shield.svg` | Shield (`sbed/shield.svg`) | **sbed** ([game-icons.net](https://game-icons.net/)) |
