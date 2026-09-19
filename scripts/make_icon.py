# -*- coding: utf-8 -*-
"""图标圆角化工具：源图 → 圆角遮罩 → 多尺寸 ico。

用法：
  python scripts/make_icon.py Assets/AppIcon.ico            # 原位圆角化（自动备份 .bak）
  python scripts/make_icon.py 源.png -o Assets/AppIcon.ico  # 从源图生成

圆角半径按 Win11 Fluent 图标风格约 22%（256px → 56px），输出 16~256 七档尺寸。
"""
import argparse
import shutil
from pathlib import Path

from PIL import Image

SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]


def rounded(img: Image.Image, radius_ratio: float = 0.22) -> Image.Image:
    """给方形图应用圆角 alpha 遮罩（角部透明），返回 RGBA。"""
    img = img.convert("RGBA")
    w, h = img.size
    # 高分辨率遮罩后缩回，避免小尺寸锯齿
    mask_big = Image.new("L", (w * 4, h * 4), 0)
    from PIL import ImageDraw

    draw = ImageDraw.Draw(mask_big)
    r = int(min(w, h) * 4 * radius_ratio)
    draw.rounded_rectangle([0, 0, w * 4 - 1, h * 4 - 1], radius=r, fill=255)
    mask = mask_big.resize((w, h), Image.LANCZOS)
    img.putalpha(mask)
    return img


def main() -> None:
    parser = argparse.ArgumentParser(description="图标圆角化")
    parser.add_argument("source", type=Path, help="源图（png/ico）")
    parser.add_argument("-o", "--output", type=Path, default=None, help="输出 ico（默认原位）")
    parser.add_argument("--radius", type=float, default=0.22, help="圆角比例（默认 0.22）")
    args = parser.parse_args()

    output = args.output or args.source
    # PIL 打开 ico 默认加载最大帧，无需手动选帧
    img = rounded(Image.open(args.source), args.radius)

    if args.source == output and output.suffix.lower() == ".ico" and not Path(str(output) + ".bak").exists():
        shutil.copy2(output, str(output) + ".bak")
        print(f"已备份原文件：{output}.bak")

    img.save(output, format="ICO", sizes=SIZES)
    print(f"已生成：{output}（圆角 {args.radius:.0%}，尺寸 {[s[0] for s in SIZES]}）")


if __name__ == "__main__":
    main()
