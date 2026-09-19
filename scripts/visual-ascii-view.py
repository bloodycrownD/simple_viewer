# 伪视觉：截图降采样为字符画（亮度/饱和度映射），用于无视觉模型环境的布局确认
import sys
from PIL import Image

path = sys.argv[1] if len(sys.argv) > 1 else r'D:\Dev\Python\simple_viewer\release\window2.png'
img = Image.open(path).convert('RGB')
W, H = img.size
cols, rows = 96, 44
cell_w, cell_h = W / cols, H / rows
out = []
for ry in range(rows):
    line = []
    for rx in range(cols):
        x0, y0 = int(rx*cell_w), int(ry*cell_h)
        box = img.crop((x0, y0, min(W, x0+max(1,int(cell_w))), min(H, y0+max(1,int(cell_h)))))
        px = list(box.getdata())
        n = len(px)
        r = sum(p[0] for p in px)//n
        g = sum(p[1] for p in px)//n
        b = sum(p[2] for p in px)//n
        lum = (r*299+g*587+b*114)//1000
        sat = max(r, g, b) - min(r, g, b)
        if sat > 40:
            ch = '#' if lum > 110 else '+'
        elif lum > 150:
            ch = '.'
        elif lum > 70:
            ch = ':'
        elif lum > 35:
            ch = '-'
        else:
            ch = ' '
        line.append(ch)
    out.append(''.join(line))
print('\n'.join(out))
