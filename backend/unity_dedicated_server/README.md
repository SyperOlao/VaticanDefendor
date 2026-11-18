# Unity Dedicated Server: Linux

Используется существующий `NetworkPrototypeBootstrap` → `ServerCompositionRoot`, один Server World на процесс. Новых игровых серверов, RPC, копий AI/движения или presentation нет. Unity 6000.6.6f1 и пакеты сохранены.

**Статус 17.11.2026:** механизм Linux build и Docker-упаковка реализованы. Linux Unity Player и конечный образ с ним **не проверены**: на доступной машине установлен только `MacStandaloneSupport`. Реальные Mac Dedicated Server/client builds, сетевое подключение и остановка проверены; результаты ниже.

## Сборка

В Unity Hub для **6000.6.6f1** нужен Linux Dedicated Server Build Support и поддержка выбранного scripting backend. Runtime-образ Editor не содержит. Установка модулей и лицензия нужны только сборочной машине.

Из корня Unity-проекта, при закрытом Editor этого проекта:

```sh
UNITY=/opt/Unity/Editor/Unity
"$UNITY" -batchmode -nographics -quit \
  -buildTarget Linux64 -standaloneBuildSubtarget Server \
  -projectPath "$PWD" \
  -executeMethod VaticanDefendor.Editor.NetworkPrototypeBuild.BuildLinuxServer \
  -logFile /tmp/vatican-linux-build.log
```

В интерактивном Editor: выбрать **Linux Dedicated Server** в Build Profiles, дождаться перекомпиляции, затем `VaticanDefendor/Network/Build Linux Dedicated Server`.

`BuildLinuxServer` вызывает общий `BuildPipeline.BuildPlayer` с `BuildTarget.StandaloneLinux64` и `(int)StandaloneBuildSubtarget.Server`. Это Dedicated Server, а не desktop Player с отключённым рендерингом. Предварительно проверяются установленная платформа, активная Linux-платформа и загруженная Server-роль Editor. Защита от ошибочного SubScene baking после переключения Server/Client сохранена.

Результат:

```text
backend/unity_dedicated_server/build/linux-server/
  VaticanDefendorServer.x86_64
  VaticanDefendorServer_Data/
  UnityPlayer.so
  ... остальные native/runtime-файлы Unity ...
  server-build.json
```

Сохраняйте **весь** output, включая StreamingAssets/EntityScenes, native plugins и MonoBleedingEdge, если он создан выбранным backend. Нельзя упаковать только executable. `server-build.json` записывается после успешной сборки: Unity version, platform/subtarget, Development flag, bundled ContentRelease и protocol version. Сборка остаётся Development, как существующие Windows/Mac методы; это не production-hardening.

Windows `BuildServer`/`BuildClient` по-прежнему пишут в `Builds/Stage2`; `BuildMacServer`/`BuildMacClient` — в `Builds/OnlineMac`. Для batch-сборок любой платформы роль Editor выбирается до executeMethod: `-standaloneBuildSubtarget Server` либо `Player`.

Server build включает только существующую `Assets/ActiveCombat/Network/NetworkPrototype.unity` с auto-load сетевой SubScene; `ActiveCombat` с UI/камерами добавляется только клиенту. В основной серверной сцене нет Camera, Canvas, AudioListener/AudioSource. `CreateServerWorld` выбирает ServerSimulation systems; собственные client presentation/animation системы защищены прежним `!UNITY_SERVER || UNITY_EDITOR`. Симуляция, ECS-компоненты, physics modules, Transport и общие bakers не удаляются. Audio subsystem отключается самим Dedicated Server target; клиентские Entities Graphics и GameObject presentation сохраняются.

## Упаковка

После успешной Linux-сборки, из корня проекта:

```sh
docker build --platform linux/amd64 --target server \
  --iidfile /tmp/vatican-server-image.id \
  -t vatican-dedicated-server:local backend/unity_dedicated_server
```

Образ на Ubuntu 24.04 предназначен для Linux x86_64. Проверяются manifest с Server subtarget, ELF64/x86_64 executable, Data/boot.config, UnityPlayer.so и отсутствующие библиотеки через `ldd`. Неполный, Mac или Windows output отклоняется. Это проверка формата артефакта, а не замена запуска Unity.

Runtime содержит системные библиотеки, CA certificates, готовый output и небольшой POSIX supervisor. Нет Unity Editor, X11/Xvfb, desktop/audio-демонов или графического клиента. Процесс работает под UID/GID 10001. Контекст Docker ограничен entrypoint и build/linux-server; `.env`, PEM/key и каталоги отладочных символов не включаются. Секреты и private keys передаются только при запуске.

## Параметры и online-запуск

Используются прежние `NetworkLaunchArguments`, `OnlineStartup`, `SecureGameDriver`, backend gateway и журнал результатов:

| Параметр | Назначение |
| --- | --- |
| `-vdRole server` | Supervisor задаёт серверную роль; Dedicated Player также выбирает её по `UNITY_SERVER` по умолчанию |
| `-vdBind`, `-vdPort` | IPv4 bind и UDP-порт. Для контейнера нужен `0.0.0.0`, а не внутренний loopback |
| `-vdBackendUrl` / `VD_BACKEND_URL` | Backend HTTPS URL. HTTP разрешён прежним `-vdInsecureLocalBackend` только для loopback; имя Docker-сервиса не является loopback |
| `-vdServerId`, `VD_SERVER_SECRET` | UUID и секрет существующего backend pool slot. Секрет не передаётся аргументом и не печатается |
| `VD_CONTENT_RELEASE_ID` | Закреплённый ContentRelease: существующая загрузка `/v1/content/{id}` и hash/schema validation. Без переменной используется bundled release из NetworkContent |
| `-vdDtls`, `VD_DTLS_CERT_FILE`, `VD_DTLS_KEY_FILE` | DTLS для non-loopback online bind; PEM-файлы монтируются read-only |
| `-vdDevelopmentIdentity`, `-vdRunId`, `VD_DEV_TICKET`, `VD_DEV_ACCOUNT_ID` | Явный тестовый режим без backend, доступный только Development Build |
| `VD_RUN_RESULT_DIR` | Существующий durable pending-results journal; в образе `/var/lib/vatican/pending-results` |
| `VD_CHECKPOINT_SECONDS`, `VD_SESSION_*` | Прежние checkpoint/retention настройки и их валидация |
| `-vdShutdownFile` | Добавленный служебный параметр существующего parser. Supervisor владеет уникальным временным путём; не задавайте его вручную при запуске через wrapper |

Подготовьте вне Git `/secure/vatican-server.env`, доступный только оператору:

```dotenv
VD_BACKEND_URL=https://api.example.com
VD_SERVER_ID=<UUID существующего pool slot>
VD_SERVER_SECRET=<секрет этого slot>
VD_CONTENT_RELEASE_ID=sha256-<64 hex символа>
VD_SERVER_PORT=7979
VD_DTLS_CERT_PATH=/secure/game-server.crt
VD_DTLS_KEY_PATH=/secure/game-server.key
```

Файлы сертификата и ключа должны быть читаемы UID 10001 внутри контейнера. Значения в примере — placeholders, не рабочие credentials.

```sh
docker compose --env-file /secure/vatican-server.env \
  -f backend/unity_dedicated_server/compose.yaml up --build -d
docker compose --env-file /secure/vatican-server.env \
  -f backend/unity_dedicated_server/compose.yaml logs -f unity_server
```

Compose запускает только Unity: существующий FastAPI/PostgreSQL стек поднимается своим текущим механизмом. Pool slot backend должен публиковать **доступный клиенту** host/UDP port и имя DTLS-сертификата, соответствующие этому процессу; `0.0.0.0` не является адресом назначения клиента. Не запускайте два процесса с одним ServerId и одним журналом.

`[Server] Listening ... waiting for backend assignment` подтверждает bind и объявление готовности backend, но не назначение забега. `[Server] Backend assigned run=...` подтверждает назначение. UDP healthcheck не заменяет content/protocol/assignment readiness. В этой задаче не добавлен фиктивный HTTP health endpoint и не изменён backend assignment contract.

## Smoke без backend

После сборки образа, из корня проекта:

```sh
export VD_DEV_TICKET="$(python3 -c 'import secrets; print(secrets.token_hex(32))')"
VD_SMOKE_RUN_ID="$(python3 -c 'import uuid; print(uuid.uuid4())')"
docker run --rm --name vatican-linux-smoke --stop-timeout 30 \
  -p 127.0.0.1:7979:7979/udp \
  --env VD_DEV_TICKET --env VD_DEV_ACCOUNT_ID=linux-smoke \
  vatican-dedicated-server:local \
  -vdBind 0.0.0.0 -vdPort 7979 \
  -vdDevelopmentIdentity -vdRunId "$VD_SMOKE_RUN_ID"
```

Запустите существующий клиент с тем же ticket/RunId, `-vdRole client -vdHost 127.0.0.1 -vdPort 7979 -vdDevelopmentIdentity`. Для headless-проверки добавьте `-batchmode -nographics -vdMove 0.5,0`; графический UI запускается обычным путём. Критерий: approval, Restored run, реплицированные Ghost, изменение серверной позиции; процесс, который просто остаётся жив, не считается успешным smoke. Тестовый UDP bind публикуется только на host loopback, не в публичный интернет.

## Остановка и состояние

```sh
docker stop --time 30 vatican-linux-smoke
```

Для Compose используйте `stop --timeout 30 unity_server` с теми же `--env-file` и `-f`. Состояние хранится в named volume `server_state`; удалять его при обычном рестарте не нужно.

Supervisor PID 1 ловит SIGTERM/SIGINT и создаёт request-файл. `ServerRunLifetime` проверяет его вне главного потока: это позволяет отменить синхронное ожидание assignment/recovery/journal HTTP и retry, пока Unity main thread занят startup. При активном World запрос обрабатывается главным потоком: `NetworkStreamRequestDisconnect`, короткий drain существующего Transport loop, `Application.Quit(0)`. Bootstrap сначала уничтожает серверные сервисы, затем удаляет World из PlayerLoop и освобождает его. Heartbeat останавливается, pending checkpoint/result/retention requests отменяются и наблюдаются. Supervisor ждёт дочерний процесс, сохраняет его exit code и удаляет только собственный временный request-файл.

При загрузке контента до создания Server World остановка обрабатывается существующим `OnlineStartup`. При ошибке старта/падении дочернего процесса supervisor сохраняет ненулевой exit code. SIGKILL и превышение container stop timeout не позволяют обещать graceful cleanup.

Для запуска готового Linux output без Docker используйте тот же wrapper:

```sh
VD_SERVER_EXECUTABLE="$PWD/backend/unity_dedicated_server/build/linux-server/VaticanDefendorServer.x86_64" \
  sh backend/unity_dedicated_server/entrypoint.sh \
  -vdBind 0.0.0.0 -vdPort 7979 -vdDtls -vdServerId "$VD_SERVER_ID"
```

Сигналы направляются supervisor, а не его дочернему Unity PID. Native Linux signal handlers Unity в этой среде не проверены; wrapper не зависит от их поведения. `VD_SERVER_EXECUTABLE` также позволяет проверить этот же supervisor с существующей Mac Dedicated Server-сборкой.

Shutdown не завершает матч искусственно, не выдаёт награды и не создаёт новый финальный checkpoint. Уже записанный result journal сохраняется; восстановление активного забега остаётся в рамках существующего периодического checkpoint и fencing. Известный конфликт checkpoint при неизменном tick (A2 аудита) этой задачей не исправлен.

Последующее обновление ECS authority: `backend/docs/unity_dedicated_server/SERVER_CLIENT_AUTHORITY.md` и раздел 10.3 аудита. Ошибки StableId/inactive palette из приведённого ниже исторического прогона исправлены; последующий Mac графический smoke не имел managed exceptions. Это не меняет статус непроверенного Linux Player.

## Реальные проверки 17.11.2026

Все Unity-проверки выполнялись в изолированной копии с текущими исходниками; сцены, prefab, GUID, `.meta`, `.asmdef`, Unity и пакеты рабочей копии не менялись.

| Проверка | Результат |
| --- | --- |
| Mac Dedicated Server и Mac Client builds | Оба успешны, exit 0: реально скомпилированы Server/Client варианты assemblies |
| Linux build preflight в установленном Editor | Ожидаемый exit 1: `StandaloneLinux64 build support is not installed`; Linux output не создан |
| Mac server + существующий headless client через этот supervisor | Два свежих сеанса: approval/Restored, движение; остановка SIGTERM и SIGINT за ~0.49/~0.51 s, server exit 0, клиент получил `ClosedByRemote`, UDP-порт освобождён, World/services disposal записаны в логах |
| Mac Server без headless flags | Без `-batchmode`, `-nographics` и `-vdRole` выбрана серверная роль, в логе `Forcing GfxDevice: Null` / `Renderer: Null Device`; Listening, процесс жив до SIGTERM, exit 0 |
| Остановка до назначения забега | Настоящий Mac Dedicated Server с HTTP fixture, возвращающей unassigned: синхронное ожидание отменено по SIGTERM за ~0.31 s, exit 0, порт освобождён. Это не проверка настоящего FastAPI |
| Другие стадии startup с HTTP fixture | SIGTERM отменил recovery-pending (~0.22 s), journal replay/retry (~0.22 s, pending файл сохранён) и content download (~0.12 s); все exit 0. Backend-семантика не проверяется fixture |
| Два локальных Editor Play/Stop | PASS: по 182 Ghost, input двигает серверного героя, сетевые Worlds освобождены; managed client/render systems на Server World не найдены |
| `python3 -m unittest discover -s backend/unity_dedicated_server/tests -v` | 2 tests passed: TERM/INT protocol и reap дочернего процесса; ненулевой exit code сохраняется. Shell fixture, не Unity |
| Docker runtime stage, linux/amd64 | Собран успешно; реальный `docker stop` с shell fixture, UID 10001, exit 0, ~0.14 s. Проверен supervisor, не Linux Player |
| Docker final stage без build output | Ожидаемый отказ: отсутствует Linux payload; пустой образ не объявляется готовым |
| Compose config с временными тестовыми значениями | `docker compose ... config --quiet`: exit 0, без вывода секретов |

Linux Dedicated Server build, его нативные библиотеки/physics/Transport, конечный Unity-образ, реальное подключение клиента к нему и его shutdown **остаются непроверенными из-за отсутствующего Linux build module**. Windows-сборка не запускалась. Общий полный графический smoke по-прежнему блокируют ошибки UI/навигации из раздела 10.1 аудита; эта задача их не меняла. В Player shutdown логах остаётся прежнее предупреждение `Persistent allocates 4 individual allocations`; освобождение World подтверждено, отсутствие всех утечек — нет.

Артефакты текущих проверок: `/Users/syper_olao/.codex/visualizations/2026/11/17/01a1e99f-92a1-70c0-b919-a2f1ff2ec087/linux-dedicated-server/`.

## Изменённые файлы этой задачи

| Файл | Изменение |
| --- | --- |
| `Assets/Scripts/Editor/NetworkPrototypeBuild.cs` | Linux Server entry point, platform preflight, output и manifest; прежние Win/Mac методы сохранены |
| `Assets/Scripts/Infrastructure/Networking/Contracts/NetworkLaunchArguments.cs` | Необязательный absolute `-vdShutdownFile` в существующем parser |
| `Assets/Scripts/Bootstrap/Server/ServerRunLifetime.cs` | Общий lifecycle существующих сервисов, stop monitor, disconnect/drain, cleanup |
| `Assets/Scripts/Bootstrap/Server/ServerCompositionRoot.cs` | Lifetime создаётся до backend wait; token передаётся gateway/journal, development retention также принадлежит lifecycle |
| `Assets/Scripts/Bootstrap/Network/NetworkPrototypeBootstrap.cs` | Уничтожение серверных сервисов перед World disposal, graceful startup cancellation |
| `Assets/Scripts/Bootstrap/Network/OnlineStartup.cs` | Остановка при asynchronous content startup и наблюдение cancelled startup |
| `Assets/Scripts/Infrastructure/Backend/BackendGateway.cs` | Отмена server startup HTTP/poll/recovery; idempotent Dispose с ожиданием heartbeat |
| `Assets/Scripts/Infrastructure/Backend/RunResultJournal.cs` | Отменяемый startup replay/retry с сохранением pending journal |
| `Assets/Scripts/Bootstrap/Server/RunCheckpoint.cs` | Отмена и наблюдение in-flight upload при уничтожении сервиса |
| `backend/unity_dedicated_server/{Dockerfile,.dockerignore,.gitattributes,entrypoint.sh,compose.yaml}` | Runtime packaging, LF shell checkout, supervisor и параметры deployment |
| `backend/unity_dedicated_server/tests/test_entrypoint.py` | Проверки сигналов, reap и exit code |
| `backend/unity_dedicated_server/README.md`, `backend/docs/unity_dedicated_server/{NETWORK_RUN.md,DEDICATED_SERVER_AUDIT.md}` | Запуск, область проверки и обновление аудита |

Чужие изменения в этих файлах сохранены; общий `git diff` относительно HEAD включает и более раннюю работу.

API и условия платформы сверены с документацией Unity 6000.6: [Dedicated Server build](https://docs.unity.com/en-us/engine/6000.6/manual/platform-specific/dedicated-server/build), [установка Server module](https://docs.unity.com/en-us/engine/6000.6/manual/platform-specific/dedicated-server/get-started/requirements), [автоматические оптимизации Server target, включая audio](https://docs.unity.com/en-us/engine/6000.6/manual/platform-specific/dedicated-server/get-started/optimizations), [поддерживаемые Server platforms](https://docs.unity.com/en-us/engine/6000.6/manual/get-started/install-and-upgrade/getting-started-installing-unity/system-requirements).


## Частоты симуляции и проверка движения

Существующий parser принимает `-vdSimulationHz 60 -vdSnapshotHz 30` или `VD_SIMULATION_HZ=60 VD_SNAPSHOT_HZ=30`. Simulation: 30..120 Hz; snapshot: 10..simulation Hz, делитель simulation. Compose передаёт обе переменные. Клиент получает действительную частоту от сервера при Netcode handshake. Transport queues и MTU прежние. После изменений Ghost metadata сервер и клиент необходимо пересобрать вместе.

Клиентские параметры interpolation/extrapolation и ограничение ближайшего enemy prediction, команды графической матрицы и результаты: [NETWORK_MOTION_VALIDATION.md](../docs/unity_dedicated_server/NETWORK_MOTION_VALIDATION.md). Стенд использует существующие Development Server/Client players; Unity Editor в runtime container для этого не требуется. Для проверки только конфигурации можно запускать сервер с прежними development identity / shutdown-file флагами и изменённой частотой; журнал `[NetworkMotion]` показывает выбранные значения.
