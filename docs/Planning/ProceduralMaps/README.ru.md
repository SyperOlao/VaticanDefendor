# Материалы планирования процедурных карт

Исторические результаты получены до следующего исправления навигации. После него осада и растеризация проверяются актуальным `EnemyNavigationVerification.Run()` в проекте: круг в углу четырёх клеток теперь занимает четыре клетки. Сохранённые JSON и ранние пробы документируют состояние на момент исследования, а не обязательный ожидаемый результат нового кода.

Актуальный результат — [план процедурного 3D-мира](Plan.ru.md), переработанный 12 ноября 2026 года по решению пользователя. Он предусматривает реальный рельеф, движение и бой на высотах, свободные позиции/повороты построек и отсутствие видимой клеточной структуры. Внутренняя сетка допустима только как средство расчёта. Первый этап — контрольный 3D-участок; процедурный генератор следует после перехода симуляции, строительства и сети. Продуктовая реализация при редакции плана не выполнялась.

## Новые 3D-эксперименты

Выбран пользователем профиль «Река с двумя бродами». Последний эксперимент увеличен с 48 × 48 до 96 × 96 м при прежних размерах зданий и персонажей. Повторно проверены 12 seed; подробности находятся в начале [отчёта](Experiments3D/README.ru.md). Диагностические точки ровности земли не являются фиксированными местами строительства.

После запроса пользователя выполнены [новые опыты генерации с водой и лавой](Experiments3D/README.ru.md): 60 ландшафтов, 120 геометрических оценок, пять отрисовок в Unity и интерактивное сравнение 15 результатов. Сравниваются холмистая поляна, река с бродами, озёрный берег, затопленные низины и вулканическая чаша. Это геометрические эксперименты, ещё не интеграция в матч. Подъём жидкости рассматривается с закреплёнными входами и ресурсами.

## Прежнее исследование плоской арены

Следующие изображения, аудиты и результаты относятся к прежнему плоскому варианту. Они сохраняют историю исследования и не являются доказательством новой 3D-геометрии или визуальным эталоном. При расхождении действует актуальный Plan.ru.md.

Для быстрого просмотра:

- [Прежние схемы давления на плоской арене](Results/ProfileConcepts.svg).
- [Плоская проба компактного и разнесённого хозяйства](Results/LocalResourceVariants.svg).
- [Независимый аудит прежней редакции](FinalCritique.ru.md).
- [Проверка прежнего плана генератора](GeneratorFinalReview.ru.md).

Независимые первые позиции и последующие передачи сохранены в GeneratorRound1–3.ru.md и CriticRound1–3.ru.md. [InitialSyncOptions](InitialSyncOptions.ru.md) сохраняет обсуждение строгой сетевой готовности и причину, по которой она не стала обязательным дополнительным протоколом первого MVP.

При расхождении раннего меморандума и итогового решения актуален Plan.ru.md. Первые раунды сохраняют историю независимой критики и не задают дополнительный объём реализации.

## Доказательства и их границы

| Файл результата | Область проверки |
| --- | --- |
| Results/OfflineSummary.json | 12 000 бинарных моделей; суррогатные метрики, не игровые pass rate |
| Results/RuntimePathProbe.json | Реальные decision/flow/movement, четыре временных World, стены и проём; без урона и сети |
| Results/GeometryRasterProbe.json | Реальная растеризация circle/box в NavigationFlowSystem |
| Results/RuntimeLayoutBatch.json | 128 схематических layouts, 512 агентов; достижение первого intent к церкви без боя и сети |
| Results/RuntimeEconomyProbe.json | Охват одной лесопилки двумя древесными узлами; без пополнения, боя и placement |
| Results/RuntimeResourceCadence.json | 16 сочетаний той же геометрии, синтетического пополнения и ручной добычи; не действующий матч |

Первый офлайн-оракул ширины был исправлен до интерпретации: расширенный блокер церкви перекрывал саму исходную точку проверки. Все опубликованные офлайн-сводки относятся к исправленному запуску. Его оставшиеся допущения и смещения подробно разобраны критиком в CriticRound2.ru.md. Общий процент, UniqueMasks и первый intent не заменяют игровые, сетевые и телефонные проверки.

## Повторение опытов

Исследование выполнено на Unity 6000.6.6f1 и установленном Netcode/Entities 6.6.0. Python-часть использует numpy и matplotlib, доступные в текущей среде. Для повторения не требуется менять игровой проект или устанавливать новый пакет Unity.

Офлайн-модели:

```powershell
Set-Location -LiteralPath 'M:/Prototypes/VaticanDefendor'
python docs/Planning/ProceduralMaps/Study.py --seeds 1000 --output Builds/MapPlanning/ReproducedStudy
python docs/Planning/ProceduralMaps/Counterexamples.py
python docs/Planning/ProceduralMaps/DrawConcepts.py
python docs/Planning/ProceduralMaps/DrawLocalVariants.py
```

Пробы настоящих систем запускаются в открытом Unity Editor с остановленным Play. Они создают и освобождают отдельные временные Worlds, а не запускают матч пользователя. Результаты записываются в Builds/MapPlanning. Пример:

```powershell
Set-Location -LiteralPath 'M:/Prototypes/VaticanDefendor'
New-Item -ItemType Directory -Force -Path 'M:/Prototypes/VaticanDefendor/Builds/MapPlanning' | Out-Null
$ProbeCode = Get-Content -LiteralPath 'M:/Prototypes/VaticanDefendor/docs/Planning/ProceduralMaps/RuntimePathProbe.cs.txt' -Raw
unity command eval --code $ProbeCode --project-path M:/Prototypes/VaticanDefendor --caller plugin --skill unity-cli --format json
```

Аналогично запускаются GeometryRasterProbe.cs.txt, RuntimeEconomyProbe.cs.txt и RuntimeResourceCadence.cs.txt. Для RuntimeLayoutBatch.cs.txt сначала копируется сохранённый вход:

```powershell
Copy-Item -LiteralPath 'M:/Prototypes/VaticanDefendor/docs/Planning/ProceduralMaps/Results/RuntimeLayouts.json' -Destination 'M:/Prototypes/VaticanDefendor/Builds/MapPlanning/RuntimeLayouts.json'
$ProbeCode = Get-Content -LiteralPath 'M:/Prototypes/VaticanDefendor/docs/Planning/ProceduralMaps/RuntimeLayoutBatch.cs.txt' -Raw
unity command eval --code $ProbeCode --project-path M:/Prototypes/VaticanDefendor --caller plugin --skill unity-cli --format json
```

Проверяется вложенный результат eval, включая diagnostics/success; внешний успешный ответ CLI сам по себе не доказывает успешного выполнения C#.

Названия полей старой исследовательской модели сохранены вместе с её критикой: MapHash в Study.py хеширует только blocked mask и не является предложенной канонической identity игрового манифеста. JSON с исходными результатами не содержит учётных данных, токенов или backend secrets.
