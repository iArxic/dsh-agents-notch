from PIL import Image, ImageDraw
from pathlib import Path

d = Path("docs/design/icons/ladder")
rows = [
    ("x1-40-dark",  ".40 face  /  dark ring"),
    ("x2-34-dark",  ".34 face  /  dark ring   <- density only"),
    ("x3-40-light", ".40 face  /  light ring"),
    ("x4-34-light", ".34 face  /  light ring   <- what you liked"),
]
TAB = (196, 0, 470, 34)
pad, labelW = 8, 196

def strip(base, box, zoom):
    img = base.crop(box).convert("RGBA")
    out = Image.new("RGBA", img.size, (26,26,30,255))
    out.alpha_composite(img)
    out = out.convert("RGB")
    return out.resize((out.width*zoom, out.height*zoom), Image.NEAREST)

true = {k: Image.open(d / f"{k}.png").convert("RGBA").resize((660, 331), Image.LANCZOS) for k, _ in rows}

for zoom, name, head in ((1, "factorial-true.png", "16 px, as drawn"),
                         (4, "factorial-zoom.png", "the same pixels at 4x nearest")):
    cells = [(label, strip(true[k], TAB, zoom)) for k, label in rows]
    w = pad*2 + labelW + cells[0][1].width
    h = pad*(len(cells)+1) + sum(c.height for _, c in cells) + 18
    img = Image.new("RGB", (w, h), (18,18,21))
    dr = ImageDraw.Draw(img)
    dr.text((pad, 4), head, fill=(175,175,185))
    y = 20 + pad
    for label, cell in cells:
        dr.text((pad, y + cell.height//2 - 4), label, fill=(225,225,235))
        img.paste(cell, (pad + labelW, y))
        y += cell.height + pad
    img.save(d / name)
    print(name, img.size)
