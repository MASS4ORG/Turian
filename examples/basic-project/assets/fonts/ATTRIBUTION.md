# Font attribution

All fonts in this folder are licensed under the [SIL Open Font License,
Version 1.1](./OFL.txt), verified from the copyright/license strings embedded
in each font file's own `name` table (checked before including them here —
this repo does not bundle fonts of unknown license).

| File | Family | Author | Copyright | Source |
|------|--------|--------|-----------|--------|
| `Lora-Regular.ttf` | Lora | Olga Karpushina, Alexei Vanyashin (Cyreal) | (c) 2011-2013 Cyreal (www.cyreal.org) | Reserved Font Name "Lora" |
| `BebasNeue-Regular.ttf` | Bebas Neue | Ryoichi Tsunekawa (Dharma Type) | (c) 2010 Dharma Type | http://dharmatype.com |
| `Inconsolata.otf` | Inconsolata | Raph Levien | (c) 2006 Raph Levien | http://scripts.sil.org/OFL |
| `NotoSansJP-Regular.ttf` | Noto Sans JP | Adobe / Ryoko Nishizuka (production & ideograph elements) | (c) 2014-2021 Adobe, Reserved Font Name "Source" | https://fonts.google.com/noto |
| `NotoSansArabic-Regular.ttf` | Noto Sans Arabic | Monotype Design Team, Nadine Chahine, Nizar Qandah, Khaled Hosny | (c) 2022 The Noto Project Authors | https://github.com/notofonts/arabic |

Picked for visual contrast in the Font asset demo: a serif body face (Lora), a
bold condensed display face (Bebas Neue), and a monospace face (Inconsolata).
Compare against Turian Studio's built-in dvui default (Bitstream Vera,
already embedded — no asset needed) in the Font asset's Inspector preview.

The two Noto fonts are staged **for preparation only** — CJK glyph rendering
and RTL bidi text layout in Guinevein (dvui) are unverified; see issue #118.
Don't expect Japanese/Arabic sample text to render correctly yet. Both are
variable fonts (multiple weights baked into one file, `NotoSansJP` in
particular is ~9.4 MB) — worth moving to Git LFS if more of these get added.
