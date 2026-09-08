# Icons

Source: [Lucide](https://lucide.dev/icons/) — ISC License, Copyright (c) for
portions of Lucide are held by Cole Bemis 2013-2022 as part of Feather (MIT).

The `.svg` files here are the unmodified originals kept for reference. WPF has
no native SVG renderer, so each icon is hand-converted into a `PathGeometry`
in [`../Icons.xaml`](../Icons.xaml) — the conversion only rewrites the SVG path
data into absolute commands and expands `<circle>` / `<rect>` / `<polyline>`
elements into equivalent path segments. Coordinates stay on the original
24x24 canvas.

Resource keys are English; the Finnish text is what the user sees on screen.

| File                | Resource key       | Shown as   |
| ------------------- | ------------------ | ---------- |
| `shopping-cart.svg` | `Icon.Checkout`    | Kassa      |
| `package.svg`       | `Icon.Products`    | Tuotteet   |
| `banknote.svg`      | `Icon.Sales`       | Myynti     |
| `chart-column.svg`  | `Icon.Reports`     | Raportit   |
| `search.svg`        | `Icon.Search`      | —          |
| `plus.svg`          | `Icon.Plus`        | —          |
| `minus.svg`         | `Icon.Minus`       | —          |
| `trash.svg`         | `Icon.Trash`       | —          |
| `arrow-left.svg`    | `Icon.ArrowLeft`   | —          |
| `credit-card.svg`   | `Icon.CreditCard`  | —          |
| `x.svg`             | `Icon.X`           | —          |
