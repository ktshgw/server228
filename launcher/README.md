# Closed MVP launcher for official osu!lazer

Этот каталог содержит маленький проверяемый launcher для Windows x64. Он запускает обычный официальный `osu!.exe`, но передаёт endpoints приватного g0v0-сервера через EnhancedAuth.

Сам launcher **не умеет перенаправить неизменённый lazer**. Перенаправление делает сторонний ruleset [EnhancedAuth (LazerAuthlibInjection)](https://github.com/MingxuanGame/LazerAuthlibInjection); без проверенного DLL запуск намеренно блокируется.

Скрипты ничего не скачивают, не принимают логин или пароль официального osu!, не изменяют `hosts`/сертификаты и не выполняют MITM. Они:

- разрешают только HTTPS endpoints вне `ppy.sh`;
- проверяют Authenticode-подпись `osu!.exe` и издателя;
- допускают только явно перечисленные версии osu!;
- проверяют точный SHA-256 EnhancedAuth перед установкой и перед каждым запуском;
- всегда передают `--disable-sentry-logger`;
- скрывают OAuth client secret из собственного вывода;
- отказываются работать с профилем без приватного маркера, привязанного к Windows SID и API origin.

## Важное ограничение профиля

У release-сборки osu! нет поддерживаемого аргумента командной строки для отдельного data/profile каталога. В официальном коде имя release-хранилища фиксировано как `osu`, а `--debug-client-id` разрешён только debug-сборкам. Переназначение data folder делается внутри клиента через `storage.ini`, не launcher-аргументом.

Поэтому надёжный MVP-вариант — **отдельная локальная учётная запись Windows только для приватного сервера**. Это даёт отдельные `%APPDATA%`, `%LOCALAPPDATA%`, Realm-базу и OAuth-токены. Подмена переменных окружения или рабочей директории здесь не выдаётся за изоляцию: официальный клиент её не гарантирует. MVP намеренно принимает только `%APPDATA%\osu` текущего Windows-пользователя и останавливается, если `storage.ini` указывает на custom storage.

У framework также есть неявный portable-режим: наличие `framework.ini` рядом с `osu!.exe` переключает storage в каталог executable. Это не отдельный profile-аргумент, и launcher намеренно останавливается при таком sentinel, потому что иначе marker и EnhancedAuth из `%APPDATA%\osu` не относятся к фактически открытому хранилищу.

Не инициализируйте этот launcher поверх профиля, где вы входили в официальный osu!. Скрипт откажется создать marker в непустой директории, но он принципиально не читает `client.realm` и не может доказать, что внутри нет официального токена.

Рекомендуемый порядок:

1. Создать отдельного обычного (не Administrator) пользователя Windows и войти под ним.
2. Скопировать этот каталог и подготовить конфиг **до первого запуска osu!** под новым пользователем.
3. Создать marker приватного профиля.
4. Установить официальный lazer под этим Windows-пользователем и закрыть его, если installer запустил игру.
5. Установить проверенный EnhancedAuth и дальше запускать игру только этим launcher.

## Закреплённые версии

Пример закрепляет:

- EnhancedAuth `v2026.709.0`;
- файл `osu.Game.Rulesets.EnhancedAuth.dll`;
- опубликованный GitHub SHA-256 `99c2a22b555cba53a28ad1401861a30155cca26fe68cf5255559c2cb4a72cfd1`;
- официальный client release `2026.709.0-tachyon`, совпадающий по версии с EnhancedAuth;
- `ppy.osu.Game` `2026.702.1`, против которого фактически собран этот релиз EnhancedAuth.

Исходный release: <https://github.com/MingxuanGame/LazerAuthlibInjection/releases/tag/v2026.709.0>. Загрузите DLL вручную именно оттуда. Launcher URL не принимает и автоматических загрузок не делает.

Официальный build: <https://github.com/ppy/osu/releases/tag/2026.709.0-tachyon>. Опубликованный GitHub SHA-256 его Windows `install.exe`: `817eae8f9e1886b92c3d6b62e2ac3617b8a66b0bbe9a1379bd965a830ebde56a`. Launcher не устанавливает его сам и дополнительно требует валидную Authenticode-подпись ppy у конечного `osu!.exe`.

Версия `2026.709.0` выбрана как узкая пара с одноимённым release EnhancedAuth. Это официальный, но tachyon/pre-release клиент, а не обещание автора EnhancedAuth о поддержке всех последующих stable-релизов. Если фактический `osu!.exe` имеет другую версию, launcher остановится. Добавляйте версию в allow-list только после отдельного smoke-test полного цикла login → download → play → score → replay. Harmony-патчи EnhancedAuth могут ломаться при любом обновлении lazer.

## Настройка

Из PowerShell в этом каталоге:

```powershell
Copy-Item .\launcher.example.json .\launcher.json
notepad .\launcher.json
```

Замените `https://private-osu.example` на ваш публичный HTTPS-домен с доверенным TLS-сертификатом. `ApiUrl` и `WebsiteUrl` должны быть чистыми origins без path. Service URLs должны включать полный безопасный path; для приложенного nginx SignalR использует `/signalr/spectator`, `/signalr/multiplayer` и `/signalr/metadata`. `BeatmapSubmissionServiceUrl` нужен EnhancedAuth как параметр, но приложенный server не реализует BSS: `/beatmap-submission` является private placeholder и публикация карты из редактора работать не будет. Готового frontend также нет, поэтому `WebsiteUrl` на API origin не даёт обычный web-сайт. Компоненты можно позже вынести на разные private hosts. Query strings, fragments, credentials, HTTP и любые `*.ppy.sh` намеренно запрещены.

Проверьте путь к официальному `osu!.exe`. Типичный Velopack-путь указан в примере, но он может отличаться. Укажите только реально протестированную версию в `CompatibleVersions`.

`ClientId` и client secret принадлежат **вашему g0v0 OAuth client**, а не официальному аккаунту osu!. Пароль пользователя сюда никогда не вводится. Secret передаётся через переменную процесса:

```powershell
$env:PRIVATE_OSU_CLIENT_SECRET = "тот-же-client-secret-что-на-вашем-сервере"
```

EnhancedAuth получает secret в command line, поэтому локальный пользователь с правом просматривать процессы потенциально может его увидеть. Для native-клиента этот secret нельзя считать конфиденциальным; не переиспользуйте здесь административные или официальные секреты.

Сначала можно проверить только JSON, без файлов игры:

```powershell
.\Test-LauncherSetup.ps1 -ConfigurationOnly
```

## Первый запуск

Создание marker требует явного подтверждения и работает только для пустого data folder:

```powershell
.\Initialize-PrivateProfile.ps1 -ConfirmNoOfficialAccountData
```

После ручной загрузки EnhancedAuth:

```powershell
.\Install-EnhancedAuth.ps1 -SourceDllPath "C:\Downloads\osu.Game.Rulesets.EnhancedAuth.dll"
```

Полная проверка без запуска игры:

```powershell
.\Test-LauncherSetup.ps1
```

Запуск:

```powershell
.\Start-PrivateOsu.ps1 -ConfirmNoOfficialLogin
```

Подтверждение требуется при каждом запуске: marker фиксирует Windows SID и API, но не умеет заглянуть в Realm и проверить, не вошёл ли пользователь позже в официальный аккаунт.

Если локальная policy запрещает `.ps1`, используйте одноразовый процесс без изменения системной политики:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Start-PrivateOsu.ps1 -ConfirmNoOfficialLogin
```

Не запускайте PowerShell от администратора. Не запускайте официальный shortcut одновременно: release-клиент однопроцессный, и второй запуск может не применить EnhancedAuth arguments.

Не используйте внутри этого профиля настройку `Change folder location`: stock launcher не получает data-path аргумент и не сможет проверить перенесённое хранилище.

## Обновление

Launcher не обновляет osu! или EnhancedAuth. Любое обновление — осознанная смена двух pin'ов:

1. Проверить новый DLL и получить SHA-256 из GitHub release metadata.
2. Проверить, против какой версии `ppy.osu.Game` он собран.
3. Протестировать точную official build без настоящего osu! аккаунта.
4. Изменить `Version`, `Sha256` и `CompatibleVersions`.
5. Установить через `Install-EnhancedAuth.ps1 -Replace`; старый DLL останется в `rulesets\private-launcher-backups`.

Official updater также нельзя считать частью этого MVP: обычный запуск osu! без launcher может обновить binary, после чего проверка версии закономерно остановит private launch.

## Самопроверка скриптов

Тесты не требуют Pester и не запускают osu!:

```powershell
.\tests\Launcher.Tests.ps1
```

Они проверяют HTTPS/ppy.sh guards, strict origins и service paths, правильный `%APPDATA%\osu`, отказ от portable sentinel, SHA-256 validation, скрытие secret и состав redirect-аргументов.

## Что этот MVP пока не гарантирует

- EnhancedAuth остаётся неофициальным Harmony-based модулем и может перестать загружаться после обновления osu!.
- `--disable-sentry-logger` реализован самим EnhancedAuth через runtime reflection после загрузки ruleset; launcher передаёт флаг, но не может доказать отсутствие ранней телеметрии или успешное отключение при изменении внутренних полей клиента.
- Launcher проверяет конфигурацию и файлы, но не доказывает полноту перенаправления каждого сетевого запроса внутри DLL.
- Он не является sandbox и не изолирует данные внутри одной Windows-учётки.
- One-click beatmap download обслуживается приложенным private backend через сторонние зеркала; launcher лишь направляет API endpoints и не проверяет загруженный `.osz`.
- Публикация карты из редактора (BSS) и отдельный web-сайт не реализованы.
- Клиентские проверки не заменяют серверную авторизацию, rate limits или античит.

Для закрытого круга друзей этот путь подходит как PoC. Для долгоживущего сервера лучше перейти на минимальный собственный fork g0v0 client, чтобы endpoints и отдельное имя хранилища были частью проверяемого исходного кода, а не runtime-патчем.
