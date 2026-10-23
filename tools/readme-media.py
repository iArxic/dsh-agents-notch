"""The README's pictures: the hero, the sheets (states, island, faces), the framed boards and the film.

Every notch in them is drawn by the window itself (`--shot`, `--record`) from a state file this
script writes; Pillow only lays those renders onto a backdrop and sets the type. Nothing here
re-draws the notch, so the pictures cannot drift from what the window draws.

    python tools/readme-media.py record     # drive the window through the demo, frames -> dist/media/rec
    python tools/readme-media.py shots      # stills of each state -> dist/media/shots
    python tools/readme-media.py art        # docs/*.png, docs/demo.webp, and dist/media/demo.gif for GIF-only sites

`record` and `shots` stop the live window first (the named mutex admits one notch); the host
brings it back on its own. Windows only, needs a built window (`npm run window:build`) and Pillow.
"""
from __future__ import annotations

import json
import os
import random
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "desktop" / "bin" / "Release" / "net9.0-windows" / "AgentsNotch.exe"
OUT = ROOT / "dist" / "media"
DOCS = ROOT / "docs"
FONTS = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"

# the product's own vocabulary (docs/design/README.md)
WORKING, ATTENTION, DONE, FAILED, IDLE = "#6AA8FF", "#FFC15E", "#4ADE80", "#FF6B6B", "#8E8E93"
INK, MUTED, FAINT = "#F5F5F7", "#A1A1AA", "#6B6B76"
ACCENT = "#5EEAD4"

# Where the big bot sits in a `--shot` (4x the window's design units): the A1 sheet hangs at x 80,
# y 29, and the 60 px bot is placed at (20, 16) inside it with its 2.5 px rim margin.
ORB_CX, ORB_CY = 4 * (80 + 20 + 2.5 + 30), 4 * (29 + 16 + 2.5 + 30)


# ── state files (docs/CONTRACT.md, schema 4) ─────────────────────────────────────────────────────

def session(sid, title, project, status, since, identity, label, tokens, steps, approval=None, kind="tool",
            streak=0):
    return {
        "id": sid, "title": title, "project": project, "cwd": f"C:\\Projects\\{project}",
        "status": status, "since": int(since), "activity": {"kind": kind, "label": label},
        "approval": approval, "steps": steps, "identity": identity, "origin": "",
        "parentSession": "", "contextTokens": tokens, "errorStreak": streak, "approvedAt": None,
    }


ORDER = {"attention": 0, "error": 1, "working": 2, "done": 3}


def state(sessions, wall, budget=1_000_000):
    rows = sorted(sessions, key=lambda s: (ORDER[s["status"]], -s["since"]))
    count = {k: sum(1 for s in rows if s["status"] == k) for k in ORDER}
    top = next((k for k in ("attention", "error", "working", "done") if count[k]), "idle")
    return {
        "schema": 4, "plugin": "dsh-agents-notch/host", "wallMs": int(wall), "hostPid": 4242,
        "retire": "route", "finishedUrl": None, "openUrl": None,
        "context": {"budget": budget, "hotAt": 0.5},
        "settings": {"scale": 1.0, "screen": "primary", "anchor": "top-center", "maxRows": 6,
                     "hideFullscreen": True, "opacity": 1.0, "motion": "on", "showProject": True, "showDone": True},
        "summary": {"status": top, **count, "total": len(rows)},
        "sessions": rows,
    }


A, B, C, D = "session-a1c4e7f0-auth", "session-b2d5f8a1-parser", "session-c3e6a9b2-deps", "session-d4f7b0c3-docs"
PUSH = {"toolName": "pwsh", "reason": "wants to push 3 commits to origin/main"}

# The story the film tells, in the window's own motion-clock ms: three sessions working, the tool light
# running along the tab; one stops on you and the tab grows into the alert; the board unfolds on it (the
# alert folds into its headline); you answer; the board folds; it finishes, and the tab says so.
ASKS, OPEN, ANSWERED, CLOSE, FINISHES, END = 1200, 3900, 5700, 7500, 8300, 10900


def demo_sessions(now, clock):
    """`since` is "now minus how long ago, on the motion clock", so the ages the window prints agree with the film."""
    m = 60_000
    ago = lambda at: int(now - max(0, clock - at))     # the window reads `since` as an integer; 1.0e12 is 0 to it
    a = session(A, "Refactor the auth middleware", "api-server", "working", now - 14 * m, "teal", "pwsh", 182_400, 9)
    b = session(B, "Write tests for the parser", "lexer", "working", now - 6 * m, "violet", "node --test", 96_300, 14, kind="thinking")
    c = session(C, "Bump deps and fix the build", "web-app", "working", now - 3 * m, "coral", "npm run build", 241_900, 6)
    if clock >= ASKS:
        a.update(status="attention", since=ago(ASKS), approval=PUSH)
    if clock >= ANSWERED:
        a.update(status="working", since=ago(ANSWERED), approval=None, steps=10)
    if clock >= FINISHES:
        a.update(status="done", since=ago(FINISHES), steps=12)
    return [a, b, c]


def write_state(path: Path, payload):
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(payload, indent=1), encoding="utf-8")
    # the window holds the file open while it reads it; a replace in that moment is refused
    for _ in range(20):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            time.sleep(0.02)


def stop_live_window():
    subprocess.run(["taskkill", "/IM", "AgentsNotch.exe", "/F"], capture_output=True)
    time.sleep(0.4)


def now_ms():
    return int(time.time() * 1000)


def env():
    return {**os.environ, "DOTNET_BUNDLE_EXTRACT_BASE_DIR": str(OUT / "bundle")}


# ── the window, driven ───────────────────────────────────────────────────────────────────────────

def record():
    """Drive the window through the story. Phases follow the clock the window stamps on each frame,
    not this process's: a frame takes ~0.5 s to render, and the motion clock is what the film shows."""
    rec = OUT / "rec"
    rec.mkdir(parents=True, exist_ok=True)
    for f in rec.glob("f*.png"):
        f.unlink()
    tsv = rec / "frames.tsv"
    tsv.unlink(missing_ok=True)
    st = OUT / "demo-state.json"
    write_state(st, state(demo_sessions(now_ms(), 0), now_ms()))
    stop_live_window()
    proc = subprocess.Popen([str(EXE), "--state", str(st), "--no-tray", "--force-show",
                             "--record", str(rec), "--record-ms", str(END - 600), "--fps", "25",
                             "--open-at", str(OPEN), "--close-at", str(CLOSE),
                             "--log", str(OUT / "rec.log")], env=env())
    while proc.poll() is None:
        clock = 0
        if tsv.exists():
            lines = tsv.read_text().strip().splitlines()
            if len(lines) > 1:
                clock = float(lines[-1].split("\t")[1])
        # one frame ahead: the window reads the file right after stamping a frame
        write_state(st, state(demo_sessions(now_ms(), clock + 40), now_ms()))
        time.sleep(0.05)
    print("recorded", len(list(rec.glob("f*.png"))), "frames ->", rec)


def shot(name, payload, opened=False, extra=()):
    st = OUT / f"{name}.json"
    png = OUT / "shots" / f"{name}.png"
    write_state(st, payload)
    args = [str(EXE), "--state", str(st), "--no-tray", "--force-show", "--shot", str(png),
            "--log", str(OUT / f"{name}.log"), *extra]
    if opened:
        args.append("--open")
    png.unlink(missing_ok=True)
    # The live host respawns its window on its own schedule; when it lands between the kill and the
    # render, the render meets the mutex and writes nothing — so a missing PNG is simply tried again.
    for _ in range(4):
        stop_live_window()
        subprocess.run(args, env=env(), timeout=30)
        if png.exists():
            return
    print("no still for", name, "- see", OUT / f"{name}.log")


# The faces sheet: each face in the state that wears it, and what it means.
FACES = [
    ("focused", "working", "running a tool"), ("thinking", "working", "between tools"),
    ("determined", "working", "a long, busy turn"), ("tired", "working", "working 30 min+"),
    ("glances", "working", "a new row arrived"), ("confused", "working", "a tool just failed"),
    ("dizzy", "working", "tools failing in a row"), ("love", "working", "you allowed it"),
    ("pleading", "attention", "needs you"), ("peeking", "attention", "also waiting"),
    ("done", "done", "finished"), ("starry", "done", "the finish notice"),
    ("wink", "done", "you closed it out"), ("xx", "error", "failed"),
    ("asleep", "stale", "host offline"),
]


def shots():
    (OUT / "shots").mkdir(parents=True, exist_ok=True)
    # The window ages a row against its own clock, so `since` is real time; `wallMs` stays far in
    # the future so a still is never photographed as a stale host — except the one that is meant to be.
    now, m, far = now_ms(), 60_000, 9_999_999_999_999
    a = lambda **k: session(A, "Refactor the auth middleware", "api-server", k.get("s", "working"), now - k.get("age", 14 * m), "teal", "pwsh", 182_400, k.get("steps", 9), k.get("ap"))
    b = lambda **k: session(B, "Write tests for the parser", "lexer", k.get("s", "working"), now - k.get("age", 6 * m), "violet", "node --test", 96_300, 14, kind="thinking")
    c = lambda **k: session(C, "Bump deps and fix the build", "web-app", k.get("s", "working"), now - k.get("age", 3 * m), "coral", "npm run build", 241_900, 6)
    d = lambda **k: session(D, "Update the API docs", "api-server", k.get("s", "done"), now - k.get("age", 11 * m), "lime", "apply_patch", 64_000, 8)
    sets = {
        "working": [a(), b(), c()],
        "attention": [a(s="attention", ap=PUSH, age=1 * m), b(), c(), d()],
        "failed": [c(s="error", age=2 * m), a(), b()],
        "done": [c(s="done", age=20 * 1000), a(s="done", age=9 * m)],
    }
    settled = ("--freeze", "3000")
    for name, rows in sets.items():
        shot(f"tab-{name}", state(rows, far), extra=settled)
    shot("board-attention", state(sets["attention"], far), True, settled)
    # the island: the tab grown into an alert, one per kind
    shot("island-x1", state(sets["attention"], far), extra=("--island", "x1", *settled))
    shot("island-x4", state([session("session-e5-fail", "Fix the e2e suite", "web-app", "error", now - 2 * m, "pink", "playwright test", 80_000, 11), a(), b()], far),
         extra=("--island", "x4", *settled))
    shot("island-d1", state([c(s="done", age=5 * 1000), a(), b()], far), extra=("--island", "d1", *settled))
    # the faces: one session each, so the big bot stands alone in the state that wears the face
    for face, status, _ in FACES:
        if status == "stale":
            payload = state([a()], now - 2 * 60 * m)
        else:
            row = a(s=status, ap=PUSH if status == "attention" else None, age=1 * m, steps=12)
            payload = state([row], far)
        shot(f"face-{face}", payload, True, ("--face", face, *settled))
    # more sessions than the board shows: the list scrolls
    many = [a(s="attention", ap=PUSH, age=1 * m), b(), c(), d(),
            session("session-e5", "Port the lexer to wasm", "lexer", "working", now - 22 * m, "pink", "cargo build", 120_000, 30),
            session("session-f6", "Triage the flaky e2e test", "web-app", "error", now - 4 * m, "teal", "playwright", 80_000, 11),
            session("session-g7", "Draft the release notes", "api-server", "done", now - 30 * m, "violet", "git log", 30_000, 4),
            session("session-h8", "Profile the cold start", "web-app", "working", now - 9 * m, "lime", "node --cpu-prof", 150_000, 7, kind="thinking")]
    shot("board-many", state(many, far), True, settled)
    shot("board-nobudget", state(sets["working"], far, budget=None), True, settled)
    shot("board-stale", state(sets["working"], now - 2 * 60 * m), True, settled)
    print("stills ->", OUT / "shots")


# ── the art ──────────────────────────────────────────────────────────────────────────────────────

def font(name, size):
    return ImageFont.truetype(str(FONTS / name), size)


def variable(size, weight):
    """Segoe UI Variable at a given weight — the type the window itself is set in."""
    f = ImageFont.truetype(str(FONTS / "SegUIVar.ttf"), size)
    # axes: weight 300..700, optical size 5..36 — big type wants the Display cut
    f.set_variation_by_axes([weight, 36 if size >= 48 else 10])
    return f


def hexc(h, a=255):
    h = h.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)


def blob(size, cx, cy, r, color, alpha):
    layer_ = Image.new("RGBA", size, (0, 0, 0, 0))
    ImageDraw.Draw(layer_).ellipse((cx - r, cy - r, cx + r, cy + r), fill=hexc(color, alpha))
    return layer_.filter(ImageFilter.GaussianBlur(r * 0.55))


def wallpaper(w, h, seed=0, grain=True):
    """A night desktop: a soft light under the notch so its black reads as a shape, and an aurora in
    the identity hues low down, where nothing has to be read over it. `grain=False` for the film."""
    img = Image.new("RGBA", (w, h), hexc("#090A12"))
    img.paste(Image.new("RGBA", (w, h), hexc("#12122A")), (0, 0), Image.linear_gradient("L").resize((w, h)))
    lights = [
        (0.50, -0.05, 0.42, ACCENT, 46), (0.12, 1.02, 0.46, "#7C5CFF", 150),
        (0.48, 1.18, 0.36, "#FF7A59", 92), (0.88, 1.00, 0.42, "#2DD4BF", 118),
        (1.02, 0.28, 0.26, "#6AA8FF", 52), (-0.04, 0.30, 0.22, "#E879F9", 36),
    ]
    random.seed(seed)
    for cx, cy, r, col, a in lights:
        jx, jy = (random.uniform(-0.04, 0.04), random.uniform(-0.03, 0.03)) if seed else (0, 0)
        img = Image.alpha_composite(img, blob((w, h), (cx + jx) * w, (cy + jy) * h, r * max(w, h * 2), col, a))
    img = vignette(img)
    return add_grain(img, 6) if grain else img


def vignette(img):
    w, h = img.size
    m = Image.new("L", (w, h), 0)
    ImageDraw.Draw(m).ellipse((-w * 0.25, -h * 0.35, w * 1.25, h * 1.35), fill=255)
    m = ImageChops.invert(m.filter(ImageFilter.GaussianBlur(min(w, h) * 0.18))).point(lambda v: v * 150 // 255)
    dark = Image.new("RGBA", (w, h), (4, 4, 10, 255))
    out = img.copy()
    out.paste(dark, (0, 0), m)
    return out


def add_grain(img, amount):
    noise = Image.effect_noise(img.size, 40).convert("L").point(lambda v: 128 + (v - 128) * amount // 20)
    grain = Image.merge("RGBA", (noise, noise, noise, Image.new("L", img.size, 18)))
    return Image.alpha_composite(img, grain)


def notch_crop(img):
    """The notch hangs from y = 0: trim the sides and the bottom, keep the top edge."""
    l, _, r, b = img.getchannel("A").point(lambda v: 255 if v > 6 else 0).getbbox()
    return img.crop((l, 0, r, b))


def scaled(img, width):
    return img.resize((width, round(img.height * width / img.width)), Image.LANCZOS)


def layer(size):
    """Pillow writes a translucent fill straight into the pixels; draw those on a layer and composite."""
    im = Image.new("RGBA", size, (0, 0, 0, 0))
    return im, ImageDraw.Draw(im)


def rounded(img, radius):
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, img.width - 1, img.height - 1), radius=radius, fill=255)
    out = Image.new("RGBA", img.size, (0, 0, 0, 0))
    out.paste(img, (0, 0), mask)
    return out


def hero():
    W, H = 2560, 1280                       # 2x GitHub's 1280x640 social card
    canvas = wallpaper(W, H)
    board = scaled(notch_crop(Image.open(OUT / "shots" / "board-attention.png")), 1240)
    canvas.alpha_composite(board, ((W - board.width) // 2, 0))
    # the top edge of the display the notch hangs from
    top, d = layer((W, H))
    d.rectangle((0, 0, W, 3), fill=hexc("#FFFFFF", 26))
    canvas = Image.alpha_composite(canvas, top)

    y = board.height + 18
    d = ImageDraw.Draw(canvas)
    title = variable(150, 700)
    left, right = "Agents ", "Notch"
    tw = d.textlength(left + right, font=title)
    x0 = (W - tw) / 2
    d.text((x0, y), left, font=title, fill=hexc(INK))
    d.text((x0 + d.textlength(left, font=title), y), right, font=title, fill=hexc(ACCENT))
    d.text((W / 2, y + 196), "Which of your DeepSeek Harness sessions needs you, at a glance.",
           font=variable(48, 400), fill=hexc("#C4C4D4"), anchor="ma")

    glass, g = layer((W, H))
    chip_f = variable(36, 600)
    chips = [("working", WORKING), ("needs you", ATTENTION), ("done", DONE), ("failed", FAILED)]
    gap, padx, ch, dot = 20, 32, 74, 18
    widths = [padx * 2 + dot + 16 + d.textlength(t, font=chip_f) for t, _ in chips]
    x = (W - sum(widths) - gap * (len(chips) - 1)) / 2
    cy = y + 286
    for (t, col), cw in zip(chips, widths):
        g.rounded_rectangle((x, cy, x + cw, cy + ch), radius=ch // 2, fill=hexc("#FFFFFF", 14), outline=hexc("#FFFFFF", 38), width=2)
        g.ellipse((x + padx, cy + (ch - dot) / 2, x + padx + dot, cy + (ch + dot) / 2), fill=hexc(col))
        g.text((x + padx + dot + 16, cy + ch / 2 + 1), t, font=chip_f, fill=hexc(INK), anchor="lm")
        x += cw + gap

    mono = font("CascadiaMono.ttf", 36)
    prompt, cmd = "> ", "dsh plugin --profile web add dsh-agents-notch"
    cw = d.textlength(prompt + cmd, font=mono) + 80
    bx, by, bh = (W - cw) / 2, cy + ch + 52, 84
    g.rounded_rectangle((bx, by, bx + cw, by + bh), radius=18, fill=hexc("#000000", 120), outline=hexc("#FFFFFF", 30), width=2)
    g.text((bx + 40, by + bh / 2), prompt, font=mono, fill=hexc(ACCENT), anchor="lm")
    g.text((bx + 40 + d.textlength(prompt, font=mono), by + bh / 2), cmd, font=mono, fill=hexc("#E4E4EE"), anchor="lm")
    canvas = Image.alpha_composite(canvas, glass)
    canvas.convert("RGB").resize((W // 2, H // 2), Image.LANCZOS).save(DOCS / "hero.png", optimize=True)
    print("hero ->", DOCS / "hero.png")


def states_sheet():
    """One card per state: the folded tab on a slice of desktop, the name, and what it asks of you."""
    cards = [
        ("tab-working", "Working", WORKING, "A blue arc turns. Light runs along the tab while a tool runs."),
        ("tab-attention", "Needs you", ATTENTION, "Amber ring. A question or an approval is waiting."),
        ("tab-done", "Done", DONE, "Green ring and the ^^ face, until you close it out."),
        ("tab-failed", "Failed", FAILED, "Red ring and the x x face. The turn ended on an error."),
    ]
    S = 2                                   # drawn at 2x, saved at 1x
    cw, chh, gap = 600 * S, 236 * S, 20 * S
    W, H = cw * 2 + gap, chh * 2 + gap
    canvas = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    for i, (name, label, col, line) in enumerate(cards):
        x, y = (i % 2) * (cw + gap), (i // 2) * (chh + gap)
        card = wallpaper(cw, chh, seed=11 + i)
        tab = notch_crop(Image.open(OUT / "shots" / f"{name}.png"))
        tab = scaled(tab, min(cw - 80 * S, int(tab.width * S / 4 * 1.9)))
        card.alpha_composite(tab, ((cw - tab.width) // 2, 0))
        d = ImageDraw.Draw(card)
        d.ellipse((40 * S, 150 * S, 54 * S, 164 * S), fill=hexc(col))
        d.text((66 * S, 157 * S), label, font=variable(27 * S, 650), fill=hexc(INK), anchor="lm")
        d.text((40 * S, 198 * S), line, font=variable(16 * S, 400), fill=hexc("#B4B4C6"), anchor="lm")
        edge, e = layer((cw, chh))
        e.rounded_rectangle((0, 0, cw - 1, chh - 1), radius=24 * S, outline=hexc("#FFFFFF", 34), width=S)
        card = Image.alpha_composite(card, edge)
        mask = Image.new("L", (cw, chh), 0)
        ImageDraw.Draw(mask).rounded_rectangle((0, 0, cw - 1, chh - 1), radius=24 * S, fill=255)
        canvas.paste(card, (x, y), mask)
    canvas.resize((W // S, H // S), Image.LANCZOS).save(DOCS / "states.png", optimize=True)
    print("states ->", DOCS / "states.png")


def island_sheet():
    """The three alerts the tab grows into, stacked on one slice of desktop, each with its name."""
    rows = [("island-x1", "Needs you", ATTENTION, "the reason, and Open — which switches the app to that session"),
            ("island-x4", "Failed", FAILED, "which session, and the last tool it ran"),
            ("island-d1", "Finished", DONE, "a two-second notice, then back to the tab")]
    S = 2
    W, rowh, top = 1200 * S, 150 * S, 24 * S
    H = top + rowh * len(rows) + 10 * S
    canvas = wallpaper(W, H, seed=31)
    d = ImageDraw.Draw(canvas)
    for i, (name, label, col, line) in enumerate(rows):
        isl = notch_crop(Image.open(OUT / "shots" / f"{name}.png"))
        isl = scaled(isl, int(isl.width * S / 4 * 1.55))
        y = top + i * rowh
        # a slice of screen edge for each, so every island hangs from a top, as it does on the desktop
        edge, e = layer((W, H))
        e.rectangle((380 * S, y, W - 24 * S, y + 2), fill=hexc("#FFFFFF", 22))
        canvas = Image.alpha_composite(canvas, edge)
        canvas.alpha_composite(isl, (380 * S + (W - 404 * S - isl.width) // 2, y))
        d = ImageDraw.Draw(canvas)
        d.ellipse((36 * S, y + 34 * S, 50 * S, y + 48 * S), fill=hexc(col))
        d.text((62 * S, y + 41 * S), label, font=variable(26 * S, 650), fill=hexc(INK), anchor="lm")
        tw = 300 * S
        words, lines, cur = line.split(), [], ""
        f = variable(15 * S, 400)
        for wd in words:
            test = (cur + " " + wd).strip()
            if d.textlength(test, font=f) > tw and cur:
                lines.append(cur); cur = wd
            else:
                cur = test
        lines.append(cur)
        for j, ln in enumerate(lines):
            d.text((36 * S, y + (72 + j * 22) * S), ln, font=f, fill=hexc("#B4B4C6"), anchor="lm")
    rounded(canvas, 28 * S).resize((W // S, H // S), Image.LANCZOS).save(DOCS / "island.png", optimize=True)
    print("island ->", DOCS / "island.png")


def faces():
    """Every face, cut from single-session boards: the big bot, its ring or spinner, and when it shows."""
    S, tile, label_h, gap, cols = 2, 300, 104, 22, 5
    rows_n = (len(FACES) + cols - 1) // cols
    W = tile * cols + gap * (cols - 1)
    H = (tile + label_h) * rows_n + gap * (rows_n - 1)
    canvas = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    half = 190
    tone = {"working": WORKING, "attention": ATTENTION, "done": DONE, "error": FAILED, "stale": IDLE}
    for i, (face, status, meaning) in enumerate(FACES):
        shot_img = Image.open(OUT / "shots" / f"face-{face}.png").convert("RGBA")
        bg = Image.new("RGBA", shot_img.size, hexc("#08080A"))
        bg.alpha_composite(shot_img)
        # the board's text column starts just right of the bot; blank it, so no letter edges into a tile
        ImageDraw.Draw(bg).rectangle((4 * (80 + 96) - 6, 0, bg.width, bg.height), fill=hexc("#08080A"))
        orb = bg.crop((ORB_CX - half, ORB_CY - half, ORB_CX + half, ORB_CY + half)).resize((tile, tile), Image.LANCZOS)
        t = Image.new("RGBA", (tile, tile + label_h), hexc("#08080A"))
        t.alpha_composite(orb, (0, 0))
        d = ImageDraw.Draw(t)
        f, g = variable(30, 650), variable(21, 400)
        name = "^^" if face == "done" else "x x" if face == "xx" else face
        tw = d.textlength(name, font=f) + 26
        x = (tile - tw) / 2
        d.ellipse((x, tile + 14, x + 14, tile + 28), fill=hexc(tone[status]))
        d.text((x + 26, tile + 21), name, font=f, fill=hexc(INK), anchor="lm")
        d.text((tile / 2, tile + 64), meaning, font=g, fill=hexc("#A9A9B8"), anchor="mm")
        cx, cy = (i % cols) * (tile + gap), (i // cols) * (tile + label_h + gap)
        canvas.paste(rounded(t, 30), (cx, cy))
    canvas.resize((W * 3 // 4, H * 3 // 4), Image.LANCZOS).save(DOCS / "faces.png", optimize=True)
    print("faces ->", DOCS / "faces.png")


def framed(name, out, width=1520):
    """A board on a slice of desktop, for the README's in-line examples."""
    board = notch_crop(Image.open(OUT / "shots" / f"{name}.png"))
    board = scaled(board, width)
    W, H = board.width + 200, board.height + 40
    canvas = wallpaper(W, H, seed=21)
    canvas.alpha_composite(board, (100, 0))
    rounded(canvas, 40).resize((W // 2, H // 2), Image.LANCZOS).save(DOCS / out, optimize=True)
    print(name, "->", DOCS / out)


def film(width, fps=20):
    """The recorded frames over a smooth wallpaper, with delays cut from the window's own stamps."""
    rec = OUT / "rec"
    rows = [l.split("\t") for l in (rec / "frames.tsv").read_text().splitlines()[1:] if l.strip()]
    kept, last = [], -1e9
    for n, ms in rows:
        if float(ms) - last >= 1000 / fps - 1:
            kept.append((rec / n, float(ms))); last = float(ms)
    union = None
    for p, _ in kept:
        bb = Image.open(p).getchannel("A").point(lambda v: 255 if v > 6 else 0).getbbox()
        if bb:
            union = bb if union is None else (min(union[0], bb[0]), 0, max(union[2], bb[2]), max(union[3], bb[3]))
    l, _, r, b = union
    margin = 60
    W, H = (r - l) + margin * 2, b + 30
    size = (width, round(H * width / W))
    smooth = wallpaper(W, H, grain=False)
    frames, delays = [], []
    for i, (p, ms) in enumerate(kept):
        img = Image.open(p).crop((l - margin, 0, r + margin, H))
        frames.append(Image.alpha_composite(smooth, img).convert("RGB").resize(size, Image.LANCZOS))
        # the last frame lasts one frame too: a hold before the loop froze the tool light mid-sweep
        nxt = kept[i + 1][1] if i + 1 < len(kept) else ms + 1000 / fps
        delays.append(max(20, int(round((nxt - ms) / 10.0)) * 10))
    return frames, delays


def demo_webp(width=1100):
    """The README's film: full colour, so the wallpaper's gradient and the state colours survive.
    A GIF has 256 colours a frame, and a gradient behind a black notch spends them all on banding."""
    frames, delays = film(width)
    frames[0].save(DOCS / "demo.webp", save_all=True, append_images=frames[1:], duration=delays,
                   loop=0, quality=88, method=6)
    kb = (DOCS / "demo.webp").stat().st_size // 1024
    print(f"demo -> {DOCS / 'demo.webp'} ({len(frames)} frames, {frames[0].size[0]}x{frames[0].size[1]}, {kb} KB)")


def demo_gif(width=800):
    """For places that only take a GIF (Reddit, a discussion). A palette per frame, so whatever state
    is on screen gets its colours (a shared one spends them on the wallpaper and loses the rings), and
    dithered, so the gradient does not band. The README uses `demo.webp`, which needs neither."""
    frames, delays = film(width)
    # Pillow only dithers when it is handed a palette, so each frame's own palette is built first
    q = [f.quantize(palette=f.quantize(colors=256, method=Image.MEDIANCUT), dither=Image.FLOYDSTEINBERG) for f in frames]
    out = OUT / "demo.gif"                  # not committed: nothing in the repo shows it
    q[0].save(out, save_all=True, append_images=q[1:], duration=delays,
              loop=0, optimize=True, disposal=1)
    kb = out.stat().st_size // 1024
    print(f"demo -> {out} ({len(q)} frames, {q[0].size[0]}x{q[0].size[1]}, {kb} KB)")


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "art"
    OUT.mkdir(parents=True, exist_ok=True)
    if cmd == "record":
        record()
    elif cmd == "shots":
        shots()
    elif cmd == "art":
        hero(); states_sheet(); island_sheet(); faces()
        framed("board-many", "board-many.png"); framed("board-nobudget", "board-nobudget.png")
        framed("board-stale", "board-stale.png")
        demo_webp(); demo_gif()
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
