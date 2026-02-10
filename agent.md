# Telegram Manager – Agent Guide

- Release: build a portable self-contained single-file app so Windows users can run without extra runtimes.

- Всегда начинай с чтения документации: `docs/overview.md`. Обновляй её при любых изменениях поведения/команд/меню.
- Не трать контекст чата на вопросы, ответы уже могут быть в `docs`.
- После изменения логики: синхронизируй описание в `docs` и упомяни новые флаги/пункты меню/пути.
- Если добавляешь новые файлы/папки/аргументы — зафиксируй это в документации сразу.
- Соблюдай существующую структуру: точка входа (`Program.cs`), треевый UI (`TrayAppContext.cs`), процессы (`TelegramProcessManager.cs`), оверлеи (`OverlayManager.cs` + `WindowOverlay.cs`), P/Invoke (`NativeMethods.cs`), иконка (`IconFactory.cs`), базовая папка (`BaseDirectoryResolver.cs`).
- Ресурсы: иконка приложения в `assets/telegram-manager.ico` (указана как ApplicationIcon в csproj).
- Настройки: `SettingsStore.cs` хранит (в `%APPDATA%/TelegramManager/settings.json`) масштаб `-scale`, используемый при запуске аккаунтов; обновляй описание при добавлении новых параметров.
- Не комить без явного разрешения пользователя.
- Release checklist: verify local app version, update all required places; only push when user explicitly asks.
- Before each push/release, update About ("Последние изменения") to include all changes in that release.
- Always preserve correct file encodings (especially legacy cp1251/Windows-1251 files). Do not introduce encoding corruption; use proper encoding when editing.
