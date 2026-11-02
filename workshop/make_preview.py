
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

W = H = 512
img = Image.new("RGB", (W, H), (12, 14, 22))
d = ImageDraw.Draw(img)
for y in range(H):
    t = y / H
    d.line([(0, y), (W, y)], fill=(int(18 + 12*t), int(20 + 10*t), int(34 + 20*t)))

def font(size, bold=False):
    for n in (["seguisb.ttf"] if bold else ["segoeui.ttf"]) + (["arialbd.ttf"] if bold else ["arial.ttf"]):
        p = os.path.join(r"C:\Windows\Fonts", n)
        if os.path.exists(p):
            try: return ImageFont.truetype(p, size)
            except Exception: pass
    return ImageFont.load_default()

f_title = font(42, bold=True)
t = "SPECTATE MATE"
d.text(((W - d.textlength(t, font=f_title)) / 2, 40), t, font=f_title, fill=(255, 255, 255))

f_sub = font(21)
for i, line in enumerate(["Right-click a teammate avatar", "to spectate"]):
    d.text(((W - d.textlength(line, font=f_sub)) / 2, 96 + i*26), line, font=f_sub, fill=(150, 180, 245))

rows = [
    (150, 200, (96, 148, 128)),
    (150, 290, (208, 176, 150)),
    (150, 380, (168, 140, 108)),
]
row_w, row_h, avatar = 300, 64, 50

for x, y, col in rows:
    d.rounded_rectangle([x-8, y-8, x+row_w, y+row_h-4], radius=10,
                        fill=(22, 26, 44), outline=(52, 64, 104), width=1)
    d.rounded_rectangle([x, y, x+avatar, y+avatar-8], radius=8,
                        fill=col, outline=(150, 170, 220), width=2)
    cx, cy = x + avatar//2, y + (avatar-8)//2
    d.ellipse([cx-12, cy-15, cx+12, cy+9], fill=(238, 240, 248))
    d.ellipse([cx-8, cy-7, cx+8, cy+5], fill=col)
    d.rounded_rectangle([x+avatar+12, y+4, x+avatar+124, y+24], radius=4, fill=(34, 40, 62))
    d.rounded_rectangle([x+avatar+12, y+32, x+avatar+124, y+42], radius=5, fill=(40, 44, 58))
    d.rounded_rectangle([x+avatar+12, y+32, x+avatar+90, y+42], radius=5, fill=(196, 60, 60))

# 高亮第二行
hx, hy = rows[1][0], rows[1][1]
glow = Image.new("L", (W, H), 0)
ImageDraw.Draw(glow).rounded_rectangle([hx-16, hy-16, hx+row_w+8, hy+row_h], radius=14, fill=155)
glow = glow.filter(ImageFilter.GaussianBlur(16))
img = Image.composite(Image.new("RGB", (W, H), (255, 170, 62)), img, glow)
d = ImageDraw.Draw(img)
d.rounded_rectangle([hx-8, hy-8, hx+row_w, hy+row_h-4], radius=10, outline=(255, 192, 92), width=3)

# 右上角眼睛
ex, ey = 470, 236
d.ellipse([ex-20, ey-12, ex+20, ey+12], outline=(140, 220, 180), width=3)
d.ellipse([ex-7, ey-7, ex+7, ey+7], fill=(140, 220, 180))

# 鼠标：放在第二行右侧空白处，指尖指向该行
mx, my = 380, 300
d.rounded_rectangle([mx-14, my-22, mx+14, my+22], radius=12,
                    fill=(14, 18, 32), outline=(240, 246, 255), width=3)
d.line([(mx, my-22), (mx, my-2)], fill=(240, 246, 255), width=3)
d.rounded_rectangle([mx+3, my-18, mx+11, my-4], radius=4, fill=(255, 176, 70))
for r_, a in ((24, 210), (34, 120), (44, 70)):
    d.ellipse([mx-r_, my-r_, mx+r_, my+r_], outline=(255, 196, 96, a), width=2)

# 从鼠标指向高亮行的一条引导线
d.line([(mx-18, my-4), (hx+row_w-4, hy+row_h//2)], fill=(255, 190, 90, 90), width=2)

f_chip = font(14)
chips = ["Read-only", "Client-side"]
total = sum(d.textlength(c, font=f_chip) for c in chips) + 40
x = (W - total) / 2
for c in chips:
    cw2 = d.textlength(c, font=f_chip)
    d.rounded_rectangle([x-11, 452, x+cw2+11, 477], radius=13, fill=(28, 38, 68), outline=(76, 104, 168), width=1)
    d.text((x, 459), c, font=f_chip, fill=(178, 202, 250))
    x += cw2 + 40

out = os.path.join(r"E:\MOD\sts2\godot_project\Spectate teammate\workshop", "preview.png")
img.save(out, "PNG", optimize=True)
print("saved", os.path.getsize(out), "bytes")
