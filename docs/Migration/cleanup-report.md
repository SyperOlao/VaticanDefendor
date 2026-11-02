# Очистка прототипа ActiveCombat

## Исходная точка

- Ветка: `feature/prototype-anna`; исходный HEAD: `1bb7b9aa99a8a7e4b6c2734dfc9b33a76efbe9de`. Он совпадает с контрольной точкой аудита, новых коммитов после неё не было.
- До очистки в рабочем дереве уже были добавлены `Assets/AGENTS.md` и `.meta`, изменены `Assets/Settings/UniversalRenderPipelineGlobalSettings.asset`, `ProjectSettings/EditorBuildSettings.asset`, `ProjectSettings/ProjectAuditorSettings.asset`. Эти изменения сохранены. Открытая Unity позднее также обновила `ProjectSettings/EntitiesClientSettings.asset` и `ProjectSettings/ShaderGraphSettings.asset`; очистка их не редактировала.
- Версия проекта: Unity `6000.6.6f1`; Entities и Entities Graphics `6.6.0`. `ProjectVersion.txt`, `manifest.json`, `packages-lock.json` и `.asmdef` проверены. Unity и пакеты не обновлялись.

## Решения по коду

Удалены 81 C# файл старой реализации и соответствующие `.meta`. Удалены пустые папки `Input` и `Navigation` с их `.meta`. Полный список по каталогам:

- **Bootstrap (4):** `CombatConfigurationBuilder.cs`, `CombatDotsWorld.cs`, `CombatEntityContent.cs`, `GameRoot.cs`.
- **Editor (23):** `CombatAllocationCapture.cs`, `CombatDotsInstaller.cs`, `CombatEcsCombatVerification.cs`, `CombatEcsLifecycleVerification.cs`, `CombatEcsRenderingVerification.cs`, `CombatEffectAuthoring.cs`, `CombatLiveBenchmark.cs`, `CombatMatchEcsVerification.cs`, `CombatPackageInstaller.cs`, `CombatPerformanceVerification.cs`, `CombatRenderCatalogBuilder.cs`, `CombatSceneBuilder.cs`, `CombatSpatialVerification.cs`, `MapLayoutVerification.cs`, `MergedBuildingAssets.cs`, `MergedFlowVerification.cs`, `MergedFoundationInstaller.cs`, `MergedHudInstaller.cs`, `MergedTouchVerification.cs`, `MergedVersionVerification.cs`, `ProgressionAppearanceAssets.cs`, `ReferenceSceneAssets.cs`, `SpecialCombo.cs`.
- **Input (5):** `BuildingPaletteAction.cs`, `CombatTouchRouter.cs`, `TacticalMapAction.cs`, `TacticalMapInput.cs`, `TerrainPlacement.cs`.
- **Navigation (1):** `CombatNavigation.cs`.
- **Presentation (7):** `BuildingStatusPresenter.cs`, `CombatCameraPresenter.cs`, `CombatComboView.cs`, `CombatGameViews.cs`, `CombatSnapshotCollection.cs`, `MergedHudPresenter.cs`, `TacticalMapPresenter.cs`.
- **Runtime (19):** `ActiveCombatGame.cs`, `BalanceValidation.cs`, `BuildingPlacement.cs`, `CombatFlowInterface.cs`, `CombatHUD.cs`, `CombatJoystick.cs`, `CombatMapInput.cs`, `CombatMeshLifetime.cs`, `CombatPerformanceMarkers.cs`, `CombatSession.cs`, `CombatSessionAction.cs`, `CombatTutorial.cs`, `CombatUIAction.cs`, `MobileCombatInterface.cs`, `MobileGestureSurface.cs`, `MobileInterfaceAction.cs`, `MobileInterfaceFactory.cs`, `MobileQuickSwipe.cs`, `WallConstruction.cs`.
- **Simulation (22):** `CombatBotDamage.cs`, `CombatBotGeometry.cs`, `CombatBotJobs.cs`, `CombatBotNavigationJob.cs`, `CombatBotSimulation.cs`, `CombatConfiguration.cs`, `CombatConstruction.cs`, `CombatEnemyRange.cs`, `CombatGameplayContext.cs`, `CombatInputCommands.cs`, `CombatMatchComponents.cs`, `CombatMatchSimulationSystem.cs`, `CombatPipelineJobs.cs`, `CombatPlayerActions.cs`, `CombatPlayerSimulation.cs`, `CombatReadModelSystem.cs`, `CombatRenderAnimationJob.cs`, `CombatRenderingComponents.cs`, `CombatStaticRenderingJob.cs`, `CombatTargetGrid.cs`, `CombatTerrainSelection.cs`, `CombatWorldSimulation.cs`.

Сохранены без изменения четыре схемы данных и их `.meta`: `Runtime/CombatBalance.cs`, `Runtime/PlayerProgression.cs`, `Runtime/MobileViewSettings.cs`, `Bootstrap/CombatRenderCatalog.cs`. Сохранён `Presentation/PlayerAppearancePresenter.cs` с исходными `ArmorTextures` и `MetalTextures`; удалено только поле `Game`. Эти пять файлов — временные схемы миграции. Массового изменения сериализуемых полей не было.

Сохранены независимые `Contracts/PointerOwnership.cs`, `Simulation/WallPath.cs`, `Simulation/CombatRenderColor.cs`, `Runtime/PhoneGraphic.cs`, `Runtime/SpecialReadyEdgeEffect.cs`, `Presentation/CombatRenderSurface.cs`, `Runtime/ReferenceGeometry.cs`. Из `CombatUIFactory.cs` сохранены `Panel`, `Stretch`, `Text`, `Button`, `Input`, `Hex` и поддержка спрайта; удалены привязка `CombatUIAction` и `Build` старого HUD. `PhoneWheelGraphic.cs` получает собственную сериализуемую `WheelView` через `SetView` без `MobileCombatInterface` и `ActiveCombatGame`. `Editor/PineMeshAuthoring.cs` сохранил `ApplyToTree`; удалён `MergeSceneTrees` со старым installer. GUID всех сохранённых скриптов остались прежними.

`ActiveCombat.Simulation.asmdef` сохранён с исходным GUID; список ссылок сокращён до `Unity.Mathematics`, нужной `CombatRenderColor`, отключён неиспользуемый unsafe. `ActiveCombat.Contracts.asmdef` не менялся. Старых gameplay-систем, контроллеров, установщиков и генераторов в компилируемом коде не осталось. Результаты генераторов — mesh, material, texture, prefab и каталог — сохранены.

## Карта ссылок сцены

До удаления по `.cs.meta` составлена карта `.cs → GUID → MonoBehaviour fileID` для двух сцен. В `ActiveCombat.unity` было 307 компонентов `MonoBehaviour`; следующие 53 были привязаны к удаляемому коду. `Assets/_Recovery/0.unity` содержала такие же 53 компонента и сохранена как архив без правки.

| Удалённый скрипт | GUID | Компонентов в основной сцене |
| --- | --- | ---: |
| `Bootstrap/GameRoot.cs` | `165da60ad19ac9a4c96946bc068ed467` | 1 |
| `Input/BuildingPaletteAction.cs` | `80f405fa2a5934b40a06b87b303fe4b3` | 6 |
| `Input/CombatTouchRouter.cs` | `c135fc7eab9b6dd488ed938db8998a8a` | 1 |
| `Input/TacticalMapAction.cs` | `21db68fc98df4ac44bd7fa8fcb0cafb5` | 8 |
| `Input/TacticalMapInput.cs` | `f8a7d8550a81d7542a082ce170981613` | 1 |
| `Navigation/CombatNavigation.cs` | `e66fc45ea720e534e93950296c85ca02` | 1 |
| `Presentation/BuildingStatusPresenter.cs` | `cd82db843b2973b40840fa46c5a3b802` | 1 |
| `Presentation/CombatCameraPresenter.cs` | `54a89eeb3bbb02e49be577de99e38b71` | 1 |
| `Presentation/MergedHudPresenter.cs` | `7e10fa55724c25541994b0fe74a63bd7` | 1 |
| `Presentation/TacticalMapPresenter.cs` | `857cfbdd093f6f64aa60eeb04bde394c` | 1 |
| `Runtime/ActiveCombatGame.cs` | `5c4f09831391c194baa383136656c159` | 1 |
| `Runtime/CombatHUD.cs` | `3bee889560118d141858aa71681aab91` | 1 |
| `Runtime/CombatJoystick.cs` | `cb8b8ff86fbb11048bc35ea01d0e5488` | 1 |
| `Runtime/CombatSession.cs` | `5406ce850eb8604458a6a0c648cbaf38` | 1 |
| `Runtime/CombatSessionAction.cs` | `415fcb6164aff184aaf6733fec11bc82` | 11 |
| `Runtime/CombatTutorial.cs` | `520ebe7f87ac53f4e920f269044db597` | 1 |
| `Runtime/CombatUIAction.cs` | `39ed1e0d0ba787e4db24aaa1d607b656` | 7 |
| `Runtime/MobileCombatInterface.cs` | `72b571a174ae43748a40f50426def56b` | 1 |
| `Runtime/MobileGestureSurface.cs` | `9ed6e5c71ca66f34f921bf94bca2b718` | 2 |
| `Runtime/MobileInterfaceAction.cs` | `f038194d7dc3eba4e9c759ca2f085812` | 4 |
| `Runtime/MobileQuickSwipe.cs` | `7fce28a553e3b0d47b8655dd51e024ef` | 1 |

Остальные 60 удалённых C# файлов не имели `m_Script` в этих сценах. `CombatMeshLifetime` также не имел ссылок в импортируемых сценах, префабах или ассетах; его `OnDestroy` удалял `sharedMesh`.

В сцене `ActiveCombat.unity` (теперь `Assets/Scenes/ActiveCombat.unity`, ранее `Assets/ActiveCombat/Scenes/ActiveCombat.unity`) удалены только 53 YAML-документа старых компонентов и их записи `m_Component`. Все 474 `GameObject` и иерархия сохранены. Поля `Game` и `Mobile` убраны соответственно из `PlayerAppearancePresenter` и `PhoneWheelGraphic`; колесо хранит неактивный вид с восемью секторами. Все `m_PersistentCalls.m_Calls` были пусты; UnityEvent-вызовов старых методов не было. Сохранены шесть исходных GUID текстур улучшений. В сцене нет ссылок на удалённые `fileID`.

`Assets/_Recovery/0.unity`, `0.unity.meta` и метаданные папки `_Recovery.meta` перенесены побайтно в `docs/Archive/Prototype/` под именами `Recovery-0.unity`, `Recovery-0.unity.meta`, `_Recovery.meta`. Сцена больше не импортируется из `Assets` и не включена в сборку. `Evidence/BeforeDots` и `Evidence/BeforeFullDots` удалены после проверки содержимого: единственный некодовый файл `Presentation/TacticalMap.shader` побайтно совпадал с действующим шейдером в `Assets`.

## Сохранённый контент

Без изменения файлов и `.meta` сохранены `Assets/ActiveCombat/Prefabs`, `Materials`, `Meshes`, `Fonts`, `Settings`, `Shaders`, а также `Presentation/CombatEntityEffect.shader` и `Presentation/TacticalMap.shader`. 33 префаба не содержат `m_Script`; они не переписывались. `CombatBalance.asset` сохранил GUID `401d1f0c3dd8cdb4b831de3b95ed0465` и 8455 байт; `CombatRenderCatalog.asset` сохранил GUID `ab531694024a75f45bd35675345b84a2`, 1056774 байта и 114 сериализованных объектов, включая все subassets. Их содержимое совпадает с исходным коммитом после нормализации переводов строк.

Содержимое `EntityAuthoring` в основной сцене и пометка `EditorOnly` сохранены. После удаления ECS bootstrap прежнее отображение моделей в Player не гарантируется; старый gameplay намеренно не восстанавливался.

## Проверки и ограничения

- Статическая сверка: 81 удалённый GUID скрипта отсутствует в импортируемых `.unity`, `.prefab` и `.asset`; все `GameObject` основной сцены сохранены, новые висячие локальные ссылки на удалённые компоненты не появились. GUID, импортные настройки и содержимое перечисленных каталогов совпадают с HEAD.
- Архив recovery-сцены и её `.meta` совпадает с исходником после нормализации переводов строк. Шесть ссылок на текстуры в `PlayerAppearancePresenter` совпадают с исходными.
- В Unity `6000.6.6f1` выполнен `Assets > Refresh`: повторно собраны `ActiveCombat.Simulation`, `Assembly-CSharp` и `Assembly-CSharp-Editor` без ошибок C#. `ActiveCombat.unity` открыта добавочно к пользовательской `Untitled`: карта и деревья видны, на корневом объекте только `PlayerAppearancePresenter` с массивами 3/3; в Console 0 ошибок.
- В Console остаётся предупреждение пакета Entities Graphics: `Mobile_Renderer` настроен не на URP Forward+. Настройки URP не менялись в рамках очистки.
- PlayMode, клиентская/server-сборки и проверка прежней играбельности не выполнялись: их старые системы удалены по задаче. Новая архитектура, сеть и перенос файлов в `Assets/Scripts` — отдельная задача.

