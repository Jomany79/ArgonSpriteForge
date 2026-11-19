# ARGON SPRITE FORGE (.NET 8 WinForms + WebView2 + ONNX Single-File EXE)

Нативное Windows-приложение на **C# / .NET 8**, собираемое в **один самодостаточный файл `ArgonSpriteForge.exe`** (весь Web UI встроен внутрь `.exe` как `EmbeddedResource`, без Python и без внешних DLL рядом с файлом).

## Возможности
- **Нативное окно Windows (WinForms + WebView2)**: тёмная рамка окна (DWM Dark Mode), системный диалог `SaveFileDialog` при экспорте PNG/ZIP, кнопки быстрого открытия папок `projects/` и `models/` в Проводнике.
- **4-агентный конвейер покадровой анимации (Gemini REST API)**:
  - **Агент 1 (Режиссёр)**: строит паспорт персонажа и покадровую раскадровку (4–24 кадра).
  - **Агент 2 (Художник кадров)**: генерирует кадры с двойной привязкой к эталону и предыдущему кадру; точечная перегенерация одного кадра.
  - **Агент 3 (Инспектор QA)**: математический расчёт `MAD`, 4-сторонних полей `Zero Clipping` + опциональный Vision QA с авто-повтором брака.
  - **Агент 4 (Стабилизатор на чистом C# + ONNX Runtime)**:
    - Быстрый managed-движок **C# Studio CV** (построчная оценка фона, отделение тёплого меха от холодных теней `R - B`, BFS-заливка бликов глаз, очистка мусора связных компонент, премультиплицированный **Lanczos-3** аффинный ресемплинг и `Pivot Lock`).
    - **ONNX Matting (`Microsoft.ML.OnnxRuntime`)**: положите любую `.onnx`-модель удаления фона (`u2net.onnx`, `u2netp.onnx`, `isnet-general-use.onnx`, `rmbg-1.4.onnx`) в папку `models/` рядом с `.exe` (кнопка **`models/`** в правой панели UI), нажмите **`↻`** и выберите режим **ONNX Matting**.
    - **Офлайн-слайсер (без API-ключа)**: нарезка листов (`Авто-поиск` или `Сетка`) сразу в RGBA + Pivot Lock.
- **Экспорт**: Lossless PNG (сетка / стрип), `atlas_grid.json`, `atlas_strip.json`, отдельные PNG и анимированный Lossless WebP в ZIP-бандле.

## Сборка одного файла `ArgonSpriteForge.exe`

### Вариант 1 — Автосборка в GitHub Actions (без установки SDK на ПК)
1. Создайте репозиторий на GitHub и загрузите туда содержимое этой папки (включая `.github/workflows/build-exe.yml`).
2. Откройте вкладку **Actions** → **Build ArgonSpriteForge Single-File EXE** (запустится автоматически при пуше в `main` или вручную кнопкой **Run workflow**).
3. Скачайте готовый **`ArgonSpriteForge.exe`** из блока **Artifacts** (`ArgonSpriteForge-win-x64`).
4. При пуше тега (например `v1.0.0`) файл автоматически прикрепится к GitHub Release.

### Вариант 2 — Локальная сборка на Windows
1. Установите [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Запустите **`build_exe.bat`** — готовый одиночный файл появится в `publish\win-x64\ArgonSpriteForge.exe`.
