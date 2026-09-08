# Icons

Source: [Lucide](https://lucide.dev/icons/) — ISC License, Copyright (c) for
portions of Lucide are held by Cole Bemis 2013-2022 as part of Feather (MIT).

The `.svg` files here are the unmodified originals kept for reference. WPF has
no native SVG renderer, so each icon is hand-converted into a `PathGeometry`
in [`../Icons.xaml`](../Icons.xaml) — the conversion only rewrites the SVG path
data into absolute commands and expands `<circle>` / `<rect>` / `<polyline>`
elements into equivalent path segments. Coordinates stay on the original
24x24 canvas.

| File                | Used for  | Resource key         |
| ------------------- | --------- | -------------------- |
| `shopping-cart.svg` | Kassa     | `Icon.Kassa`         |
| `package.svg`       | Tuotteet  | `Icon.Tuotteet`      |
| `banknote.svg`      | Myynti    | `Icon.Myynti`        |
| `chart-column.svg`  | Raportit  | `Icon.Raportit`      |
