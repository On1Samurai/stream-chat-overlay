#!/usr/bin/env bash
# Тесты, готовый exe и архив для раздачи. Нужны .NET SDK 10 и python3, работает и на Linux.
# Для тестовой сборки обновления (без правки Directory.Build.props):
#   STREAMCHAT_VERSION=1.1.90 STREAMCHAT_REPOSITORY=owner/repo STREAMCHAT_OUT=dist/update-test ./build.sh
# Подпись архива: путь к закрытому ключу в STREAMCHAT_UPDATE_KEY, без него выпуск не подписывается.
set -euo pipefail
cd "$(dirname "$0")"
out="${STREAMCHAT_OUT:-dist}"
# версия считается один раз и дальше передаётся везде
version="${STREAMCHAT_VERSION:-$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Directory.Build.props | head -1)}"
repo="${STREAMCHAT_REPOSITORY:-}"
if [ -n "$repo" ]; then
  [[ "$repo" =~ ^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$ ]] || { echo "Неверный STREAMCHAT_REPOSITORY" >&2; exit 1; }
  # подписанная версия с чужим репозиторием не должна совпасть с настоящим выпуском
  [[ "$version" =~ ^1\.1\.9[0-9]$ ]] || { echo "Тестовый репозиторий только для версий 1.1.90-1.1.99" >&2; exit 1; }
  [ "$(realpath -m "$out")" != "$(realpath -m dist)" ] || { echo "Тестовый репозиторий нельзя собирать в dist: задайте STREAMCHAT_OUT" >&2; exit 1; }
fi
echo "Репозиторий обновлений: ${repo:-по умолчанию}"
# всегда явно: глобальное свойство сильнее переменной среды UpdateRepository
props=(-p:Version="$version" -p:UpdateRepository="$repo")
mkdir -p "$out"
dotnet test tests/StreamChat.Tests -m:1 --nologo -p:UpdateRepository=
# не rm -rf: рядом лежат архивы других версий
rm -f "$out/StreamChat.exe"
dotnet publish src/StreamChat -c Release -o "$out" -m:1 --nologo "${props[@]}"
STREAMCHAT_VERSION="$version" STREAMCHAT_OUT="$out" python3 - <<'PY'
import os
import zipfile
from pathlib import Path

version = os.environ["STREAMCHAT_VERSION"]
out = os.environ["STREAMCHAT_OUT"]
guide = Path("GUIDE.txt").read_text("utf-8").replace("{VERSION}", version).replace("\n", "\r\n")
with zipfile.ZipFile(f"{out}/StreamChat-{version}.zip", "w", zipfile.ZIP_DEFLATED) as archive:
    archive.write(f"{out}/StreamChat.exe", "StreamChat/StreamChat.exe")
    archive.writestr("StreamChat/ИНСТРУКЦИЯ.txt", guide.encode("utf-8-sig"))
    archive.writestr("StreamChat/LICENSES.txt", Path("LICENSES.txt").read_text("utf-8").replace("\n", "\r\n").encode("utf-8-sig"))
PY

# подпись архива: без неё кнопка обновления не примет выпуск
zip="$out/StreamChat-$version.zip"
key="${STREAMCHAT_UPDATE_KEY:-}"
rm -f "$zip.sig" # старая подпись не должна пережить пересборку
if [ -f "$key" ]; then
  hash=$(sha256sum "$zip" | cut -d' ' -f1)
  printf 'StreamChat-update-v1\nversion=%s\nsha256=%s\n' "$version" "$hash" \
    | openssl dgst -sha256 -sign "$key" > "$zip.sig.tmp"
  mv "$zip.sig.tmp" "$zip.sig"
else
  echo "ВНИМАНИЕ: нет ключа подписи, этот выпуск не обновится кнопкой" >&2
fi
sha256sum "$out"/*.zip
