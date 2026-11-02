# Клиентский Shell и UI-стенд (этап 3)

Клиентская Development Build содержит `NetworkPrototype.unity` и сохранённую `ActiveCombat.unity`. Bootstrap создаёт один `GameClient` World и загружает вторую сцену добавочно. `ClientShellView` на `MobileCombatCanvas` связывает сохранённые `MainMenu`, `SettingsModal`, `PauseOverlay`, `TacticalMapCanvas` и `BattleRenderSurface` с `ClientShellPresenter`. В server build включена только `NetworkPrototype.unity`; код Shell исключён из dedicated player.

`IClientSessionPort` — граница UI и сетевого мира. `NetcodeClientSessionPort` читает фазу подключения и позицию локального ghost, принимает запрос подключения и передаёт клиентское намерение `ClientMoveIntent`. Только `ClientMoveInputSystem` переносит его в Netcode input. Сервер по-прежнему проверяет ввод и рассчитывает движение. `RunId` и `ContentReleaseId` на экране взяты из параметров запуска клиента; сервер отдельно проверяет их и право доступа. Эти строки показаны для диагностики и ничего не доказывают.

Кнопка Start запускает транспортное соединение; Connecting блокирует повторный Start; Lost и Error показывают причину и Retry. В Game работают WASD/стрелки, Shift и touch drag в нижней области. Menu открывает локальное меню без остановки сервера; Leave закрывает соединение и возвращает в Launch. Настройки позволяют поменять локальную высоту верхней карты между 1/4 и 1/3. Параметры жеста берутся из сохранённого `CombatBalance.asset`; боевые значения не читаются UI. Каждое касание закрепляется за одним регионом (интерфейс, карта, бой, разделитель) до завершения или закрытия экрана. Пока сервер не публикует бой, ресурсы и строительство, соответствующие старые элементы скрыты.

Обе камеры используют одну сцену и один реплицированный ghost: сохранённый `Knight.prefab` следует локальному герою в нижней области, маркер на слое карты следует той же позиции сверху. `CombatRenderSurface` продолжает владеть RenderTexture и освобождает его при уничтожении сцены. Shell удаляет свои подписки и временные визуальные экземпляры при выключении.

Подготовить сохранённую сцену и отдельный UI-стенд:

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe'
& $unity -batchmode -nographics -quit -projectPath 'F:\projects\VaticanDefendor' -executeMethod VaticanDefendor.Editor.ClientShellAssets.Prepare -logFile 'F:\projects\VaticanDefendor\Builds\Stage3-prepare.log'
```

Стенд `Assets/ActiveCombat/Network/ClientUiStand.unity` открывается в Editor Play Mode. Он использует те же `ClientShellView`, `ClientShellPresenter`, `ClientShellViewModel` и `IClientSessionPort`, но явно показывает *simulated session* и кнопки Connected/Lost/Rejected/Idle. Он подходит для независимой работы над экранными состояниями и не является сетевой проверкой. Для настоящего запуска используйте команды из `NETWORK_RUN.md` с графическим клиентом без `-batchmode`; клиентская сборка теперь показывает сохранённую сцену и Shell вместо капсулы прототипа.
