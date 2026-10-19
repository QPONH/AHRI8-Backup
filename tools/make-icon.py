"""从 Assets/app.png 生成多尺寸 Assets/app.ico（供 exe 图标与窗口图标使用）。

用法：
    python tools/make-icon.py
需要 Pillow。生成的 ico 包含 16..256 多个尺寸；每个尺寸都单独缩放过并加了边距，
避免小尺寸下图形贴边、被资源管理器裁掉。
"""
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Assets" / "app.png"
TARGET = ROOT / "Assets" / "app.ico"

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def main() -> None:
    image = Image.open(SOURCE).convert("RGBA")
    # 统一裁成正方形，保留系统图标需要的透明边距（约 4%）。
    side = min(image.size)
    left = (image.width - side) // 2
    top = (image.height - side) // 2
    image = image.crop((left, top, left + side, top + side))

    frames = []
    for size in SIZES:
        margin = max(1, round(size * 0.04))
        inner = size - margin * 2
        resized = image.resize((inner, inner), Image.LANCZOS)
        canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        canvas.paste(resized, (margin, margin))
        frames.append(canvas)

    # Pillow 用最大尺寸作为基准，其余尺寸由 sizes 参数编码成 ICO 内的多个图像。
    frames[-1].save(TARGET, format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
    print(f"written {TARGET} ({TARGET.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
