#!/usr/bin/env bash
# Сценарий проверки системы "Учет складских товаров".
# Запускать после: docker compose up --build -d
set -e

GW="http://localhost:8080"
JSON="Content-Type: application/json"

echo "=== 0. Здоровье сервисов ==="
curl -s $GW/health; echo
curl -s $GW/api/products/health; echo
curl -s $GW/api/inventory/health; echo
curl -s $GW/api/movements/health; echo

echo "=== 1. Создать товар ==="
curl -s -X POST $GW/api/products -H "$JSON" \
  -d '{"sku":"SKU-001","name":"Кабель HDMI 1м","unit":"шт"}'; echo

echo "=== 2. Создать склад ==="
curl -s -X POST $GW/api/warehouses -H "$JSON" \
  -d '{"name":"Основной склад","location":"Москва, ул. Заводская 1"}'; echo

echo "=== 3. Приход 100 шт (warehouseId=1, productId=1) ==="
curl -s -X POST $GW/api/movements -H "$JSON" \
  -d '{"type":"IN","warehouseId":1,"productId":1,"quantity":100}'; echo

echo "=== 4. Остатки на складе 1 ==="
curl -s $GW/api/warehouses/1/stock; echo

echo "=== 5. Расход 30 шт ==="
curl -s -X POST $GW/api/movements -H "$JSON" \
  -d '{"type":"OUT","warehouseId":1,"productId":1,"quantity":30}'; echo

echo "=== 6. История операций ==="
curl -s $GW/api/movements; echo

echo "=== 7. Нехватка остатка (ожидается 400) ==="
curl -s -o /dev/null -w "%{http_code}\n" -X POST $GW/api/movements -H "$JSON" \
  -d '{"type":"OUT","warehouseId":1,"productId":1,"quantity":1000}'

echo "=== 8. Перезапуск и проверка персистентности ==="
docker compose down
docker compose up -d
sleep 5
curl -s $GW/api/warehouses/1/stock; echo
curl -s $GW/api/movements; echo

echo "=== 9. Веб-пульт, отчёты и диагностика ==="
# Интерфейс: открыть http://localhost/ в браузере
curl -s -o /dev/null -w "UI: %{http_code}\n" $GW/

# Сводка для пульта (KPI, остатки, низкие остатки, последние операции)
curl -s $GW/api/reports/summary; echo

# Готовый срез остатков: склад + товар + количество + состояние
curl -s $GW/api/reports/stock; echo

# Текстовый отчёт — то, что ВЫВОДИТ система
echo "--- начало текстового отчёта ---"
curl -s $GW/api/reports/text
echo "--- конец текстового отчёта ---"

# Агрегированная диагностика всех сервисов
curl -s $GW/status; echo

echo "=== 10. Проверка заголовка текстового отчёта ==="
curl -s -I $GW/api/reports/text | head -n 5
