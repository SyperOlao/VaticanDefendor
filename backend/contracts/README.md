# Внутренние gRPC-контракты

- `auth/v1/auth.proto`: проверка токена, актуальные роли и разрешения.
- `inventory/v1/inventory.proto`: снимок выбранного оружия и статус команды.
- `wallet/v1/wallet.proto`: сохранённый статус финансовой команды.

Из корня проекта, в Python 3.12 окружении с `grpcio-tools==1.85.1`:

```sh
python -m pip install grpcio-tools==1.85.1
python backend/contracts/generate_auth.py
python backend/contracts/generate_inventory.py
python backend/contracts/generate_wallet.py
```

Генераторы копируют только stubs контракта в независимо собираемые сервисы. Импортировать реализацию/ORM соседнего сервиса нельзя. При изменении protobuf сохраняйте существующие номера полей; удалённые номера резервируйте. Все runtime-клиенты используют deadline и ограничение размера сообщения. Внутренние gRPC-порты не проксируются через публичный gateway.

Изменения состояния выполняются командами RabbitMQ; HTTP остаётся для внешнего API, контента, health и служебных методов. Полная матрица — `backend/docs/INTERSERVICE_AUDIT.ru.md`.
