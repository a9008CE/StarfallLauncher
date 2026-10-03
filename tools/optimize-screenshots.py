#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把官网截图压到 256 色调色板 PNG，体积约减 2/3，肉眼基本无损。

用法：
    python tools/optimize-screenshots.py [目录]      # 默认 website/

背景：启动器界面截图是「大面积纯色 UI + 一张照片级壁纸」，真彩色 PNG 约 250-680 KB/张，
11 张接近 3.7 MB，对国内访问不友好。转 256 色调色板后约 1.25 MB，实测看不出差别
（2026-10-03 用这套参数压过一轮，官网首屏与原图 sha256 校验一致）。

依赖：Pillow
"""
import os
import sys
import glob

try:
    from PIL import Image
except ImportError:
    sys.exit("需要 Pillow：pip install Pillow")


def main() -> None:
    target = sys.argv[1] if len(sys.argv) > 1 else "website"
    if not os.path.isdir(target):
        sys.exit("目录不存在：%s" % target)

    files = sorted(glob.glob(os.path.join(target, "shot-*.png")))
    if not files:
        sys.exit("在 %s 下没找到 shot-*.png" % target)

    total_before = total_after = 0
    for path in files:
        before = os.path.getsize(path)
        img = Image.open(path).convert("RGB")
        img.quantize(colors=256, method=Image.MEDIANCUT, dither=Image.FLOYDSTEINBERG).save(
            path, optimize=True
        )
        after = os.path.getsize(path)
        total_before += before
        total_after += after
        print("  %-24s %5d KB -> %5d KB" % (os.path.basename(path), before // 1024, after // 1024))

    saved = 100 - total_after * 100 // max(total_before, 1)
    print("合计 %d KB -> %d KB（省 %d%%）" % (total_before // 1024, total_after // 1024, saved))


if __name__ == "__main__":
    main()
