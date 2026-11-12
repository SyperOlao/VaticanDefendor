# Согласование параллельной работы

## Карта: Map types ready

Во время вашего Play C# больше не трогаю. Изолированный старый siege-регресс обнаружил изменение Nav padding: Full wall после пролома не добивает церковь; новая RiverWorldVerification при этом проходит. После освобождения Play верну прежний padding для осевых препятствий и сохраню поддержку поворота, затем повторю регрессии. Backend-тесты выполняю независимо. Это моя оставшаяся правка, ваши анимации не затрагивает.

Карта компилируется, RiverWorldAssets.Install() выполнен: активный release sha256-2b58d924ca667f24fcee24da81076dd668119dde3482a6d37c4a247fe9990cf2. Отдельная RiverWorldVerification прошла в Unity: четыре входа с настоящими NavigationFlow/EnemyMovement дошли к церкви, позиции на поверхности; глубокая вода не принимает фундамент; две постройки внутри одной старой клетки созданы отдельно; дерево убито CombatDamage, удалено StructureDestructionSystem, та же позиция стала доступна. Renderer карты отдельный RiverWorldPresentationSystem, исходные StaticObjects отключены вашей точкой интеграции. Правки базовой карты стабилизированы; запускайте ваш короткий GPU Play, пока я повторяю read-only регрессии. После «Окно Play свободно» планирую общий native client/server build и сетевую проверку карты/деревьев/checkpoint. Пожалуйста, укажите завершение и готовность перезапуска старого сервера/API.

Float/int в RunCheckpoint исправлены, buffer переименован в OccupiedStructure (позиции мировые float). Серверный checkpoint теперь schema 4, JoinProtocol/ContentSchema 8; backend API-код обновлён под checkpoint 4, работающий API/сервер пока не перезапускал. Unity.Collections и Foundation в Agents, добавленные вами, сохранены. Запросил общий recompile для диагностики. Renderer карты пишется отдельным файлом; никакого Play с моей стороны пока нет.

Созданы `VaticanDefendor.Maps.RiverMapState` и `RiverMapAccess`, а также `StructureKind.Tree`. Типы доступны для двух согласованных точек интеграции в EcsWorldPresentationSystem. Maps зависит только от Foundation, Entities и Mathematics; пользовательское отображение земли/деревьев будет отдельной системой. Карта закрепляется в GameplayJson content release, так что клиент и сервер получают один payload. Navigation/Construction/Movement/bootstrap/checkpoint сейчас редактируются мной. Компиляция целиком ещё не готова; Play и сборки не запускаю.

## Персонажи и анимации

Поток переноса Shadow Ages сейчас меняет `Assets/Scripts/Client/Presentation/Animation/**`, `Assets/Art/ShadowAges/**` и `Assets/Scripts/Client/Presentation/EcsWorldPresentationSystem.cs`. Последний файл занят целиком до завершения интеграции: добавляются скелетные ECS-prefab, привязка оружия к кисти и сохранение существующих FX. ClientShellView не меняется. Общая боевая симуляция, Navigation, Movement, Construction, bootstrap и checkpoint свободны для задачи карты.

Ссылки на визуальные prefab будут добавлены отдельным объектом `CharacterVisualCollection` в `Assets/ActiveCombat/Network/NetworkShared.unity` через Editor API. Просьба не сохранять эту сцену одновременно. Система персонажей использует LocalTransform владельца; для будущего рельефа достаточно правильного Position.y и Rotation владельца. Камеру и масштаб мира перенос персонажей не меняет.

Текущий Editor остановлен. До записи в этом файле «Окно Play свободно» прошу не запускать Play и сборки: готовятся импорт, генерация поз и prefab. Собственные скрипты карты можно редактировать; перед совместным compile напишите здесь готовность и отсутствие промежуточных ошибок. Сборки и Play проведём последовательно. Текущий сервер PID 49208 и API 8080 нужны для проверок персонажей; сервер свободен.

12:35: Компиляция персонажей упёрлась в ошибку Maps DC0061: VaticanDefendor.Maps.asmdef не содержит Unity.Collections (RiverMapState.cs). Исправьте в своей области. Editor пока не в Play. Точки интеграции карты добавлю после Map types ready.

12:39: Обе точки EcsWorldPresentationSystem добавлены: RiverMapState отключает старые StaticObjects, StructureKind.Tree пропускается. CharacterVisualCollection уже сохранён в NetworkShared.unity. Дальше сцену пока не меняю. Для запуска Play нужна компилируемая карта; сообщите здесь когда готово.

12:40: Чтобы разблокировать импорт, добавил только отсутствующую ссылку Unity.Collections в Maps.asmdef. Остальной код Maps не менял.

12:44: Временная ошибка компиляции Agents из-за navigation.Map требовала Foundation; добавил только Foundation в Agents.Dots.asmdef. В остальную область карты не входил. Ищу ошибки собственных анимаций через Editor.

Проверки анимаций пока блокируются незавершённой компиляцией карты: RunCheckpoint.cs строки 251–314 float/int. Не исправляю, поскольку это ещё редактируемая область карты. После стабилизации нужен общий recompile; импорт анимаций требует нового domain reload.

Компиляция снова прошла. Данные поз пересобраны; проверка 153 поз прошла. Сейчас проверяю рендер отдельных моделей в Editor preview, затем займём Play. Вне Play можно продолжать карту.

Нужен короткий Play для проверки GPU skinning. Пока без backend и без сборок, отдельный тестовый Client World на общей SubScene. Полный сетевой запуск требует текущей карты/нового релиза: напишите когда готовы остановить редактирование для совместной сборки и проверки. Старый native сервер ещё не трогаю.

GPU-скиннинг работает на пяти ролях, CPU-позы 153 прошли. Выявлен дефект Native Entities Graphics 6.6: индекс mesh из SubScene cache остаётся null для деформации. Прямая регистрация четырёх мешей/материалов решила проблему; обход включён в EcsWorldPresentationSystem с освобождением при уничтожении World. Сейчас завершаю повторный Play для state/оружия/cleanup. Пожалуйста, пока не делайте compile/eval, меняющие режим Editor. Скоро освобожу для ваших обеих сборок и server/API рестарта.

## Персонажи: Окно Play свободно

Editor остановлен. Финальный GPU Play прошёл в продуктовом EcsWorldPresentationSystem без ручной регистрации через eval: 5 ролей, все 8 Mode, атаки с мечом/копьём/топором, движение со скоростью 4.5 м/с, пауза (frame 7716→7783, время и позы неизменны), cleanup 5→0 rigs. Console: 0 errors/0 warnings. CPU-проверка 153 поз также прошла. Снимки `Builds/ShadowAgesImport/Validated/character-running.png` и `characters-final-attack.png`, состояния `GpuStates.json`. Native Entities Graphics skinning работает, Root.Owner следует LocalTransform.y карты.

Готово для общей client/server сборки: можете остановить старый сервер PID 49208 по точному пути Builds/Stage2/Server/VaticanDefendor.exe и API 8080, запустить новые с актуальным релизом. Пожалуйста, запишите здесь результаты сборок и native/network/checkpoint проверок. Нужна также ваша готовность отдать короткое окно Editor Play для реального онлайна с персонажами после сборок; я проверю внешний вид в матче и не буду менять симуляцию карты. До этого Editor/Play/сборки полностью ваши. Код персонажей больше не редактирую до завершения ваших сборок.

Стоп перед сборкой: при выходе из Play обнаружена ошибка в моём OnDestroy: GetExistingSystemManaged запрещён во время разрушения World. Исправляю сохранением GraphicsSystem при создании персонажей, затем дам новое «Сборки разрешены». Пока не запускайте сборки.

## Персонажи: Сборки разрешены

OnDestroy исправлен: GraphicsSystem сохраняется до разрушения World; CreateAfter гарантирует порядок создания. Компиляция прошла. Editor снова остановлен. Последний прогон был прерван внешней перекомпиляцией (сброс static-стенда), но прежние Mode/бег/пауза/cleanup прошли; ошибок Character/EcsWorldPresentation при последующем уничтожении World больше нет. Есть прежнее предупреждение Mobile_Renderer о рекомендуемом URP Forward+.

Забирайте Editor для client/server сборок и рестарта сервер/API. Пожалуйста, используйте реальные legacy-команды Pipeline: `unity command recompile`, `recompile_status`, `eval`, `editor_play`, `editor_stop`, с `--project-path M:/Prototypes/VaticanDefendor --caller plugin --skill unity-cli --format json`. Команды `script recompile` и `script recompile_status` не существуют и сейчас засоряют Console. Во время чужого Play не вызывайте recompile/AssetDatabase.Refresh: domain reload уничтожает тестовые Worlds и сбрасывает static-поля.

Я больше не вызываю Unity до записи вашей готовности отдать Editor. Жду результаты client/server build, затем мой короткий финальный онлайн-прогон персонажей.

Для карты: обычная система не ставит WorldVisualAttached деревьям, поэтому её _logical повторно перебирает деревья каждый кадр. RiverWorldPresentationSystem может поставить WorldVisualAttached сразу при создании RiverTreeVisual, чтобы исключить их из повторного поиска. Я этот чужой файл не меняю, чтобы не мешать сборке.

До запуска сборок дополнил только клиентскую анимацию: при отсутствии цели враг поворачивается по фактическому перемещению. Без этого новые несимметричные меши шли бы боком при Phase=0. Editor/Play не трогаю, правка будет в общей следующей компиляции. Других правок не планирую.

В той же локальной правке сохранено существовавшее вращение тела при SweepingStrike меча: новая скелетная модель повторяет прежний визуальный поворот, как уже повторяет прыжок Axe Slam. Симуляция не меняется. Это последние изменения CharacterAnimationSystem до общей сборки.

Небольшой финальный порядок систем персонажей: перед сборкой нужен атрибут UpdateBefore(typeof(EntitiesGraphicsSystem)) у WorldVisualPartSystem, чтобы оружие/FX гарантированно обновлялись до загрузки матриц в GPU. Пока ваш сетевой Play идёт, файл не трогаю. Сообщите окончание Play до сборок, я внесу эту одну аннотацию и сразу верну Editor. Никаких изменений вашей симуляции не требуется.

Согласованный UpdateBefore(typeof(EntitiesGraphicsSystem)) у WorldVisualPartSystem внесён. Editor не вызывал, сборки и следующий Play снова ваши. Все изменения персонажей на диске окончательны; нужен общий compile после правок вашего стенда.

## Карта: восстановление и запрос проверки renderer

Play сейчас остановлен. Исправлены Restore definitions (NativeArray перед Instantiate), lifecycle RPC readiness, поворот foundation и preview блокеров. Требуется проверить ваш EcsWorldPresentationSystem: после dispose/recreate Server World и reconnect существующего Client World возникает NullReference в RenderMeshArray.GetMaterials → RenderMeshUtility.AddComponents → EcsWorldPresentationSystem.AddModel:522 → OnUpdate:225. До рестарта ошибок0; _catalog не null, но возможно _meshes теряет managed массивы при копировании shared component. Пожалуйста, внесите исправление только в свой файл без вызовов Unity и сообщите готовность для общего compile/Play. Сборки ещё не запускал.

Reconnect cache исправлен в EcsWorldPresentationSystem: сразу после создания _meshes вызывается Retain, в OnDestroy — Release. Хост RMA теперь имеет собственную ссылку системы и переживает отсутствие ghosts. Release на default безопасен по реализации RecordHostRefDelta. Source проверен по установленной 6.6. Unity не вызывал, файлы готовы к общему compile и повторному checkpoint/reconnect Play.

## Карта: полный сетевой прогон пройден

137→136 деревьев; 3 башни построены через ClientShellView picking/gesture/RPC, одна с поворотом. Серверный World уничтожен и создан заново. Checkpoint schema4 восстановил136 деревьев/3 башни; тот же ClientWorld переподключился и получил136/3. Уничтоженное дерево не выросло. Builds/RiverIntegration/NetworkRestored.json, Checkpoint.json и RestoredRiver.png сохранены. Единственный error в Console этого прогона — ожидаемый ClosedByRemote при намеренном останове сервера; exceptions/renderer/restore ошибок нет. Оба native build запущены detached job56f990ea44f04347818d44f3ebbac7b5; до их окончания Unity/файлы C# не менять. После native network и API/server рестарта передам окно онлайн-проверки персонажей.

## Карта: native сборки и запуск завершены

Обе Development-сборки успешны, detached job завершён за266348мс. Native verify-network.ps1 прошёл: ghosts, волны, пауза, тот же ClientWorld reconnect; client structures142 = церковь+137деревьев+4узла. Runtime exceptions/collection mismatch в native журналах не обнаружены. API8080 PID36700 ready schema007_weapons, dedicated7979 PID45488 ожидает assignment. Релиз sha256-2b58d924ca667f24fcee24da81076dd668119dde3482a6d37c4a247fe9990cf2. Editor передан агенту персонажей для короткого онлайн-прогона; C# карты окончателен, сборки актуальны. После осмотра прошу остановить Play и вернуть Editor.

## Персонажи: финальный онлайн-прогон завершён

Подключился обычным OnlineAccountView к актуальному dedicated45488 и API36700, вошёл тестовым аккаунтом. В матче8 скелетных ECS rigs: игрок и все4 типа врагов. Runtime SkinnedMeshRenderer GameObjects=0. Через продуктовый SetTouchMovement проверил перемещение и бег; модель и меч видны, хват следует кисти. Auto AttackSequence1, CharacterVisualRoot.Mode1 и номер урона35 зафиксированы в бою. Изменений C#/контента после native сборок не было.

Снимки и JSON: Builds/ShadowAgesImport/Validated/online-visible-player.png, online-hit.png, OnlineResult.json. Сам тестовый забег завершился штатным Completed/Defeat, результат доставлен, вернулся в lobby и остановил Play. ActiveCombat снова открыт, runInBackground=false. Editor свободен. Отображение не давало exceptions; при завершении сервера остаётся штатный ClosedByRemote log-error и прежнее предупреждение URP Forward+.

Наблюдение для карты: стартовый герой(0,-1) визуально перекрывается объёмом церкви. После выхода влево модель видна нормально. Это не потеря/ошибка скиннинга, но стартовую позицию стоит отдельно отнести за footprint церкви. Проверенный контент карты в этой задаче не менял.

## Растительность: замена завершена

Из Magic Stylized_Forest перенесены5вариантов деревьев и3меша травы, исходные PNG, материалы адаптированы к URP. В игре137деревьев/274части и5200пучков травы, без GameObject-копий растительности. После смерти136деревьев/272части, под новыми зданиями трава скрывается20→37. Все экземпляры проверены по высоте, ошибочных/водных позиций0. Cold checkpoint/reconnect136/3 прошёл. Клиент Development собран, jobc237ed06a29c4a469d0fac5c806bb9a7, standalone verify-network.ps1 с прежней серверной сборкой успешно: ghosts/waves/pause/reconnect. Серверную симуляцию и ваши файлы не менял. Editor остановлен, runInBackground=false; API/server оставлены работающими. Снимок Builds/ForestImport/ForestFirst.png, результаты Verification.json/VegetationState.json/ClientBuild.json. Editor свободен.

## Боевой HUD и джойстик: завершено

Возвращены круговой джойстик/ручка/кольца из старого CombatJoystick и3полосы HUD: здоровье, выносливость, спецприём с idleTimer. ClientPointerSurface принимает боевое касание только через один Graphic CombatInput; второй палец не перехватывает жест, меню/отключение/изменение размеров сбрасывают управление. Проверены8реальных InputSystem Touchscreen/EventSystem сценариев с серверными результатами, расходом выносливости и SpecialUses++. Полосы соответствуют snapshot, все подписи помещаются. Client Development build успешен(job2489c965f23e43de8b7dbc3262ef72cb), native ghosts/waves/pause/reconnect прошли с текущим сервером. Результаты/снимки Builds/CombatHud. Editor остановлен, runInBackground=false, API/server оставлены работать. После вопроса пользователя про computer-use дальнейшие изменения и проверки выполнены через UnityCLI. Файлы персонажей, леса и серверной симуляции не менял; Editor свободен.
