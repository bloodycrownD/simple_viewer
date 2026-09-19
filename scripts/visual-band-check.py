# 分析窗口截图：画布区（侧栏右侧）内容行列投影 → 卡片行带/列带的几何数值
from PIL import Image

img = Image.open(r'D:\Dev\Python\simple_viewer\release\window.png').convert('RGB')
W, H = img.size
px = img.load()

# 找主导背景色（四角+边）
samples = [px[2, 2], px[W-3, 2], px[2, H-3], px[W-3, H-3], px[W//2, 4]]
bg = max(set(samples), key=samples.count)
print(f'image={W}x{H} bg={bg}')

def is_bg(c, tol=12):
    return abs(c[0]-bg[0]) <= tol and abs(c[1]-bg[1]) <= tol and abs(c[2]-bg[2]) <= tol

# 画布 x 范围：从右往左找第一列“几乎全背景”的边界（侧栏右侧起）
content_cols = []
for x in range(W):
    cnt = sum(1 for y in range(60, H-10) if not is_bg(px[x, y]))
    content_cols.append(cnt)

# 行带检测（画布区）：全宽统计内容像素
def bands(mask):
    out = []
    start = None
    for i, v in enumerate(mask):
        if v and start is None:
            start = i
        elif not v and start is not None:
            out.append((start, i-1))
            start = None
    if start is not None:
        out.append((start, len(mask)-1))
    return out

row_mask = []
for y in range(40, H-8):
    cnt = sum(1 for x in range(W//3, W-8) if not is_bg(px[x, y]))
    row_mask.append(cnt > (W - W//3) * 0.02)
rb = bands(row_mask)
print('row bands (y0,y1,height) in canvas x-range:')
for a, b in rb:
    print(f'  y={a+40}..{b+40} h={b-a+1}')

# 对每个行带，输出其内容列带（卡片列）
for a, b in rb:
    y0, y1 = a+40, b+40
    col_mask = []
    for x in range(W//3, W-8):
        cnt = sum(1 for y in range(y0, y1+1) if not is_bg(px[x, y]))
        col_mask.append(cnt > max(1, (y1-y0+1)//3))
    cb = bands(col_mask)
    desc = ', '.join(f'x={c0+W//3}..{c1+W//3} w={c1-c0+1}' for c0, c1 in cb)
    print(f'band y={y0}..{y1} h={y1-y0+1}: {desc}')
