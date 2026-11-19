using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5002");

// Строка подключения из environment-переменной DB (не хардкод).
var connStr = builder.Configuration["DB"]
    ?? "Host=db;Port=5432;Database=app;Username=postgres;Password=1234";
builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connStr).Build());

var app = builder.Build();
var ds = app.Services.GetRequiredService<NpgsqlDataSource>();

await InitDb(ds);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "inventory", time = DateTime.UtcNow }));

// Создать склад
app.MapPost("/warehouses", async (HttpRequest req) =>
{
    var body = await req.ReadFromJsonAsync<WarehouseDto>();
    if (body is null || string.IsNullOrWhiteSpace(body.name))
        return Results.BadRequest(new { error = "name is required" });

    await using var cmd = ds.CreateCommand(
        "INSERT INTO inventory.warehouses (name, location) VALUES ($1, $2) RETURNING id");
    cmd.Parameters.AddWithValue(body.name.Trim());
    cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(body.location) ? "" : body.location.Trim());
    var id = (int)(await cmd.ExecuteScalarAsync())!;
    return Results.Created($"/warehouses/{id}", new { id, name = body.name, location = body.location });
});

// Список складов
app.MapGet("/warehouses", async () =>
{
    var list = new List<object>();
    await using var cmd = ds.CreateCommand(
        "SELECT id, name, location FROM inventory.warehouses ORDER BY id");
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        list.Add(new { id = r.GetInt32(0), name = r.GetString(1), location = r.GetString(2) });
    return Results.Ok(list);
});

// Остатки на складе
app.MapGet("/warehouses/{id:int}/stock", async (int id) =>
{
    if (!await WarehouseExists(ds, id))
        return Results.NotFound(new { error = $"warehouse {id} not found" });

    var items = new List<object>();
    await using var cmd = ds.CreateCommand(
        "SELECT product_id, quantity FROM inventory.stock WHERE warehouse_id = $1 ORDER BY product_id");
    cmd.Parameters.AddWithValue(id);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        items.Add(new { productId = r.GetInt32(0), quantity = r.GetInt32(1) });
    return Results.Ok(new { warehouseId = id, items });
});

// Внутренний endpoint: изменение остатка (вызывается Moves Service по HTTP).
// Не позволяет увести остаток в минус.
app.MapPost("/inventory/adjust", async (AdjustDto body) =>
{
    if (body.warehouseId <= 0 || body.productId <= 0 || body.delta == 0)
        return Results.BadRequest(new { error = "warehouseId>0, productId>0 and delta!=0 are required" });

    if (!await WarehouseExists(ds, body.warehouseId))
        return Results.NotFound(new { error = $"warehouse {body.warehouseId} not found" });

    // 1) Пытаемся изменить существующую строку, не допуская отрицательного остатка.
    await using var upd = ds.CreateCommand("""
        UPDATE inventory.stock SET quantity = quantity + $3
        WHERE warehouse_id = $1 AND product_id = $2 AND quantity + $3 >= 0
        RETURNING quantity
        """);
    upd.Parameters.AddWithValue(body.warehouseId);
    upd.Parameters.AddWithValue(body.productId);
    upd.Parameters.AddWithValue(body.delta);
    var newQty = await upd.ExecuteScalarAsync();

    if (newQty is null)
    {
        // Строки нет либо остатка не хватает — различаем эти случаи.
        await using var check = ds.CreateCommand(
            "SELECT quantity FROM inventory.stock WHERE warehouse_id = $1 AND product_id = $2");
        check.Parameters.AddWithValue(body.warehouseId);
        check.Parameters.AddWithValue(body.productId);
        var exists = await check.ExecuteScalarAsync() is not null;

        if (exists || body.delta < 0)
            return Results.BadRequest(new { error = "insufficient stock" });

        // Строки не было и delta > 0 — создаем. ON CONFLICT защищает от гонки.
        await using var ins = ds.CreateCommand("""
            INSERT INTO inventory.stock (warehouse_id, product_id, quantity) VALUES ($1, $2, $3)
            ON CONFLICT (warehouse_id, product_id)
            DO UPDATE SET quantity = inventory.stock.quantity + EXCLUDED.quantity
            RETURNING quantity
            """);
        ins.Parameters.AddWithValue(body.warehouseId);
        ins.Parameters.AddWithValue(body.productId);
        ins.Parameters.AddWithValue(body.delta);
        newQty = await ins.ExecuteScalarAsync();
    }

    return Results.Ok(new { warehouseId = body.warehouseId, productId = body.productId, quantity = (int)newQty! });
});

app.Run();

static async Task<bool> WarehouseExists(NpgsqlDataSource ds, int id)
{
    await using var cmd = ds.CreateCommand("SELECT 1 FROM inventory.warehouses WHERE id = $1");
    cmd.Parameters.AddWithValue(id);
    return await cmd.ExecuteScalarAsync() is not null;
}

// Инициализация схемы с повторными попытками (ждем подъем PostgreSQL).
static async Task InitDb(NpgsqlDataSource ds)
{
    const string sql = """
        CREATE SCHEMA IF NOT EXISTS inventory;
        CREATE TABLE IF NOT EXISTS inventory.warehouses (
            id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name TEXT NOT NULL,
            location TEXT NOT NULL DEFAULT '',
            created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS inventory.stock (
            warehouse_id INT NOT NULL REFERENCES inventory.warehouses(id),
            product_id INT NOT NULL,
            quantity INT NOT NULL DEFAULT 0 CHECK (quantity >= 0),
            PRIMARY KEY (warehouse_id, product_id)
        );
        """;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var cmd = ds.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
            return;
        }
        catch (Exception) when (attempt < 30)
        {
            await Task.Delay(2000);
        }
    }
}

record WarehouseDto(string? name, string? location);
record AdjustDto(int warehouseId, int productId, int delta);
