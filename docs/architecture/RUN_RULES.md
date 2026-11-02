# RunRules и результат забега (этап 8)

`RunLifecycle` в авторитетном Server World проходит `Active → BattleFinished → ResultPending → Completed`. `RunTerminalSystem` делает первый переход единожды: победа после завершения последней настроенной волны (`WaveState.Phase == 4`), поражение при первом уничтожении стартовой церкви. Если оба условия возникли в один tick, приоритет у поражения. Смерть героя сама по себе не завершает забег: существующий AI после неё направляет живых врагов на церковь. `RunActivityState.Terminal` останавливает движение, бой, решения врагов, волны, строительство и производство; входящие боевые и строительные команды не получают игрового эффекта.

Стартовая церковь описана `StartingChurch.asset` и входит в неизменяемый `ContentRelease` схемы 5. Её 600 HP и позиция `(0, -3)` перенесены из сохранённого `CombatBalance.asset` и архивного `CreateStartingBase`; начальный уровень равен 1. Combat изменяет `CombatHealth` и ставит `Dead` единожды. `ChurchStateRpc` даёт клиенту здоровье, уровень и положение; сохранённый `MergedChurch.prefab` отображается в той же сцене и на верхней карте. Повышение уровня через Tycoon ещё не имеет утверждённого контракта стоимости и не реализовано: текущий забег начинается с уровня 1.

Dedicated server формирует JSON результата из закреплённых `RunId`, `AccountId`, `ContentReleaseId`, поколения текущего назначения, исхода и уровня церкви. Backend принимает его только с удостоверением настроенного игрового процесса. Для каждого release оператор явно задаёт положительную ставку `perChurchLevel`; backend копирует её в строку `runs` при назначении. Подтверждённое правило: закреплённая ставка × достигнутый уровень церкви начисляется при победе и при поражении. Клиент не передаёт сумму или уровень доверенному endpoint. Численное значение ставки продуктом пока не утверждено, поэтому backend отклоняет регистрацию сервера без явной политики для выбранного release.

`POST /internal/servers/{serverId}/results` блокирует строки забега и процесса, сверяет аккаунт/release/поколение/назначение и в одной транзакции обновляет профиль, пишет `run_result_operations`, переводит `runs` в `completed` и освобождает слот. `run_result_operations.run_id` — первичный ключ. SHA-256 канонического payload хранится рядом: идентичный повтор возвращает прежнюю квитанцию, конфликт исхода или уровня даёт 409. Если ответ после commit потерян, повтор не начисляет валюту повторно. После результата слот помечается `unavailable`: новый забег требует нового dedicated-процесса.

Перед `ResultPending` процесс записывает payload в `VD_RUN_RESULT_DIR` (или `Application.persistentDataPath/pending-results`) через временный файл, `Flush(true)` и rename. HTTP выполняется асинхронно вне simulation tick; временные ошибки вызывают ограниченный backoff до 30 с между попытками, постоянный HTTP 4xx сохраняет запись для разбора. После рестарта процесс сначала повторяет все свои записи и лишь затем объявляет готовность backend. Успешная квитанция удаляет локальную запись и переводит живой World в `Completed`.

Гарантия относится к применению результата в PostgreSQL и повторной доставке сохранённого payload. Она требует сохранности каталога журнала, стабильного ServerId/секрета и доступной БД. Потеря процесса между терминальным ECS-переходом и записью журнала, восстановление самой ECS-сессии, переназначение уже начатого забега и возвращение клиента после перезапуска процесса остаются задачами этапов 9–10. Число награды, Tycoon-улучшение церкви и историческая регенерация церкви пока не заданы в рабочей release-модели; их нельзя считать проверенной игровой балансировкой.

## Локальный запуск

После выбора продуктом ставки и создания release:

```powershell
backend/scripts/start-local.ps1 -ContentReleaseId sha256-<64 hex> -RewardPerChurchLevel <утверждённое целое число>
backend/scripts/run-server.ps1
backend/scripts/run-client.ps1
```

Для PostgreSQL-тестов используй отдельную БД в `VD_TEST_DATABASE_URL` и `PYTHONPATH=backend`, затем `backend/.venv/Scripts/python.exe -m pytest backend/tests/test_api.py -q`. Unity-проверка: `-executeMethod VaticanDefendor.Editor.Stage8Verification.Verify`; перед сборкой нового release: `-executeMethod VaticanDefendor.Editor.Stage8Assets.Prepare`. Server и client Development Build собираются последовательно через `NetworkPrototypeBuild.BuildServer` и `BuildClient`.

Для проверки потери backend и рестарта отправителя используй отдельную локальную БД (например, `vatican_stage8`, созданную с владельцем `vatican`) и **только тестовую** ставку. Запусти `backend/scripts/start-api-local.ps1 -ContentReleaseId <release> -RewardPerChurchLevel <test-rate> -DatabaseName vatican_stage8`, затем `run-stage8-server.ps1` и `run-stage8-client.ps1` из того же каталога. После строки `Connection approved and in game` останови только запущенный этими скриптами API PID из `backend/.runtime/stage8-api.pid`. Дождись `Result pending on disk` и файла в `backend/.runtime/pending-results`, останови запущенные server/client PID из соответствующих файлов `.runtime`, затем снова запусти API и сервер. В `Builds/Stage8/server-recovered.log` должно появиться `Recovered pending result`; файл исчезает, а профиль гостя из `backend/.runtime/stage8-guest.json` получает одну прибавку. Перед остановкой процесса проверь его путь и командную строку по PID; скрипты не завершают чужие процессы автоматически.
