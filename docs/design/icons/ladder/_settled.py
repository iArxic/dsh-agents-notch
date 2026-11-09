from PIL import Image, ImageDraw
from pathlib import Path
d = Path("docs/design/icons/ladder")
rows = [("y1-dark"," .34 + dark ring"), ("y2-light",".34 + light ring")]
TAB = (180, 0, 470, 34)
pad, labelW = 8, 132
def strip(base, box, zoom):
    img = base.crop(box).convert("RGBA")
    out = Image.new("RGBA", img.size, (26,26,30,255)); out.alpha_composite(img)
    out = out.convert("RGB")
    return out.resize((out.width*zoom, out.height*zoom), Image.NEAREST)
true = {k: Image.open(d / f"{k}.png").convert("RGBA").resize((660, 331), Image.LANCZOS) for k, _ in rows}
cells = [(label, strip(true[k], TAB, 4)) for k, label in rows]
w = pad*2 + labelW + cells[0][1].width
h = pad*(len(cells)+1) + sum(c.height for _, c in cells) + 18
img = Image.new("RGB", (w, h), (18,18,21)); dr = ImageDraw.Draw(img)
dr.text((pad, 4), "settled sessions at 4x: does a bright rim fight the state ring?", fill=(175,175,185))
y = 20 + pad
for label, cell in cells:
    dr.text((pad, y + cell.height//2 - 4), label, fill=(225,225,235))
    img.paste(cell, (pad + labelW, y)); y += cell.height + pad
img.save(d / "settled-rings.png"); print("settled-rings.png", img.size)
