"""从 QGIS Agent 项目复用图标，生成 ArcMap Agent 所需的多尺寸 PNG。

QGIS Agent 的图标（D:/Work/projects/qgis_agent/icon.png）是青色六边形机器人，
64x64 RGBA。这里按 ArcMap 插件的实际需要缩放为：
  Images/icon.png    64x64  原始尺寸，作为嵌入式资源供面板头部显示
  Images/agent32.png 32x32  停靠窗口 / 大按钮
  Images/show16.png  16x16  工具栏按钮（ArcMap 工具条使用 16x16）
"""
import os

try:
    from PIL import Image
except ImportError:  # pragma: no cover
    raise SystemExit("需要 Pillow：请使用受管 Python 环境（已内置 PIL）")

SRC = r"D:\Work\projects\qgis_agent\icon.png"
BASE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(BASE, "Images")
os.makedirs(OUT, exist_ok=True)

with Image.open(SRC) as im:
    im = im.convert("RGBA")

    def save(size, name):
        resized = im.resize((size, size), Image.LANCZOS)
        path = os.path.join(OUT, name)
        resized.save(path, "PNG", optimize=True)
        print("wrote", path, resized.size, os.path.getsize(path), "bytes")

    save(64, "icon.png")
    save(32, "agent32.png")
    save(16, "show16.png")

print("icons done")
