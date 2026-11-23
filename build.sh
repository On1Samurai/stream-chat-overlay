#!/usr/bin/env bash
# Тесты, готовый exe и архив для раздачи. Нужны .NET SDK 10 и python3, работает и на Linux.
set -euo pipefail
cd "$(dirname "$0")"
dotnet test tests/StreamChat.Tests -m:1 --nologo
# не rm -rf: рядом лежат архивы других версий
rm -f dist/StreamChat.exe
dotnet publish src/StreamChat -c Release -o dist -m:1 --nologo
python3 - <<'PY'
import re
import zipfile
from pathlib import Path

version = re.search(r"<Version>(.+?)</Version>", Path("Directory.Build.props").read_text()).group(1)
guide = Path("GUIDE.txt").read_text("utf-8").replace("{VERSION}", version).replace("\n", "\r\n")
with zipfile.ZipFile(f"dist/StreamChat-{version}.zip", "w", zipfile.ZIP_DEFLATED) as archive:
    archive.write("dist/StreamChat.exe", "StreamChat/StreamChat.exe")
    archive.writestr("StreamChat/ИНСТРУКЦИЯ.txt", guide.encode("utf-8-sig"))
    archive.writestr("StreamChat/LICENSES.txt", Path("LICENSES.txt").read_text("utf-8").replace("\n", "\r\n").encode("utf-8-sig"))
PY
sha256sum dist/*.zip
