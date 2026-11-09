from PIL import Image, ImageDraw
from pathlib import Path

d = Path("docs/design/icons")
variants = [("t0-flat","shipped"),("t1-tint","tint"),("t2-coin","coin"),("t3-adaptive","adaptive")]
TAB  = (196, 0, 470, 34)      # the tab's bot cluster, DIP
ROWS = (60, 118, 420, 236)    # the board's row list, DIP
ORB  = (92, 37, 172, 117)     # the big bot, DIP

def strip(base, box, zoom=1, bg=(26,26,30)):
    img = base.crop(box).convert("RGBA")
    out = Image.new("RGBA", img.size, bg + (255,))
    out.alpha_composite(img)
    out = out.convert("RGB")
    return out.resize((out.width*zoom, out.height*zoom), Image.NEAREST) if zoom > 1 else out

true = {k: Image.open(d / f"{k}.png").convert("RGBA").resize((660, 331), Image.LANCZOS) for k, _ in variants}

pad, labelW = 8, 66

def sheet(rows, header, cols_desc, path, gap=10):
    widths = []
    for _, cells in rows:
        widths.append(sum(c.width for c in cells) + gap*(len(cells)-1))
    w = max(widths) + labelW + pad*2
    h = sum(max(c.height for c in cells) for _, cells in rows) + pad*(len(rows)+1) + 16
    img = Image.new("RGB", (w, h), (18,18,21))
    dr = ImageDraw.Draw(img)
    dr.text((pad, 4), header, fill=(170,170,180))
    dr.text((pad + labelW, 4), cols_desc, fill=(120,120,130))
    y = 18 + pad
    for label, cells in rows:
        hh = max(c.height for c in cells)
        dr.text((pad, y + hh//2 - 4), label, fill=(225,225,235))
        x = pad + labelW
        for c in cells:
            img.paste(c, (x, y + (hh - c.height)//2))
            x += c.width + gap
        y += hh + pad
    img.save(d / path)
    print(path, img.size)

# 1) the small bots, exactly as the screen draws them, and the same pixels at 3x
rows = []
for k, label in variants:
    rows.append((label, [strip(true[k], TAB), strip(true[k], TAB, zoom=3)]))
sheet(rows, "small bots: TRUE size (16 px)  |  the same pixels at 3x nearest",
      "<= 16 px as drawn            <= 3x, no smoothing", "compare-small.png")

# 2) the board rows (14 px) at true size
rows = []
for k, label in variants:
    rows.append((label, [strip(true[k], ROWS)]))
sheet(rows, "board rows: TRUE size (14 px bots)", "", "compare-rows.png")

# 3) the big bot at 60 px
rows = []
for k, label in variants:
    rows.append((label, [strip(true[k], ORB), strip(true[k], ORB, zoom=3)]))
sheet(rows, "big bot at 60 px  |  and 3x", "", "compare-orb.png")
