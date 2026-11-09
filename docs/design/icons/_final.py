from PIL import Image, ImageDraw
from pathlib import Path
d = Path("docs/design/icons")
TAB = (196, 0, 470, 34)
def strip(base, box, zoom=1):
    img = base.crop(box).convert("RGBA")
    out = Image.new("RGBA", img.size, (26,26,30,255)); out.alpha_composite(img)
    out = out.convert("RGB")
    return out.resize((out.width*zoom, out.height*zoom), Image.NEAREST) if zoom > 1 else out
rows = [("shipped-0.2.0.png","working, as drawn (16 px)"),
        ("shipped-settled.png","settled: failed + done")]
true = {k: Image.open(d/k).convert("RGBA").resize((660,331), Image.LANCZOS) for k,_ in rows}
pad, labelW = 8, 180
cells = [(lab, strip(true[k], TAB)) for k,lab in rows]
w = pad*2 + labelW + cells[0][1].width
h = pad*(len(cells)+1) + sum(c.height for _,c in cells) + 18
img = Image.new("RGB", (w,h), (18,18,21)); dr = ImageDraw.Draw(img)
dr.text((pad,4), "0.2.0 bots at TRUE size, and settled", fill=(175,175,185))
y = 20+pad
for lab,cell in cells:
    dr.text((pad, y+cell.height//2-4), lab, fill=(225,225,235))
    img.paste(cell, (pad+labelW, y)); y += cell.height+pad
img.save(d/"shipped-true.png"); print("shipped-true.png", img.size)
# and the whole board, cropped, for the record
im = Image.open(d/"shipped-0.2.0.png").convert("RGBA")
b = im.getchannel("A").getbbox(); p=24
crop = im.crop((max(0,b[0]-p),max(0,b[1]-p),min(im.width,b[2]+p),min(im.height,b[3]+p)))
bg = Image.new("RGBA", crop.size, (26,26,30,255)); bg.alpha_composite(crop)
bg.convert("RGB").save(d/"shipped-board.png"); print("shipped-board.png", crop.size)
