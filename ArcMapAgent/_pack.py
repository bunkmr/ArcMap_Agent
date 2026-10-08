import os, zipfile

# 按官方 .esriaddin 布局打包（参考 ArcGIS 自带 ESRI.ArcGIS.MapCenter.esriaddin）：
#   Config.xml                     <- 清单（注意不是 Config.esriaddinx）
#   Images/*.png
#   Install/ArcMapAgent.dll        <- DLL 必须位于 Install/ 子目录
proj = r"D:\Work\projects\arcmap_agent\ArcMapAgent"
bin_dir = os.path.join(proj, "bin", "Release", "net48")
dll = os.path.join(bin_dir, "ArcMapAgent.dll")
config = os.path.join(proj, "Config.xml")
images = os.path.join(proj, "Images")
out = os.path.join(proj, "ArcMapAgent.esriaddin")

assert os.path.exists(dll), dll
assert os.path.exists(config), config
assert os.path.isdir(images), images

if os.path.exists(out):
    os.remove(out)

with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    z.write(config, "Config.xml")
    z.write(dll, "Install/ArcMapAgent.dll")
    for root, dirs, files in os.walk(images):
        for f in files:
            full = os.path.join(root, f)
            rel = os.path.relpath(full, proj).replace("\\", "/")
            z.write(full, rel)

print("OK packaged:", out, os.path.getsize(out), "bytes")

with zipfile.ZipFile(out, "r") as z:
    print("CONTENTS:")
    for n in z.namelist():
        print("  ", n)
