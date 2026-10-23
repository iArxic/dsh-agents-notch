# Design renders — agent overlay

> **Superseded by 0.1.8.** These are the round-10/11 renders, kept as the record of what was locked
> then. Round 12 replaced two of their ideas: the state **badge** is now a **ring** around the bot
> (with a spinner while it works), and the **chip column** is gone — every other session is one row
> on a scrolling list. The binary is `AgentsNotch.exe`, not `AgentOverlay.exe`. Pictures of the
> shipped notch live in `../`: `hero.png`, `island.png`, `faces.png`, `board-*.png`.

The board (the one in `V-states.png`) with the bot animated and rimmed in its session's
identity colour. Drawn in WPF — the same stack the real overlay will use — and rendered at 2×
over a simulated light desktop. Every shape, shadow and glyph is something `AgentOverlay.exe`
can draw; the motion frames come from the same `Orb()` the window will drive.

| File | What it is |
|---|---|
| `V-states.png` | **The board, in every state** (idle / working / needs you / done / failed) — the locked design. |
| `V-motion.png` | The bot's motion frames: blink, eye drift, rim levels. |
| `V-identity.png` | One hue per session: the rim is the session, the badge is its state. |
| `V1-board.png` | The board on a desktop — one session running. |
| `V2-grid.png` | The board with three running sessions and a 2×2 chip grid. |
| `V3-dense.png` | The board with four steps and a line of numbers. |
| `V-stack.png` | The identity area at 1, 2, 3, 4 and 5 running sessions. |
| `V-tabs.png` | The tab alone, in the same five states, at 2×. |

## Identity: the rim is the session

Every session gets a **stable hue** (derived from its id, not from its state): teal
`blindKnifeArgon`, violet `uiAnimation`, coral `swordsGo`, pink `.dsh`, lime `homepage`. That
hue is the orb's **rim** — a 2.5 px contour with a soft glow of the same colour. It answers
"which session is this?" at a glance, and it is what makes the running-session stack read as
*several different sessions* rather than three copies of one.

State stays a separate signal so the two never lie to each other:

| | meaning | where |
|---|---|---|
| rim colour | **which session** | the orb's contour, the stack bubbles' contours |
| badge colour | **what it is doing** | the dot on the orb's lower-right: blue working, amber needs you, green done, red failed, grey idle |
| chip colour | **that session's state** | fill, dot and name of each chip |

Chips deliberately stay state-coloured: identity lives on the bot, so the list of other
sessions still reads as a list of *statuses* rather than a bag of hues.

## Motion

| what | timing | rule |
|---|---|---|
| **blink** | 140 ms, every 4–7 s | eyes squash to a line and back; occasionally a double blink |
| **drift** | 220 ms ease | the eyes follow the pointer, 2–3 px, clamped to the face |
| **breathe** | 3.2 s sine | the whole orb scales ±1.5 % (0.7 px on a 64 px orb) |
| **rim** | 2.4 s sine | identity glow breathes between `.3` (idle) and `1` (attention) |
| **attention flare** | 1.2 s | the rim goes to `1` and the badge pulses once |
| **done** | 400 ms | the badge settles to green and the orb relaxes to `rim .45` |
| **new session** | 220 ms | a new bubble slides in behind the stack |

The orb is drawn from a single `Orb(size, state, identity, blink, lookX, lookY, rim)` — the
window's render loop only changes those last four numbers, so the stills above and the live
window are the same drawing code.

## The stack means sessions, not agents

The front orb is the session on screen. Each smaller orb behind it is another **session that is
running right now** — not another agent inside this session. 1 → one orb; 2–3 → one or two
bubbles; 4 → three; 5+ → two and a `+N` bubble, so the count is never lost. The subtitle says
it in words (`3 sessions running`).

## Re-rendering

```powershell
cd plugins/agent-overlay/designs/MockupStudio
dotnet run -c Release -- ..
```

Requires the .NET 9 SDK (present on this machine). WPF with no NuGet packages, so it builds
offline in about 20 s. Output overwrites the PNGs in the parent directory.

## The rules of this language

- **An identity orb, not a header.** 72 px when alone, 64 px when stacked; white radial
  gradient, two rounded eyes, a state badge at its lower-right, an identity rim around it.
- **Prose, not rows.** Task lines are plain text with a tiny state glyph; only the active one is
  bright. A session's state is a colour, never a word in a column.
- **Material, not a box.** `#161618` at 98.5 %, 26 px radius, a *catch-light* — a 1 px white
  gradient along the top edge, fading at the corners — and one soft shadow
  (blur 90 / depth 30 / 34 %).
- **Colour is the whole vocabulary:** working `#6AA8FF`, needs you `#FFC15E`, done
  `#4ADE80`, failed `#FF6B6B`, idle `#8E8E93`.

## Traps (learned while making these)

- **WPF colours are `#AARRGGBB`.** `#FFFFFF14` is opaque yellow-white, not 8 % white; 8 % white
  is `#14FFFFFF`. The first render round had a khaki outline around every panel because of it.
- The shadow has to be soft (blur ≥ 70). A tight one draws a visible ring on a light desktop.
- Draw the tab and the card as two shapes that touch: a card whose top edge is rounded *and*
  detached from the tab reads as a floating web card, not a notch.
- The board must be photographed on a light desktop. On the dark render background the tab and
  the card's edge disappear.
- A rim drawn *under* the face is only a 2.5 px band — at that size it needs a glow to read as a
  contour rather than a smudge. The first attempt was invisible at 1× and obvious at 2×.
- Identity on the *chips* was tried and reverted: it dims the state reading of the list, which
  is what the chips are for.

