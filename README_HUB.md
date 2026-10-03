# DriftHub v0 (с нуля)

HUB для установки дрифт-спотов. Стек: WPF .NET 10 (Windows, как и GTA V / CodeWalker).

## Запуск
```
dotnet run --project drifthub/DriftHub.csproj
```
1. Укажи папку с GTA (кнопка Выбрать → выбрать `GTA5.exe`, или Авто).
2. Слева список спотов из `drifthub/bin/Debug/net10.0-windows/spots/`.
3. Справа галерея-превью + кнопка Установить / Удалить.

## Формат спота
```
spots/<id>/spot.json
spots/<id>/preview.jpg (или несколько preview*.jpg/png)
spots/<id>/payload.zip (ymap + _manifest.ymf + пропсы)
```
Пример создаётся сам при первом запуске.

## Что сейчас умеет (фаза 1, без RPF)
- детект GTA: Steam / Epic / Rockstar + проверка `GTA5.exe`
- каталог + галерея
- установка копированием в `GTA/mods/custom_maps/<dlcName>/`
- генерация `DRIFTHUB_README.txt` с ручным шагом: CodeWalker RPF Explorer → `mods/update/x64/dlcpacks` + строка в `dlclist.xml`

## Автообновления и фид спотов (GitHub Releases)
При старте exe тихо проверяет `manifest.json` из последнего GitHub-релиза:
- `https://github.com/Egzotik/drifthub/releases/latest/download/manifest.json`
- другой URL можно положить в `%AppData%\DriftHub\feed_url.txt`
- нет сети → работает офлайн, ничего не ломается

Если `appVersion` новее текущей — exe скачивается (проверка SHA256),
заменяет себя через bat-скрипт и перезапускается.
Споты с версией выше локальной докачиваются в `spots/<id>/` (ymap + preview + `spot.json`
с полями `version`/`source:"feed"`). Ручные папки без метки `source:"feed"`
никогда не затираются и не удаляются.

Выпуск новой версии:
```
.\tools\Publish-Release.ps1 -Version "1.0.1"
```
Скрипт публикует single-file exe, собирает `.feed/` (exe + manifest + ymap/превью),
считает SHA256 и заливает всё в релиз `v1.0.1` через `gh`     (или печатает ручную инструкцию).

## Фаза 2 — CodeWalker.Core (RPF напрямую)
1. `git submodule add https://github.com/dexyfex/CodeWalker external/CodeWalker`
2. В `DriftHub.csproj` добавить Reference на `CodeWalker.Core` (RpfFile, YmapFile, YtypFile).
3. В `Services/SpotInstaller.cs` заменить ручной шаг на:
   - открыть `mods/update/update.rpf` через `RpfFile`
   - импорт `ymap`/`ytyp` из payload
   - правка `dlclist.xml` внутри RPF
   - credit dexyfex + ссылка на gta5-mods (требование автора)
