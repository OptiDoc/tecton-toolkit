# tecton-toolkit

Центр инструментов платформы Tecton: конвейеры выпуска и инструмент упаковки расширений.
По этому репозиторию собирают свои расширения отдельные плагины (см. план в репозитории
`tecton`, этап 3): шаблон нового плагина подключает workflows отсюда, а правила
описания и состава архива описаны в `STANDARDS.md` репозитория `tecton`.

## Состав

| Путь | Назначение |
| --- | --- |
| `.github/workflows/ci.yml` | Переиспользуемый конвейер проверки: сборка, тесты, упаковка, проверка архива |
| `.github/workflows/release.yml` | Переиспользуемый выпуск по тегу `vX.Y.Z`: упаковка, подпись, GitHub Release |
| `.github/workflows/toolkit-ci.yml` | Собственный CI этого репозитория (сборка + самопроверка) |
| `tools/PackageTool` | Консольный инструмент `tecton-toolkit`: упаковка, подпись, проверка |

## Инструмент

```text
dotnet run --project tools/PackageTool -c Release -- keygen
dotnet run --project tools/PackageTool -c Release -- pack \
  --description manifest/manifest.json --source out/content --output out/mod-layout-1.0.0.zip
dotnet run --project tools/PackageTool -c Release -- stamp \
  --description manifest/manifest.json --sha256 <hex> --size <n> --signature ed25519:...
dotnet run --project tools/PackageTool -c Release -- sign \
  --key @publisher.key --description manifest/manifest.json --archive out/mod-layout-1.0.0.zip
dotnet run --project tools/PackageTool -c Release -- pub --key @publisher.key
dotnet run --project tools/PackageTool -c Release -- verify \
  --description manifest/manifest.json --archive out/mod-layout-1.0.0.zip --public-key <base64>
dotnet run --project tools/PackageTool -c Release -- selftest
```

`pack` собирает детерминированный архив: только каталоги `plugin/`, `blocks/`,
`i18n/`, `assets/`; генерирует `extension.json`; порядок записей по возрастанию пути,
время 1980-01-01, сжатие store — один коммит даёт один `sha256`.
`verify` проверяет архив по стандарту: имя архива, предел класса, пути, `files[]`,
множество команд, подпись `id|version|sha256`.

## Подключение в репозитории плагина

Проверка на каждый пуш (`.github/workflows/ci.yml` плагина):

```yaml
jobs:
  ci:
    uses: OptiDoc/tecton-toolkit/.github/workflows/ci.yml@main
    with:
      project: src/Plugin.LayoutManager/Plugin.LayoutManager.csproj
      tests: tests/Plugin.LayoutManager.Tests/Plugin.LayoutManager.Tests.csproj
      description: manifest/manifest.json
      source: out/content
```

Выпуск по тегу (`.github/workflows/release.yml` плагина):

```yaml
on:
  push:
    tags: ["v*"]

permissions:
  contents: write

jobs:
  release:
    uses: OptiDoc/tecton-toolkit/.github/workflows/release.yml@main
    with:
      project: src/Plugin.LayoutManager/Plugin.LayoutManager.csproj
      description: manifest/manifest.json
      source: out/content
    secrets:
      publisher-key: ${{ secrets.TECTON_PUBLISHER_KEY }}
```

Проверки выпуска: тег без `v` должен совпадать с полем `version` описания, имя
архива — `<слаг>-<версия>.zip`, подпись ставится ключом издателя, в Release
попадают архив, описание с заполненными `package.sha256/size/signature` и
`SHA256SUMS`.

## Секреты

| Секрет | Что это | Как получить |
| --- | --- | --- |
| `TECTON_PUBLISHER_KEY` | Приватный ключ издателя, base64 (32 байта) | `keygen`; приватный ключ — только в секретах, публичный — в настройках клиента |
| `toolkit-token` | Токен доступа к этому репозиторию, если оно приватное | Токен GitHub с правом чтения; нужен только при приватном `tecton-toolkit` |

## Требования

- .NET SDK 8.0.x, `jq` и `gh` (на стандартном раннере GitHub уже есть).
- Пакет контракта для плагина: `Modules.Abstractions` версии, равной `core_min`
  описания (публикуется при выпуске ядра, репозиторий `tecton`).
