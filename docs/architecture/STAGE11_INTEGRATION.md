# Этап 11: воспроизводимый запуск и измерения

## Сборки и локальный backend

Рабочая копия: `feature/prototype-anna`, Unity `6000.6.6f1`, Entities / Entities Graphics / Netcode for Entities / Unity Transport `6.6.0`. Команды создают отдельные Windows Development Build в `Builds/Stage2/Server` и `Builds/Stage2/Client` (историческое имя каталога сохранено). Сборки выполнять последовательно, из корня проекта. Последняя пара после ограничения клиентского FPS завершилась `Build Finished, Result: Success` в `Builds/Stage11-{server,client}-fpscap.log`; smoke этой пары `load-20261102-041007` прошёл с отдельными server/client, approval и 32 врагами за 15 секунд:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe' -batchmode -nographics -quit -projectPath 'F:\projects\VaticanDefendor' -executeMethod VaticanDefendor.Editor.NetworkPrototypeBuild.BuildServer -logFile 'F:\projects\VaticanDefendor\Builds\Stage11-server-build.log'
& 'C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe' -batchmode -nographics -quit -projectPath 'F:\projects\VaticanDefendor' -executeMethod VaticanDefendor.Editor.NetworkPrototypeBuild.BuildClient -logFile 'F:\projects\VaticanDefendor\Builds\Stage11-client-build.log'
```

Локально PostgreSQL 16 работает в WSL на `127.0.0.1:5432`; отдельная тестовая БД `vatican_stage11` создана с владельцем `vatican`. Альтернатива с Docker Compose: `backend/scripts/start-local.ps1 -ContentReleaseId <ID> -RewardPerChurchLevel <утверждённое значение>`, команда поднимает БД и API. `backend/scripts/start-api-local.ps1` применяет миграции и запускает API при уже работающей PostgreSQL. Пример без секретов: `backend/.env.example`. Стендовые секреты и guest token создаются в игнорируемой Git `backend/.runtime/`. Значение `7` в нижних командах — **тестовая ставка**, не продуктовое решение.

```powershell
backend/scripts/start-api-local.ps1 -DatabaseName vatican_stage11 -ApiPort 8082 -ContentReleaseId 'sha256-962228240007ddb1729e204402e3d8b5e58e4123fcfb9557b6afbfd085aead77' -RewardPerChurchLevel 7
backend/scripts/run-stage9-server.ps1 -ApiPort 8082 -CheckpointSeconds 10 -Metrics -LogName stage11-server.log
backend/scripts/run-stage9-client.ps1 -ApiPort 8082 -GuestFile stage11-guest.json -Build 'structure.wall,2,0,4,0' -AttackEvery 0.5 -Metrics -LogName stage11-client.log
```

API `/health/ready` должен вернуть `ready`; клиент получает профиль и назначение от backend, сервер регистрируется и получает тот же RunId. Повторный запуск server-процесса с устаревшим назначением без checkpoint явно получает HTTP 409 `No recoverable checkpoint for assigned run`. Для нового чистого стенда нужен новый серверный слот или отдельная тестовая БД; исторический забег не удаляется автоматически.

## Сквозной прогон

`Builds/Stage9/stage11-server-2.log` и `stage11-client-writer-fix-2.log`: отдельные API, dedicated server и client. RunId `a8f2d1ea-da95-4286-90c2-aa9324499a83`; клиент получил профиль и право, построил 3 клетки стены (`gold=120`), убил цель (`damage=30+23`, `gold=128`), затем сервер завершил забег поражением при разрушении церкви. Pending-result был записан на диск и применён backend: `reward=7`, `profileVersion=2`. Запрос `/v1/profile` и SQL подтвердили `permanent_currency=7`, одну строку `run_result_operations`, статус `completed`. Подтверждено начисление за достигнутый уровень церкви при обоих исходах; этот прогон проверяет поражение. Сумма ставки для продукта ещё не утверждена.

## Нагрузка и инструментирование

Измерительный стенд `backend/scripts/measure-stage11.ps1` запускает от 1 до 6 независимых пар server/client на разных UDP-портах с разными RunId и development ticket. Этот путь явно использует Development identity и не проверяет массовое назначение через backend. Каждая пара обслуживает одного игрока. `backend/scripts/summarize-stage11.py <каталог>` сводит CSV и логи. Размеры `small/medium/large`: 32/128/512 врагов на забег; это уровни эксперимента, не заявленная ёмкость.

```powershell
backend/scripts/measure-stage11.ps1 -Runs 6 -Level small -DurationSeconds 30 -FirstPort 8220 -Move '0.5,0'
backend/.venv/Scripts/python.exe backend/scripts/summarize-stage11.py Builds/Stage11/load-<timestamp>
backend/scripts/measure-stage11.ps1 -Runs 1 -Level small -DurationSeconds 30 -FirstPort 8230 -Move '0.5,0' -NetworkSimulatorProfile backend/scripts/stage11-network-simulator.json
```

Оборудование: AMD Ryzen 9 9950X3D, 16 физических / 32 логических ядра, 31,1 GiB RAM, NVIDIA RTX 5080, Windows 11. Сборки Development, Windows x64; dedicated без графики. Включённый `-vdMetrics` пишет интервальные значения UTP `NetworkDriver.GetStatistics()` (байты L3+, максимальное заполнение очередей), GC и frame time. `[ServerTick]` измеряет время ECS server tick. CPU из Windows `Get-Process` — процент одного логического ядра, память — Working Set и Private Bytes каждого Unity-процесса. Первые интервалы содержат запуск/загрузку; сравнивать steady intervals отдельно. Headless frame time характеризует цикл клиента без карты и 3D и не заменяет графический FPS. Время старта/подключения отмечено в `events.csv` с дискретностью наблюдения до 2 секунд; `Stop-Process` в стенде измеряет принудительное завершение, не graceful teardown.

Подтверждённый активный прогон `Builds/Stage11/load-20261102-035543`: 6 разных RunId, 12 процессов, 32 врага в каждом забеге, 30 секунд после запуска клиентов. Все получили approval; не было crash или terminal state. На один server-процесс: CPU 4,54–8,74% одного логического ядра, пик Working Set 266,2–267,3 MiB, Private Bytes до 480,3 MiB, средний ECS tick 0,181–0,199 мс, максимальный отдельный tick 29,5–33,6 мс (включая запуск), GC allocation 0,65–0,71 MiB за интервал, по одному сбору каждого поколения. UTP TX 0,18–0,22 MiB, RX 0,14–0,17 MiB; пики очередей RX 2–3, TX 1. На один headless client: CPU 5,39–7,69% ядра, Working Set до 274,3 MiB, Private Bytes до 505,9 MiB, GC allocation 0,68–0,75 MiB. P95 headless кадра 16,67 мс при лимите 60 FPS. `events.csv` и `summary.json` хранят отдельные процессы и время запуска/подключения/остановки. Суммарный Private Bytes 12 процессов порядка 5,8 GiB; стоимость «процесс на забег» требует отдельного решения после более долгого прогона и измерения без Development/metrics.

Отдельные прогоны `load-20261102-035912` и `load-20261102-035937`: один забег с 128 и 512 врагами соответственно, по 15 секунд, без crash/terminal. 128: server CPU 7,30% ядра, tick 0,242 мс, UTP TX 0,29 MiB; client CPU 6,40%, RX 0,29 MiB. 512: server CPU 11,11%, tick 0,408 мс, TX 1,00 MiB, Private Bytes 519,6 MiB; headless client CPU 9,49%, RX 1,00 MiB, Private Bytes 553,0 MiB. Это короткие измерения трафика всего транспорта, не частоты/ошибки каждого ghost. Ранний 45-секундный прогон `load-20261102-034050` закончился поражением на ~43-й секунде и не считается 45 секундами активной нагрузки.

Графический Windows-клиент с 128 врагами проверен в окне `Active Combat` вместе с картой и нижней 3D-областью (`graphic-capped-client.log`). Измерение до исправления показало отсутствие лимита кадров: ~740 FPS, до 77,5 MiB GC allocation / 5 с. После установки 60 FPS в клиентском bootstrap и успешной пересборки `Stage11-client-fpscap.log`: p95 16,67 мс, максимум 16,74 мс в стабильном интервале, но по-прежнему ~6,17 MiB GC allocation и 4–5 сборок каждого поколения / 5 с при 128 врагах. Это конкретный оставшийся hotspot клиентского read model/представления; headless цифры нельзя использовать как графическую оценку. На GPU RTX 5080 тест был в небольшом окне ~308×665, поэтому p95 не является мобильной оценкой.

## Границы и проверка репликации

Вражеский ghost интерполируется на клиенте; сервер отправляет `LocalTransform`, `CombatHealth` и роль цели. AI, навигация, RNG и authoritative атака остаются только на сервере. Клиентский `NetcodeClientSessionPort` читает один набор ghost и передаёт его карте и 3D-представлению. Серверное отсечение только по фрустуму нижней камеры не включено: карта также нуждается во врагах. `TargetGhost.prefab` сейчас `OptimizationMode=Dynamic`, `MaxSendRate=0` (нет отдельного лимита prefab); фактическая частота и ошибка координат при 128/512 врагах требуют отдельного измерения. Snapshot profiler counter доступен только при `NETCODE_DEBUG`; UTP-трафик включает также команды и RPC, поэтому не выдаётся за размер одних ghost.

Netcode asset `Assets/Resources/NetworkTransportLimits.asset` задаёт очереди per pipeline stage: client receive/send 64/16, server 64/64 пакетов; максимальный размер сообщения оставлен 1400 байт. Сервер принимает за tick максимум 8 attack RPC и 4 construction RPC; лишние уничтожаются с агрегированным предупреждением раз в 5 секунд. Существующие ограничения длины стены, operation ID и частоты строительства остаются. Эти лимиты не доказывают устойчивость к произвольному потоку пакетов.

Первый запуск с `NETCODE_DEBUG` обнаружил баг ghost map / пустого chunk в Unity Entities/Netcode 6.6: все 6 debug-клиентов аварийно завершились сразу после появления hero ghost (`Builds/Stage11/load-20261102-034644`). Эта сборка не используется как рабочая. При обычной сборке повторный ECS query hero ghost также иногда попадал в недействительный chunk; клиентское движение теперь ищет принадлежащий hero через публичную `SpawnedGhostEntityMap`, проверяет существование Entity и кэширует его. Финальный backend reconnect и шесть независимых запусков после исправления прошли. Корневая причина неконсистентной ghost map в пакете остаётся не выясненной.

`load-20261102-035646` проверил Development UTP simulator: лог клиента подтверждает `delay=100±10ms drop=1% duplicate=1%`, approval и 32 врага в течение 25 секунд без crash/terminal. Клиентские очереди RX/TX достигли 3/4 пакетов, сервер RX/TX 4/1. Профиль читается один раз при старте и не создаёт второй сетевой стек. Это короткая проверка одного соединения, без sweep параметров или детальной метрики задержки reconciliation.

`stage11-final-server.log`, `stage11-client-mapcache-reconnect.log`, `stage11-recovery-server.log`, `stage11-recovery-client.log`: сначала клиент повторно вошёл в живой RunId `7fc3c58f-c38f-48cd-8f1c-dd57c7ed82c7`, увидел 3 постройки и золото 120, повтор `OperationId=1` вернул прежний результат. После принудительного завершения server-процесса новый процесс получил тот же RunId и восстановил checkpoint tick 2434, золото 120, 3 постройки, 8 целей и RNG `4388994785587189769`. Новый клиент получил полное состояние и повторно подключился без второго героя. При закрытии клиента Unity пишет `Leak Detected : Persistent allocates 4 individual allocations`. Диагностический запуск `leak-diagnostic-client.log` с `-native-leak-detection EnabledWithStackTrace` локализовал минимум одну из них в `Unity.NetCode.GhostEntityMapping.Create → NetcodeUnmanaged.TryInitialize` пакета 6.6, даже без подключения к серверу. В текущем пакете его `Netcode.Dispose` вызывается редакторским callback, но публичный API Player для освобождения этого singleton не найден; пакетный код не патчился. Остальные allocations и повторные циклы World пока требуют отдельной проверки.

Отдельный outage/replay: `stage11-outage-server-before.log` получил RunId `29195b82-2230-4baf-a681-da883f5548b9` и approval, после чего API на `8082` был остановлен до завершения боя. Сервер зарегистрировал `Checkpoint Publish failed`, затем поражение на tick 2901 и `Result pending on disk`; файл размером 242 байта оставался в `.runtime/pending-results`. Оба Unity-процесса были принудительно завершены. После рестарта API и нового server-процесса `stage11-outage-server-after.log` показал `Recovered pending result ... replayed=False`. PostgreSQL: `runs.status=completed`, `outcome=defeat`, `permanent_reward=7`, ровно одна строка `run_result_operations`; journal после подтверждения удалён. Это подтверждает сохранение pending через рестарт отправителя и одно применение данной операции. Тестовая ставка 7 не задаёт продуктовую награду. Потеря именно HTTP-ответа после успешной транзакции проверена backend-тестом, а не этим прогоном.

CI `.github/workflows/backend.yml` поднимает PostgreSQL 16.6 и выполняет 7 backend API тестов. Unity build выполняется только на машине с установленным Unity; CI не имитирует Editor и лицензию. Migration/Authoring и сохранённые GUID не удалялись.

## Непроверенное

- Мобильный touch, графическая нагрузка 512 врагов, частота и точность отдельных ghost.
- Более долгий outage с исчерпанием backoff и отказом восстановления старого поколения; короткий replay выше прошёл.
- Длительный soak и graceful освобождение World/native памяти при многократном запуске; принудительное закрытие процесса не проверяет этот lifecycle.
- Глубокая проверка NETCODE_DEBUG и причины недействительной hero ghost map; текущий рабочий обход проверен только в описанных сценариях.
