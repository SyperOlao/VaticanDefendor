# Stage 2: локальный многопроцессный запуск

Сборки используют Unity 6000.6.6f1 и установленные `com.unity.netcode`, `com.unity.transport` и `com.unity.entities` версии 6.6.0. В редакторе сначала выполните `VaticanDefendor > Network > Create Prototype Assets` (если ассеты ещё не созданы), затем `Build Development Client` и `Build Development Server`. Для batchmode закройте редактор с этим проектом и выполняйте команды последовательно:

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe'
& $unity -batchmode -nographics -quit -projectPath 'F:\projects\VaticanDefendor' -executeMethod VaticanDefendor.Editor.NetworkPrototypeBuild.BuildClient -logFile 'F:\projects\VaticanDefendor\Builds\Stage2\client-build.log'
& $unity -batchmode -nographics -quit -projectPath 'F:\projects\VaticanDefendor' -executeMethod VaticanDefendor.Editor.NetworkPrototypeBuild.BuildServer -logFile 'F:\projects\VaticanDefendor\Builds\Stage2\server-build.log'
```

Запускайте процессы из разных PowerShell-окон. Сервер A (один заранее назначенный `RunId`, аккаунт и ticket):

```powershell
$env:VD_DEV_ACCOUNT_ID = 'local-account-A'
$env:VD_DEV_TICKET = 'replace-with-random-ticket-A'
& 'F:\projects\VaticanDefendor\Builds\Stage2\Server\VaticanDefendor.exe' -batchmode -nographics -logFile 'F:\projects\VaticanDefendor\Builds\Stage2\server-A.log' -vdRole server -vdRunId 11111111-1111-1111-1111-111111111111 -vdPort 7979 -vdDevelopmentIdentity
```

Клиент A (тот же ticket, без `VD_DEV_ACCOUNT_ID`):

```powershell
$env:VD_DEV_TICKET = 'replace-with-random-ticket-A'
& 'F:\projects\VaticanDefendor\Builds\Stage2\Client\VaticanDefendor.exe' -logFile 'F:\projects\VaticanDefendor\Builds\Stage2\client-A.log' -vdRole client -vdRunId 11111111-1111-1111-1111-111111111111 -vdHost 127.0.0.1 -vdPort 7979 -vdDevelopmentIdentity
```

Графический клиент этапа 3 открывает сохранённую сцену через Client Shell: нажмите Start, затем управляйте героем WASD/стрелками, Shift или жестом в нижней области. Верхняя карта и нижняя камера следуют одному реплицированному герою. Для автоматического сетевого прогона добавьте клиенту `-batchmode -nographics -vdMove 1000,0`: значение вне единичного диапазона проверяет ограничение серверной скорости. `-vdRun` выбирает скорость бега. Клиент передаёт направление и флаг бега, а не позицию. Отдельный UI-стенд и границы представления описаны в `CLIENT_SHELL.md`.

Для независимого забега B откройте ещё два PowerShell-окна:

```powershell
$env:VD_DEV_ACCOUNT_ID = 'local-account-B'
$env:VD_DEV_TICKET = 'replace-with-random-ticket-B'
& 'F:\projects\VaticanDefendor\Builds\Stage2\Server\VaticanDefendor.exe' -batchmode -nographics -logFile 'F:\projects\VaticanDefendor\Builds\Stage2\server-B.log' -vdRole server -vdRunId 22222222-2222-2222-2222-222222222222 -vdPort 7980 -vdDevelopmentIdentity
```

```powershell
$env:VD_DEV_TICKET = 'replace-with-random-ticket-B'
& 'F:\projects\VaticanDefendor\Builds\Stage2\Client\VaticanDefendor.exe' -batchmode -nographics -logFile 'F:\projects\VaticanDefendor\Builds\Stage2\client-B.log' -vdRole client -vdRunId 22222222-2222-2222-2222-222222222222 -vdPort 7980 -vdDevelopmentIdentity -vdMove 0,0
```

В логах серверов строки `[Server] run=... position=...` показывают авторитетные координаты каждого забега. До подключения разрешённого клиента A проверьте отказы отдельными запусками client executable на порт 7979: с чужим `VD_DEV_TICKET` сервер и клиент должны вывести `Unauthorized`; с правильным ticket и аргументом `-vdExpectedContent sha256-invalid` — `ContentMismatch`. Этот аргумент предназначен только для проверки отказа в development build. Клиентский content ID служит проверкой совместимости, а не подтверждением права на забег.

`-vdDevelopmentIdentity` действует только в Development Build. Без него или в production build запуск явно завершается ошибкой отсутствующего production identity provider. Ticket передаётся через переменную окружения, не хранится в дизайнерском ассете и не печатается в лог. Это локальный адаптер до этапа 4, не production-аутентификация.

На Windows графический клиент может вызвать запрос Firewall для доступа к публичным сетям. Локальный прогон на `127.0.0.1` не требует изменения этого разрешения; проверяйте сетевой цикл по логам обоих процессов. После принудительного завершения клиента сервер освобождает слот после transport timeout. Возврат в тот же забег с прежним состоянием относится к этапу 9.
