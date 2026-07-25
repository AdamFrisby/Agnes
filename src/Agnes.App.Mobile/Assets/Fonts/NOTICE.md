# Bundled fonts

The Multitudal design system specifies these three families (as documented substitutes for the brand's
own unprovided wordmark font). They're embedded rather than requested from the system so typography is
identical on every device — Android OEMs ship wildly different default sans faces.

| File | Family | Licence |
| --- | --- | --- |
| `SpaceGrotesk-Bold.ttf` | Space Grotesk — display / headings | SIL Open Font License 1.1 |
| `Manrope-Regular.ttf`, `Manrope-SemiBold.ttf`, `Manrope-Bold.ttf` | Manrope — UI / body | SIL Open Font License 1.1 |
| `JetBrainsMono-Regular.ttf` | JetBrains Mono — code, logs, paths | SIL Open Font License 1.1 |

All three are OFL 1.1, which permits bundling in an application.

- Space Grotesk — © 2018 Florian Karsten, <https://github.com/floriankarsten/space-grotesk>
- Manrope — © 2018 Mikhail Sharanda, <https://github.com/sharanda/manrope>
- JetBrains Mono — © 2020 JetBrains, <https://github.com/JetBrains/JetBrainsMono>

**Note on the Manrope and Space Grotesk files:** these are the Latin-subset static instances Google
Fonts serves, whose `name` tables inherit the parent variable font's default-instance naming (every
weight declares the family as e.g. "Manrope ExtraLight"). Avalonia matches embedded fonts by family
name, so the name and OS/2 weight records were rewritten to declare a single `Manrope` / `Space Grotesk`
family with correct subfamilies. Outlines are untouched.
