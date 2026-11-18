# Локальная проверка 2026-11-17

- `../../../docs/infra/superset/verification/last-check.txt` — успешная проверка итогового Docker-образа: семь наборов метрик и снимков, 45 HTTP query contexts, период/категория и списки значений на пяти страницах, read-only доступ. Проверены повтор, неизвестный/нулевой XP, разные валюты, отрицательная задержка, миллисекундная граница, перенос даты при исправлении и сохранение снимка при сбое.
- `../../../docs/infra/superset/verification/benchmark.json` — три измеренных повтора после прогрева, все 44 бизнес-графика, 2,31 млн синтетических физических строк. Совпали обычный и внутридневной интервалы. Это данные отдельного временного окружения; не показатели реальных игроков.
- Все восемь страниц открыты в браузере; наличие фактов и успешность SQL проверяются API и SQL-проверкой выше. Скриншот служебной страницы с семью актуальными витринами: `backend/.runtime/bi-proof/optimized-bi-health.jpg` (локальный, не хранится в git).
- `docker compose config --quiet` для общего compose.yaml прошёл. Исходные таблицы и контракты сервисов не изменялись.

Описание условий замера и пределов: [PERFORMANCE.ru.md](../../../docs/infra/superset/PERFORMANCE.ru.md). Повторный запуск: [README.ru.md](../README.ru.md#проверка).


Демо с постоянными тестовыми фактами и отдельными копиями: [DEMO.ru.md](../../../docs/infra/superset/DEMO.ru.md). `../../../docs/infra/superset/verification/demo.json` хранит независимые ожидаемые значения среза; `../../../docs/infra/superset/verification/demo-check.txt` — результаты 45 заполненных графиков, фильтров и проверок генератора. Это отдельная проверка от исторического benchmark выше.

Понятность всех восьми демо-страниц: [READABILITY.ru.md](../../../docs/infra/superset/READABILITY.ru.md). `../../../docs/infra/superset/verification/readability-check.txt` фиксирует сверку 45 демо-графиков, рабочих графиков и сохранность основных фактов/дашбордов после обновления подписей.

Обработка ошибок: [ERRORS.ru.md](../../../docs/infra/superset/ERRORS.ru.md); `../../../docs/infra/superset/verification/error-check.txt` — unit-тесты, реальные ошибки HTTP, состояния refresh и регрессия 90 графиков.

Рабочие пояснения: [PRODUCTION_GUIDANCE.ru.md](../../../docs/infra/superset/PRODUCTION_GUIDANCE.ru.md); `../../../docs/infra/superset/verification/production-guidance-check.txt` — полнота всех 45 определений, 8 страниц без обрезания текста, HTTP-проверка графиков, неизменность запросов и демо.

Профиль игрока: [PLAYERS.ru.md](../../../docs/infra/superset/PLAYERS.ru.md); `../../../docs/infra/superset/verification/player-check.txt` — 28 новых запросов, независимая сверка двух игроков за 30/7/1 день, пустой выбор, неизвестный ID, регрессия 90 графиков и проверка фильтра в браузере.
