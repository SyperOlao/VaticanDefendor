# Этап 5: серверный бой

## Границы и правила

`Combat` принимает `CombatAttackCommand` для принадлежащего игроку hero ghost, проверяет живое состояние героя, конечный и нормализуемый вектор направления, интервал, выносливость и область меча. Сервер выбирает ближайшую живую цель в области удара, меняет `CombatHealth` и один раз отмечает смерть. Входящий RPC содержит только номер команды и направление: здоровье, урон и позицию цели клиент не назначает. Повторный номер команды игнорируется.

`RunEconomy` отдельно владеет золотом, опытом и числом убийств текущего забега. Combat вызывает `ApplyKillReward` только при первом переходе `CombatHealth.Dead` в `1`; операция повторно отвергает тот же номер события. Постоянный профиль backend этим действием не меняется.

Снимок `ContentRelease` версии манифеста 2 содержит определения атаки, цели и экономики. При создании `RunSession` сервер копирует их в ECS tuning и не перечитывает ScriptableObject на tick. Новый release не меняет запущенную сессию. Клиент получает ghost здоровья и подтверждённый RPC результата. `ClientCombatStatus` хранит последний подтверждённый результат и отдельный номер последнего попадания для однократного визуального импульса; prediction движения остаётся отдельно, необратимых predicted наград и эффектов нет.

В сохранённом `CombatBalance.asset` и архивном коде для обычного меча совпадают 30 урона, 0,25 с интервала, радиус оружия 3,6 м и множитель дальности 0,65. Допуск области 0,4 м взят из сохранённого правила попадания в коде; цена обычного удара 0. Слабая цель имеет 53 HP. Начальное золото забега 150, первое убийство даёт 8 золота и 18 опыта. Эти значения адресно перенесены в `HeroSwordAttack.asset`, `WeakCombatTarget.asset`, `RunEconomy.asset`. Это одна неподвижная цель без AI и волн.

## Воспроизведение

Из корня проекта в PowerShell, с установленным Unity 6000.6.6f1:

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe'
& $unity -batchmode -nographics -quit -projectPath (Get-Location).Path -executeMethod VaticanDefendor.Editor.CombatStageAssets.Prepare -logFile Builds/Stage5-prepare.log
& $unity -batchmode -nographics -quit -projectPath (Get-Location).Path -executeMethod VaticanDefendor.Editor.CombatStageVerification.Verify -logFile Builds/Stage5-rules-verify.log
& $unity -batchmode -nographics -quit -projectPath (Get-Location).Path -executeMethod VaticanDefendor.Editor.NetworkPrototypeBuild.BuildServer -logFile Builds/Stage5-server-build.log
& $unity -batchmode -nographics -quit -projectPath (Get-Location).Path -executeMethod VaticanDefendor.Editor.NetworkPrototypeBuild.BuildClient -logFile Builds/Stage5-client-build.log
& ./backend/scripts/verify-combat.ps1
```

Запускай две сборки Unity последовательно в отдельных процессах: Netcode генерирует метаданные ghost при каждой сборке. Скрипт проверки создаёт случайные development ticket и RunId, запускает dedicated server и headless client отдельными процессами через Unity Transport, проверяет два попадания, `Cooldown`, единственную смерть и награду, затем запускает новый забег с атакой назад и проверяет `Miss` без награды. Логи остаются в `Builds/Stage5/`; скрипт завершает оба процесса. Development identity включён только явным флагом в Development Build. Для backend-режима используются команды из `BACKEND_RUN.md`.

В графическом клиенте сохранённый layout `ActiveCombat.unity` показывает здоровье ghost-цели и итог атаки; кнопка «УДАР» и Space передают намерение через Presenter. Цель и маркер верхней карты используют один и тот же target ghost, скрываются после смерти. Кнопка владеет своим pointer через `ClientPointerSurface`.

## Текущие ограничения

Цель не атакует героя и не перемещается; её AI, навигация и волны относятся к этапу 6. Визуальная проверка кадра и touch на устройстве остаётся отдельной проверкой клиентского представления. Сохранение состояния забега после падения относится к этапу 10.
