# Telegram Manager — обзор

## Назначение
Лаунчер для нескольких Telegram Portable: ищет `Telegram.exe` в подпапках рабочей папки, запускает по запросу и позволяет закрывать конкретные или все экземпляры. Показ номеров поверх окон помогает выбрать нужный аккаунт.

## Базовая папка
- Приоритет 1: аргумент `-workdir <path>` / `--workdir=<path>`.
- Приоритет 2: сохранённый путь `%APPDATA%\TelegramManager\workdir.txt` (сохраняется при успешном определении/выборе).
- Приоритет 3: `Environment.CurrentDirectory` (например, “Рабочая папка” ярлыка). Если отличается от exe — сохраняется.
- Приоритет 4: интерактивный выбор папки (FolderBrowser) если предыдущие варианты не подошли/равны папке exe.
- Далее — папка exe (fallback).

## Трeeвое меню
- «Открыть все аккаунты» — запускает все найденные `Telegram.exe` в подпапках.
- «Открыть аккаунт» — подменю, перечисляет подпапки с `Telegram.exe` по имени папки, запуск выбранного.
- «Открыть аккаунт группы» — подменю: группы → аккаунты; запуск выбранного.
- «Закрыть выбранный аккаунт» — подменю открытых экземпляров (Аккаунт 1, 2, …), при наведении выводит оверлеи с номерами на окнах; кликом закрывает выбранный.
- «Закрыть все аккаунты» — закрывает все известные экземпляры.
- «Выход» — скрывает трей, закрывает известные Telegram и завершает приложение.
- «Параметры запуска» → «Масштаб интерфейса» — ввод и сохранение значения для аргумента `-scale` (при запуске аккаунтов добавляется, если задано).

## Шаблоны
- В списке всегда есть базовый шаблон без кнопки: «Примет, скинь пожалуйста карточку компании (компания), не могуй найти». Назначьте ему хоткей.
- У базового шаблона 200 переформулировок: при вставке выбирается случайный вариант. Повторное нажатие Tab (без лимита по времени) заменяет текущий текст на новый вариант; можно жать Tab сколько угодно, пока текст не менялся руками.
- Назначайте клавиши в меню «Шаблоны». Нажатие выбранной клавиши → Tab вставляет текст шаблона.

## Оверлеи
- При наведении на «Закрыть аккаунт» или при открытии его подменю показываются метки с номерами на видимых окнах Telegram.
- Скрываются при уходе курсора/закрытии подменю/выходе.

## Архитектура
- `Program.cs` — точка входа, парсер `-workdir`, создание `TrayAppContext`.
- `TrayAppContext.cs` — меню, обработка событий, логика вызова менеджеров.
- `TelegramProcessManager.cs` — поиск `Telegram.exe`, запуск, фильтрация и закрытие процессов.
- `OverlayManager.cs`, `WindowOverlay.cs` — показ/скрытие оверлеев.
- `BaseDirectoryResolver.cs` — выбор рабочей папки.
- `IconFactory.cs` — значок трея.
- `NativeMethods.cs` — P/Invoke для оконных координат/видимости.
- Ресурсы: `assets/telegram-manager.ico` — значок приложения/ярлыка (прописывается в csproj).

## Сборка
`dotnet build -c Release` (TargetFramework: `net8.0-windows`). Предупреждение NETSDK1137 о смене SDK можно игнорировать или поменять Sdk на `Microsoft.NET.Sdk`.

## Release
Publish portable self-contained single-file build for Windows so users can run without extra runtimes.

- Profile: Properties/PublishProfiles/Portable.pubxml
- Command: dotnet publish -p:PublishProfile=Portable
- Output: bin/Release/net8.0-windows/win-x64/publish/
- Package: zip the publish folder for distribution

## Notes
Only subfolders that contain a tdata directory are considered valid account folders.

## ���������� ����������
����-���������� ����� ��������� ��������� ��������� GitHub Release �� ���� `v*` � ��������� portable-�����.

��������� ����� `app_update.json` � ����� ����� � exe:

{
  "RepoOwner": "OWNER",
  "RepoName": "REPO",
  "AssetName": "TelegramTrayLauncher-portable-win-x64.zip"
}

- `AssetName` ����� �� ���������: ����� ������ ������ asset � ��������� `portable-win-x64.zip`.
- ����� ���������� ����� ��������������� �� ��������� �����, ����� ���������� � ����� �������, ���������� ���������������.
- ����� ������������� ���������� ������������ ��������� ���� �� ������ ��������� (����������/����������/�����������).
- If the update script starts, the app forces exit as a fallback so the update can apply.
- On update errors a message is shown; details in pp_update.log.
- app_update.json is included in publish output; update RepoOwner/RepoName if the repository changes.

## ���������������
������ ����������� �������� � `TelegramTrayLauncher.csproj` (���� `<Version>`). ��� ������ ��� ������ ��������� � ���� ������� (��������, `v1.0.0`).

## Explorer folder context menu
- Added submenu `Telegram Manager` for directory right-click (`HKCU\Software\Classes\Directory\shell\TelegramManager`).
- Commands:
  - `--explorer-add-to-group "<folder>" "<group>"`
  - `--explorer-remove-from-group "<folder>"`
  - `--explorer-create-group-and-add "<folder>"`
  - `--explorer-delete-group "<group>"`
- Account-folder operations validate that folder contains both `Telegram.exe` and `tdata`.
- New modules: `ExplorerContextMenuManager.cs`, `ExplorerGroupCommandHandler.cs`.

## Explorer context menu update
- Removed group deletion action from Explorer menu.
- `Add to group` now includes default groups: `���������` and `������`.
- Telegram auto-update skips prerelease/draft entries when `VersionUrl` returns a releases array and prefers the latest stable release.

## Telegram update
- � ���� ���������: ��������� ���������� Telegram.
- ��� ������ ������ �� 3 ������� �������� ������; ��������� �� ������ ������������ ������ ��� ������ �������� ��� ����� 3 ������ ������.
- While updating, running Telegram windows are closed and then restarted.
- On update errors a message is shown; details in 	elegram_update.log.
