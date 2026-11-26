# Разработка

## Сборка

```
dotnet build
dotnet test
dotnet run --project src/FreshExile.App
```

Нужен .NET 10 SDK. Данные дерева скачиваются при первом запуске из
`grindinggear/skilltree-export` в `%APPDATA%\FreshExile\tree\data.json`.
Данные «гем → акт, NPC, квест» и цвета гемов берутся из [HeartofPhos/exile-leveling](https://github.com/HeartofPhos/exile-leveling)
(MIT) в `%APPDATA%\FreshExile\gems\`.
Состояние (билд, этап, купленные гемы, позиции окон) лежит в `%APPDATA%\FreshExile\state.json`.

Данные в `src/FreshExile.Core/Data` обновляются скриптами раз в лигу:

- `tools/update-mod-tiers.ps1` — базы и тиры модов из RePoE (`ModTiers.tsv`);
- `tools/update-influence-mods.ps1` — строки влияний (`InfluenceMods.tsv`);
- `tools/update-cluster-skills.ps1` — какие нотабли бывают у каждого enchant кластера (`ClusterSkills.tsv`).

`ClusterNotables.tsv` (порядок нотаблей) и `ClusterOrbitOffsets.tsv` (поворот колец кластеров) взяты из
`Data/ClusterJewels.lua` Path of Building.

## Публикация

Рабочая история приватная. Публичный репозиторий [freshexileapp/FreshExile](https://github.com/freshexileapp/FreshExile)
живёт на локальной ветке `public` (remote `public`): каждый релиз там один коммит от FreshExile с деревом текущего HEAD.
`pwsh tools/publish.ps1 -Version X.Y.Z [-Push]` делает этот коммит и собирает `release/FreshExile.zip` (self-contained exe),
архив загружается на GitHub в Releases руками.

## Структура

- `src/FreshExile.Core`: PoB-декодер и парсер, данные дерева, кластеры (`ClusterJewel`, `ClusterGraph`, `PassiveTree.WithClusters`),
  планировщик пути, чтение Client.txt, API персонажа, прайс-чек и трейд, состояние, распознавание дерева на скриншоте
  (`TreeAligner`, `TreeDetector`), переключение этапов по логу (`StageFollower`), замены гемов (`GemSwap`).
- `src/FreshExile.App`: WPF-оверлей (панель и мини-трекер, окно дерева, наложение на игровое дерево, прайс-чек, настройки, хоткеи, отчёт).
- `tests/FreshExile.Tests`: тесты ядра на реальных данных.

## QA

- **Кадры из игры** — `tests/FreshExile.Tests/TestData/frames`: `open-*.png` (дерево открыто, ожидаемые масштаб и сдвиг
  в `frames.json`) и `closed-*.png` (бег по карте, город, рабочий стол: наложение не должно открываться). Новый кадр: положить
  PNG с нужным префиксом, прогнать генератор, проверить превью глазами, обновить `frames.json`.
  Новые кадры `.gitignore` не пускает в коммит: сначала проверить, что на кадре нет чата, ников, рабочего стола и путей,
  потом добавить явно `git add -f <файл>`. Для `closed-*` лучше синтетика (`closed-synthetic-*`), а не скрин своего экрана.
- **Скриншоты README** — `docs/screenshots/{en,ru}`: сняты `--ui-shots` с QA-запуска на чистых данных (`FRESHEXILE_DATA`:
  `state.json` с тестовым билдом `TestData/ranger-pathfinder.txt` — и код, и распакованный `BuildXml`, — лог
  `client-ranger-acts1-4.txt`, без аккаунта и персонажа, `TreeOverlay` выкл.; кэши `gems`, `icons`, `tree`, `trade` скопированы).
  Act 4: окно дерева, `--demo-town` с `Compact` (мини-трекер), `--demo-swap`, `--settings=tracking`, `--demo-price=<кластер.txt>`;
  `StageIndex` 11 и `FollowGame` выкл.: карточка кластера (`MainWindow-Cluster.png`) и кроп дерева с кластерами.
  `ingame-*` сняты в игре руками. Любой новый кадр сначала проверить глазами.
- **Прогон лога** — `TestData/client-ranger-acts1-4.txt`: настоящий Client.txt Рейнджера с Act 1 до Act 4 (только строки зон
  и уровней). Проверяет, что этапы переключаются вперёд по порядку и что «в городе» совпадает с зоной.
- **Прайс-чек без игры** — `--demo-price` открывает окно на тестовых сапогах и делает один настоящий поиск;
  `--demo-price=<файл>` — на предмете из файла (текст, как его копирует `Ctrl+Alt+C`).
  Чтобы такой запуск не трогал твой `state.json`, задай `FRESHEXILE_DATA` (папка для данных QA-запуска).
- **Вид окон без скриншота экрана** — `--ui-shots=<папка>`: через 7 секунд каждое открытое окно сохраняется в PNG (рисует сам WPF,
  без игры и рабочего стола за окном). Удобно вместе с `--demo-price`, `--demo-town`, `--settings=<раздел>`.
- **Отчёт** — настройки → «О программе» → «Сохранить отчёт на рабочий стол» (или `--report`): zip с `overlay.log`, `crash.log` и настройками.
  Аккаунт и персонаж заменены, `C:\Users\<имя>` в путях → `<user>`, от пути к логу игры остаётся только `…\Path of Exile\logs\Client.txt`
  (`Privacy` в Core). Кадры наложения (`%APPDATA%\FreshExile\frames`, хранится 8 штук) — только с галочкой
  «Приложить скриншоты» (или `--report --with-frames`): на них может попасть чат с чужими никами.
- **Падения** не роняют оверлей: исключение пишется в `%APPDATA%\FreshExile\crash.log`, на экране короткое сообщение.
- **Тесты на скачанных данных** (дерево GGG, гемы exile-leveling) без кэша в `%APPDATA%` помечаются пропущенными с причиной,
  а не проходят вхолостую: перед прогоном один раз запусти оверлей.
- Флаги для ручной проверки без игры: `--demo-town` (мини-трекер «в городе»), `--demo-swap` (плашка нового этапа),
  `--demo-newchar` (вопрос про нового персонажа), `--settings[=раздел]` (окно настроек на разделе: `keys`, `trade`…).
- Вторая копия приложения для проверки перехватывает горячие клавиши основной: не запускай её, пока играешь.
