# Этап 7: строительство, экономика и производство

## Владение и поток данных

- `StructureDefinition`, `ConstructionRulesDefinition` и `ProductionDefinition` — редактируемые authoring-ассеты. `ContentReleaseBuilder` валидирует ID, ссылки, диапазоны и связи и компилирует их в стабильный `ContentRelease` схемы 4. Сервер копирует определения в `ConstructionDefinitionRuntime` при создании `RunSession`; уже запущенная сессия сохраняет прежний release.
- `RunEconomyState` остаётся единственным авторитетным балансом золота забега. Combat начисляет награды за смерть, Construction списывает покупку, Production зачисляет собранный выпуск. `RunEconomyView` на ghost героя — только реплицируемая проекция для MVP. Постоянный профиль backend не меняется от строительства или производства.
- Construction принимает RPC с ID определения и клетками. Сервер проверяет соединение и владельца героя, активность и жизнь героя, определение, интервал, длину стены, границы, статические и динамические препятствия Navigation, занятость клеток и близость к живым сущностям, лимит препятствий и баланс. Вся команда предварительно проверяется; ожидаемый отказ не меняет баланс/клетки/постройки. После успешной проверки создаются ghost построек, занятые клетки и `NavigationObstacleRequest`; Navigation владеет своей сеткой. Конкурирующие команды обрабатываются последовательно в server World и видят уже занятые клетки/обновлённый баланс.
- Стена строится жестом по `WallPath.GridCell.Trace`. Применение **атомарно для всей команды**: частичного фрагмента или частичного списания нет. Сервер ограничивает команду 12 клетками и интервалом 0,25 с. `OperationId` строго возрастает в рамках сессии; повтор последней операции возвращает прежний результат, старый ID отвергается без эффекта.
- `ProductionSystem` серверный: пилорама накапливает 25 золота каждые 10 секунд времени сессии в `ProductionSource.StoredGold`. При приближении живого героя к точке выдачи выпуск единожды переходит в `RunEconomyState`. При `Paused` и `Terminal` прогресс и выдача не меняются. Клиент получает `StoredGold` на интерполируемом ghost и показывает сохранённый `GoldBag.prefab`.
- Клиентский Shell связывает сохранённые кнопки `Building1`/`Building5` с выбором стены/пилорамы; остальные не реализованные кнопки скрыты. Верхняя карта принимает указатель отдельно от боевого джойстика, показывает локальное превью и отправляет намерение через Presenter/порт. Цена, лимиты и выпуск приходят из серверного offer; локальное превью не решает вопрос о праве и окончательной допустимости. Result RPC и репликация построек формируют read model и вид в карте/3D.

## Перенос значений и ассетов

- Цена стены **10** взята из `Assets/ActiveCombat/Settings/CombatBalance.asset`, `Structures[1].Price`. Сохранённое там `InitialGold=2000` расходится с действующим `RunEconomy.asset` этапа 5 (`150`); источником стартового баланса остаётся действующий release, значение не объединялось.
- Для пилорамы сохранённый `CombatBalance.asset` записи не содержит. Цена **50** взята адресно из архивного `MergedFoundationInstaller`, выпуск **25 за 10 с**, смещение выдачи **+2,6 м Z** и радиус **1,1 м** — из архивного `BuildingProgression`. Эти значения созданы в новых типизированных ассетах и могут изменяться с новым release.
- Существующие `StructureWall.prefab`, `MergedSawmill.prefab`, `GoldBag.prefab` используются по прежним GUID как клиентское представление. `StructureGhost.prefab` новый сетевой prefab без графики. Старые installers не запускались.

## Воспроизведение

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe'
$project = 'F:\projects\VaticanDefendor'
function Invoke-UnityStage7($method, $log) {
    $arguments = '-batchmode -nographics -quit -projectPath "' + $project + '" -executeMethod ' + $method + ' -logFile "' + "$project\Builds\$log" + '"'
    $process = Start-Process -FilePath $unity -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "$method failed; see Builds/$log" }
}
Invoke-UnityStage7 'VaticanDefendor.Editor.Stage7Assets.Prepare' 'Stage7-prepare.log'
Invoke-UnityStage7 'VaticanDefendor.Editor.Stage7Verification.Verify' 'Stage7-verify.log'
Invoke-UnityStage7 'VaticanDefendor.Editor.NetworkPrototypeBuild.BuildServer' 'Stage7-build-server.log'
Invoke-UnityStage7 'VaticanDefendor.Editor.NetworkPrototypeBuild.BuildClient' 'Stage7-build-client.log'
& "$project\backend\scripts\verify-construction.ps1"
```

`verify-construction.ps1` запускает отдельные server/client процессы и проверяет ответ RPC, сумму в реплицированном `RunEconomyView`, число ghost построек, повтор операции и накопление выпуска на клиенте. Для ручного стенда используйте существующие `NETWORK_RUN.md` и `COMBAT_RUN.md`: задайте тот же `VD_DEV_TICKET`, `VD_DEV_ACCOUNT_ID` на сервере, `-vdDevelopmentIdentity` и `-vdRunId` на двух процессах. Дополнительный клиентский аргумент `-vdBuild structure.wall,2,0,4,0 -vdBuildRepeat` отправляет стену и повторяет тот же `OperationId` после ответа; `-vdBuild structure.sawmill,-2,2,-2,2` проверяет производство. Эти аргументы разрешены только Development client в явном режиме development identity.
