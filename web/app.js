/* =========================================================
   СКЛАД-ПУЛЬТ — логика интерфейса.
   Данные всегда берутся из gateway (/api/...). Если Docker не
   запущен, включается явно помеченный демо-режим: интерфейс
   всё равно выводит данные и отчёт, но не притворяется живым API.
   ========================================================= */

const $ = (selector) => document.querySelector(selector);

const DEMO = {
  products: [
    { id: 1, sku: 'SKU-001', name: 'Кабель HDMI 1м', unit: 'шт' },
    { id: 2, sku: 'SKU-002', name: 'Маркер чёрный', unit: 'шт' },
    { id: 3, sku: 'SKU-003', name: 'Паллет деревянная 1200×800', unit: 'шт' },
    { id: 4, sku: 'SKU-004', name: 'Перчатки рабочие', unit: 'пар' }
  ],
  warehouses: [
    { id: 1, name: 'Основной склад', location: 'Москва, ул. Заводская 1' },
    { id: 2, name: 'Резервный склад', location: 'Подольск, складской комплекс' }
  ],
  stock: [
    row(1, 'Основной склад', 'Москва, ул. Заводская 1', 1, 'SKU-001', 'Кабель HDMI 1м', 'шт', 70),
    row(1, 'Основной склад', 'Москва, ул. Заводская 1', 2, 'SKU-002', 'Маркер чёрный', 'шт', 4),
    row(1, 'Основной склад', 'Москва, ул. Заводская 1', 3, 'SKU-003', 'Паллет деревянная 1200×800', 'шт', 18),
    row(2, 'Резервный склад', 'Подольск, складской комплекс', 1, 'SKU-001', 'Кабель HDMI 1м', 'шт', 25),
    row(2, 'Резервный склад', 'Подольск, складской комплекс', 4, 'SKU-004', 'Перчатки рабочие', 'пар', 0)
  ],
  movements: [
    { id: 4, type: 'OUT', warehouseId: 1, productId: 2, productSku: 'SKU-002', productName: 'Маркер чёрный', quantity: 6, balance: 4, createdAt: '2026-11-19T09:42:00Z' },
    { id: 3, type: 'IN', warehouseId: 2, productId: 1, productSku: 'SKU-001', productName: 'Кабель HDMI 1м', quantity: 25, balance: 25, createdAt: '2026-11-19T09:15:00Z' },
    { id: 2, type: 'OUT', warehouseId: 1, productId: 1, productSku: 'SKU-001', productName: 'Кабель HDMI 1м', quantity: 30, balance: 70, createdAt: '2026-11-18T16:20:00Z' },
    { id: 1, type: 'IN', warehouseId: 1, productId: 1, productSku: 'SKU-001', productName: 'Кабель HDMI 1м', quantity: 100, balance: 100, createdAt: '2026-11-18T15:58:00Z' }
  ],
  warnings: []
};

const DEMO_STATUS = {
  status: 'demo',
  services: [
    { service: 'products', status: 'error', responseMs: 0, httpCode: 0 },
    { service: 'inventory', status: 'error', responseMs: 0, httpCode: 0 },
    { service: 'movements', status: 'error', responseMs: 0, httpCode: 0 },
    { service: 'reports', status: 'error', responseMs: 0, httpCode: 0 }
  ]
};

function row(warehouseId, warehouseName, location, productId, sku, name, unit, quantity) {
  return { warehouseId, warehouseName, location, productId, sku, name, unit, quantity, state: stateOf(quantity) };
}

let mode = 'wait';
let latest = null;

function stateOf(quantity, threshold = 5) {
  if (quantity <= 0) return 'НЕТ';
  if (quantity <= threshold) return 'МАЛО';
  return 'НОРМА';
}

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  })[ch]);
}

function fmtDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : date.toISOString().slice(0, 16).replace('T', ' ');
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }
  });
  const text = await response.text();
  let body;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }

  if (!response.ok) {
    const detail = typeof body === 'string' ? body.slice(0, 180) : (body?.error || 'ошибка запроса');
    throw new Error(`${response.status}: ${detail}`);
  }
  return body;
}

function log(message, kind = 'dim') {
  const terminal = $('#terminal');
  const line = document.createElement('div');
  line.className = `terminal-line ${kind}`;

  const time = document.createElement('span');
  time.className = 'terminal-time';
  time.textContent = `[${new Date().toLocaleTimeString('ru-RU', { hour12: false })}] `;

  const msg = document.createElement('span');
  msg.className = 'terminal-msg';
  msg.textContent = message;

  line.append(time, msg);
  terminal.prepend(line);
  while (terminal.children.length > 80) terminal.lastElementChild.remove();
}

function setMode(next, text) {
  mode = next;
  const badge = $('#modeBadge');
  badge.className = `mode-badge mode-${next}`;
  badge.textContent = text;
}

function derive(data) {
  const stock = data?.stock ?? [];
  const movements = data?.movements ?? [];
  return {
    products: data?.products?.length ?? 0,
    warehouses: data?.warehouses?.length ?? 0,
    totalUnits: stock.reduce((sum, item) => sum + Number(item.quantity || 0), 0),
    incoming: movements.filter((m) => m.type === 'IN').length,
    outgoing: movements.filter((m) => m.type === 'OUT').length,
    lowStockCount: stock.filter((item) => item.state !== 'НОРМА').length
  };
}

function renderKpis(data) {
  const s = derive(data);
  const cards = [
    { label: 'Товаров в справочнике', value: s.products, unit: 'ед.', hint: 'products-service', alert: false },
    { label: 'Складов в системе', value: s.warehouses, unit: 'ед.', hint: 'inventory-service', alert: false },
    { label: 'Всего на остатке', value: s.totalUnits, unit: 'шт', hint: 'сумма по всем складам', alert: false },
    { label: 'Операций приход', value: s.incoming, unit: 'шт', hint: 'тип IN', alert: false },
    { label: 'Операций расход', value: s.outgoing, unit: 'шт', hint: 'тип OUT', alert: false },
    { label: 'Требуют внимания', value: s.lowStockCount, unit: 'поз.', hint: 'порог 5 шт', alert: s.lowStockCount > 0 }
  ];

  $('#kpis').innerHTML = cards.map((card) => `
    <div class="kpi ${card.alert ? 'is-alert' : ''}">
      <div class="kpi-label">${esc(card.label)}</div>
      <div class="kpi-value">${esc(card.value)}<span class="kpi-unit">${esc(card.unit)}</span></div>
      <div class="kpi-hint">${esc(card.hint)}</div>
    </div>
  `).join('');
}

function renderStock(data) {
  if (!data) return;
  const query = $('#stockSearch').value.trim().toLowerCase();
  const rows = [...(data.stock || [])]
    .filter((item) => [item.warehouseName, item.sku, item.name, item.location]
      .some((field) => String(field || '').toLowerCase().includes(query)))
    .sort((a, b) => a.warehouseId - b.warehouseId || a.sku.localeCompare(b.sku));

  const max = Math.max(1, ...rows.map((item) => Number(item.quantity || 0)));
  const badgeClass = (state) => state === 'НОРМА' ? 'ok' : state === 'МАЛО' ? 'low' : 'none';

  $('#stockRows').innerHTML = rows.length ? rows.map((item) => {
    const percent = Math.max(5, Math.round((Number(item.quantity) / max) * 100));
    return `
      <tr>
        <td>${esc(item.warehouseName)}<span class="product-name">${esc(item.location)}</span></td>
        <td><span class="sku">${esc(item.sku)}</span><span class="product-name">${esc(item.name)}</span></td>
        <td class="num">${esc(item.quantity)} <span class="muted">${esc(item.unit)}</span></td>
        <td>
          <span class="badge badge-${badgeClass(item.state)}">${esc(item.state)}</span>
          <span class="bar ${item.state === 'НОРМА' ? '' : 'is-low'}"><i style="width:${percent}%"></i></span>
        </td>
      </tr>`;
  }).join('') : '<tr><td colspan="4" class="empty">Совпадений нет</td></tr>';

  $('#stockCount').textContent = `СТРОК: ${rows.length} / ${(data.stock || []).length}`;
}

function renderMovements(data) {
  if (!data) return;
  const filter = $('#movementFilter').value;
  const warehouseNames = new Map((data.warehouses || []).map((w) => [w.id, w.name]));
  const rows = (data.movements || [])
    .filter((m) => filter === 'ALL' || m.type === filter)
    .slice()
    .sort((a, b) => b.id - a.id);

  $('#movementRows').innerHTML = rows.length ? rows.map((m) => `
    <tr>
      <td class="num">#${esc(m.id)}</td>
      <td><span class="badge badge-${m.type === 'IN' ? 'in' : 'out'}">${m.type === 'IN' ? 'ПРИХОД' : 'РАСХОД'}</span></td>
      <td class="muted">${esc(fmtDate(m.createdAt))}</td>
      <td><span class="sku">${esc(m.productSku)}</span>
          <span class="product-name">${esc(m.productName)} / ${esc(warehouseNames.get(m.warehouseId) || `склад ${m.warehouseId}`)}</span></td>
      <td class="num">${m.type === 'IN' ? '+' : '−'}${esc(m.quantity)}</td>
      <td class="num">${esc(m.balance)}</td>
    </tr>
  `).join('') : '<tr><td colspan="6" class="empty">Операций пока нет</td></tr>';

  $('#movementCount').textContent = `ЗАПИСЕЙ: ${rows.length}`;
}

function renderSelects(data) {
  const warehouse = $('#warehouseSelect');
  const product = $('#productSelect');
  const previousWarehouse = warehouse.value;
  const previousProduct = product.value;

  warehouse.innerHTML = (data.warehouses || []).map((w) =>
    `<option value="${esc(w.id)}">${esc(w.name)}</option>`).join('') || '<option value="">нет складов</option>';

  product.innerHTML = (data.products || []).map((p) =>
    `<option value="${esc(p.id)}">${esc(p.sku)} — ${esc(p.name)}</option>`).join('') || '<option value="">нет товаров</option>';

  if ([...warehouse.options].some((o) => o.value === previousWarehouse)) warehouse.value = previousWarehouse;
  if ([...product.options].some((o) => o.value === previousProduct)) product.value = previousProduct;
}

function renderServices(status) {
  const target = status || DEMO_STATUS;
  const rows = (target.services || []).map((service) => {
    const ok = service.status === 'ok';
    return `
      <div class="service-row ${ok ? 'is-ok' : ''}">
        <span class="led" aria-hidden="true"></span>
        <span class="service-name">${esc(service.service)}</span>
        <span class="service-ms">${Number(service.responseMs || 0)} мс</span>
        <span class="service-code">${esc(service.httpCode || '—')}</span>
      </div>`;
  }).join('');

  $('#services').innerHTML = rows + (status ? '' :
    '<p class="form-hint">Агрегатор /status недоступен. Показан демо-статус.</p>');
}

function render(data, status) {
  latest = data;
  renderKpis(data);
  renderStock(data);
  renderMovements(data);
  renderSelects(data);
  renderServices(status);
}

async function joinStock(warehouses, products) {
  const byId = new Map(products.map((p) => [p.id, p]));
  const rows = [];
  for (const warehouse of warehouses) {
    try {
      const stock = await api(`/api/warehouses/${warehouse.id}/stock`);
      for (const item of stock.items || []) {
        const product = byId.get(item.productId);
        rows.push(row(
          warehouse.id, warehouse.name, warehouse.location,
          item.productId,
          product?.sku || `#${item.productId}`,
          product?.name || 'неизвестный товар',
          product?.unit || 'шт',
          item.quantity
        ));
      }
    } catch (error) {
      log(`Остатки склада «${warehouse.name}»: ${error.message}`, 'warn');
    }
  }
  return rows;
}

async function refresh(silent = false) {
  if (!silent) log('Запрос свежих данных…');
  $('#refreshBtn').disabled = true;

  try {
    const [products, warehouses, movements] = await Promise.all([
      api('/api/products'),
      api('/api/warehouses'),
      api('/api/movements')
    ]);

    let stock;
    let warnings = [];
    try {
      const summary = await api('/api/reports/summary');
      stock = summary.stock || [];
      warnings = summary.warnings || [];
    } catch (error) {
      log(`Сервис отчётов недоступен, остатки собраны напрямую: ${error.message}`, 'warn');
      stock = await joinStock(warehouses, products);
    }

    render({ products, warehouses, movements, stock, warnings });
    setMode('live', 'API ОНЛАЙН');

    if (!silent) {
      log(`Принято: товаров ${products.length}, складов ${warehouses.length}, операций ${movements.length}, остатков ${stock.length}.`, 'ok');
    }
    warnings.forEach((warning) => log(`! ${warning}`, 'warn'));
  } catch (error) {
    render(JSON.parse(JSON.stringify(DEMO)), DEMO_STATUS);
    setMode('demo', 'ДЕМО-ДАННЫЕ');
    log(`API недоступен (${error.message}). Показан демо-срез — запустите docker compose up --build -d.`, 'warn');
  }

  let status = null;
  try { status = await api('/status'); } catch { /* диагностика необязательна для таблиц */ }
  if (mode === 'live') renderServices(status);

  $('#refreshBtn').disabled = false;
}

async function submitAction(path, payload, label, form) {
  if (mode !== 'live') {
    log('Изменения доступны только с работающим API. Запустите Docker и обновите данные.', 'warn');
    return;
  }
  try {
    const result = await api(path, { method: 'POST', body: JSON.stringify(payload) });
    log(`${label}: ${JSON.stringify(result)}`, 'ok');
    if (form) form.reset();
    await refresh(true);
  } catch (error) {
    log(`${label} не выполнена: ${error.message}`, 'err');
  }
}

async function seedDemo() {
  if (mode !== 'live') {
    log('Демо-заполнение требует работающего API.', 'warn');
    return;
  }

  try {
    let products = await api('/api/products');
    let warehouses = await api('/api/warehouses');
    let movements = await api('/api/movements');
    let added = 0;

    for (const product of DEMO.products) {
      if (!products.some((p) => p.sku === product.sku)) {
        await api('/api/products', { method: 'POST', body: JSON.stringify(product) });
        added++;
      }
    }
    for (const warehouse of DEMO.warehouses) {
      if (!warehouses.some((w) => w.name === warehouse.name)) {
        await api('/api/warehouses', { method: 'POST', body: JSON.stringify(warehouse) });
        added++;
      }
    }

    products = await api('/api/products');
    warehouses = await api('/api/warehouses');
    movements = await api('/api/movements');

    if (movements.length === 0) {
      const productBySku = new Map(products.map((p) => [p.sku, p]));
      const warehouseByName = new Map(warehouses.map((w) => [w.name, w]));
      for (const movement of DEMO.movements) {
        await api('/api/movements', {
          method: 'POST',
          body: JSON.stringify({
            type: movement.type,
            warehouseId: warehouseByName.get(movement.warehouseName)?.id,
            productId: productBySku.get(movement.sku)?.id,
            quantity: movement.quantity
          })
        });
        added++;
      }
    }

    log(`Демо-заполнение завершено. Добавлено записей: ${added}.`, 'ok');
    await refresh(true);
  } catch (error) {
    log(`Демо-заполнение не удалось: ${error.message}`, 'err');
  }
}

function buildTextReport(data) {
  const s = derive(data);
  const lines = [
    '================================================================',
    '  ОТЧЁТ ПО СКЛАДУ  /  УЧЁТ СКЛАДСКИХ ТОВАРОВ',
    `  сформирован: ${new Date().toISOString().slice(0, 19).replace('T', ' ')} UTC`,
    '================================================================',
    '',
    `  Товаров в справочнике ......... ${String(s.products).padStart(5)}`,
    `  Складов ....................... ${String(s.warehouses).padStart(5)}`,
    `  Общее количество на складах ... ${String(s.totalUnits).padStart(5)} шт`,
    `  Операций прихода .............. ${String(s.incoming).padStart(5)}`,
    `  Операций расхода .............. ${String(s.outgoing).padStart(5)}`,
    `  Позиций с низким остатком ..... ${String(s.lowStockCount).padStart(5)}`,
    '',
    '--- ОСТАТКИ ПО СКЛАДАМ -----------------------------------------'
  ];

  const grouped = new Map();
  for (const item of data.stock || []) {
    if (!grouped.has(item.warehouseName)) grouped.set(item.warehouseName, []);
    grouped.get(item.warehouseName).push(item);
  }
  for (const [name, items] of grouped) {
    lines.push(`  ${name}`);
    for (const item of items) {
      lines.push(`    ${item.sku.padEnd(12)} ${item.name.padEnd(28)} ${String(item.quantity).padStart(5)} ${item.unit.padEnd(3)} [${item.state}]`);
    }
    lines.push('');
  }

  lines.push('--- ПОСЛЕДНИЕ ОПЕРАЦИИ -----------------------------------------');
  for (const movement of (data.movements || []).slice(0, 10)) {
    const sign = movement.type === 'IN' ? '+' : '-';
    lines.push(`  #${movement.id} ${fmtDate(movement.createdAt)}  ${movement.type}  ${movement.productSku} ${sign}${movement.quantity} -> остаток ${movement.balance}`);
  }
  lines.push('', '================================================================', '  Конец отчёта');
  return lines.join('\n');
}

function openReport() {
  if (mode === 'live') {
    window.open('/api/reports/text', '_blank', 'noopener');
    log('Текстовый отчёт открыт в новой вкладке.', 'ok');
    return;
  }
  const blob = new Blob([buildTextReport(latest || DEMO)], { type: 'text/plain;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  window.open(url, '_blank', 'noopener');
  setTimeout(() => URL.revokeObjectURL(url), 30000);
  log('API недоступен — сформирован локальный демо-отчёт.', 'warn');
}

function tickClock() {
  $('#clock').textContent = new Date().toLocaleTimeString('ru-RU', { hour12: false });
}

// ---------- События ----------
$('#refreshBtn').addEventListener('click', () => refresh());
$('#reportBtn').addEventListener('click', openReport);
$('#seedBtn').addEventListener('click', seedDemo);
$('#clearLog').addEventListener('click', () => { $('#terminal').innerHTML = ''; log('Терминал очищен.'); });
$('#stockSearch').addEventListener('input', () => renderStock(latest));
$('#movementFilter').addEventListener('change', () => renderMovements(latest));

$('#movementForm').addEventListener('submit', (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  const data = new FormData(form);
  const payload = {
    type: data.get('type'),
    warehouseId: Number(data.get('warehouseId')),
    productId: Number(data.get('productId')),
    quantity: Number(data.get('quantity'))
  };
  if (!payload.warehouseId || !payload.productId || !Number.isInteger(payload.quantity) || payload.quantity < 1) {
    log('Проверьте склад, товар и количество (целое число больше нуля).', 'err');
    return;
  }
  submitAction('/api/movements', payload, 'Операция проведена');
});

$('#productForm').addEventListener('submit', (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  const data = new FormData(form);
  submitAction('/api/products', {
    sku: String(data.get('sku') || '').trim(),
    name: String(data.get('name') || '').trim(),
    unit: String(data.get('unit') || 'шт').trim() || 'шт'
  }, 'Товар добавлен', form);
});

$('#warehouseForm').addEventListener('submit', (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  const data = new FormData(form);
  submitAction('/api/warehouses', {
    name: String(data.get('name') || '').trim(),
    location: String(data.get('location') || '').trim()
  }, 'Склад добавлен', form);
});

// ---------- Запуск ----------
tickClock();
setInterval(tickClock, 1000);
setInterval(() => refresh(true), 15000);
log('СКЛАД-ПУЛЬТ: запуск. Стиль — светлый промышленный терминал.');
refresh();
