using System.Diagnostics;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

// Адреса сервисов — из environment-переменных (задаются в docker-compose.yml).
string Env(string name, string fallback) => builder.Configuration[name] ?? fallback;

// Адрес health-эндпоинта: работает и с завершающим слешем, и без него.
string HealthUrl(string name, string fallback)
{
    var value = Env(name, fallback);
    return value.EndsWith('/') ? value + "health" : value + "/health";
}

var routes = new[]
{
    new RouteConfig
    {
        RouteId = "products",
        ClusterId = "products",
        Match = new RouteMatch { Path = "/api/products/{**catch-all}" },
        Transforms = new List<Dictionary<string, string>>
        {
            new() { ["PathRemovePrefix"] = "/api" }
        }
    },
    new RouteConfig
    {
        RouteId = "warehouses",
        ClusterId = "inventory",
        Match = new RouteMatch { Path = "/api/warehouses/{**catch-all}" },
        Transforms = new List<Dictionary<string, string>>
        {
            new() { ["PathRemovePrefix"] = "/api" }
        }
    },
    new RouteConfig
    {
        RouteId = "inventory",
        ClusterId = "inventory",
        Match = new RouteMatch { Path = "/api/inventory/{**catch-all}" },
        Transforms = new List<Dictionary<string, string>>
        {
            new() { ["PathRemovePrefix"] = "/api" }
        }
    },
    new RouteConfig
    {
        RouteId = "movements",
        ClusterId = "movements",
        Match = new RouteMatch { Path = "/api/movements/{**catch-all}" },
        Transforms = new List<Dictionary<string, string>>
        {
            new() { ["PathRemovePrefix"] = "/api" }
        }
    },
    new RouteConfig
    {
        RouteId = "reports",
        ClusterId = "reports",
        Match = new RouteMatch { Path = "/api/reports/{**catch-all}" },
        Transforms = new List<Dictionary<string, string>>
        {
            new() { ["PathRemovePrefix"] = "/api/reports" }
        }
    },

    // Базовые пути без завершающей части: /api/products -> /products и т.д.
    // Без этих маршрутов YARP матчит только /api/products/... и отдаёт 404
    // для запросов самого пульта (/api/products, /api/warehouses, ...).
    new RouteConfig
    {
        RouteId = "products-root",
        ClusterId = "products",
        Match = new RouteMatch { Path = "/api/products" },
        Transforms = new List<Dictionary<string, string>>
        {
            new() { ["PathRemovePrefix"] = "/api" }
        }
    },
    new RouteConfig
    {
        RouteId = "warehouses-root",
        ClusterId = "inventory",
        Match = new RouteMatch { Path = "/api/warehouses" },
        Transforms = new List<Dictionary<string, string>>
        {
            new() { ["PathRemovePrefix"] = "/api" }
        }
    },
    new RouteConfig
    {
        RouteId = "inventory-root",
        ClusterId = "inventory",
        Match = new RouteMatch { Path = "/api/inventory" },
        Transforms = new List<Dictionary<string, string>>
        {
            new() { ["PathRemovePrefix"] = "/api" }
        }
    },
    new RouteConfig
    {
        RouteId = "movements-root",
        ClusterId = "movements",
        Match = new RouteMatch { Path = "/api/movements" },
        Transforms = new List<Dictionary<string, string>>
        {
            new() { ["PathRemovePrefix"] = "/api" }
        }
    },
    new RouteConfig
    {
        RouteId = "reports-root",
        ClusterId = "reports",
        Match = new RouteMatch { Path = "/api/reports" },
        Transforms = new List<Dictionary<string, string>>
        {
            new() { ["PathRemovePrefix"] = "/api" }
        }
    }
};

var clusters = new[]
{
    new ClusterConfig
    {
        ClusterId = "products",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["d1"] = new DestinationConfig { Address = Env("PRODUCTS_URL", "http://products:5001/") }
        }
    },
    new ClusterConfig
    {
        ClusterId = "inventory",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["d1"] = new DestinationConfig { Address = Env("INVENTORY_URL", "http://inventory:5002/") }
        }
    },
    new ClusterConfig
    {
        ClusterId = "movements",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["d1"] = new DestinationConfig { Address = Env("MOVEMENTS_URL", "http://movements:5003/") }
        }
    },
    new ClusterConfig
    {
        ClusterId = "reports",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["d1"] = new DestinationConfig { Address = Env("REPORT_URL", "http://reports:5004/") }
        }
    }
};

builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);
builder.Services.AddHttpClient();

var app = builder.Build();

// Точка проверки самого шлюза.
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "gateway", time = DateTime.UtcNow }));

// Сводная диагностика: реально опрашивает все health-эндпоинты (аналог /status
// в репозитории-примере). Ошибка одного сервиса не ломает ответ целиком.
app.MapGet("/status", async (IHttpClientFactory http) =>
{
    var targets = new (string Name, string Url)[]
    {
        ("products", HealthUrl("PRODUCTS_URL", "http://products:5001/")),
        ("inventory", HealthUrl("INVENTORY_URL", "http://inventory:5002/")),
        ("movements", HealthUrl("MOVEMENTS_URL", "http://movements:5003/")),
        ("reports", HealthUrl("REPORT_URL", "http://reports:5004/"))
    };

    var checks = await Task.WhenAll(targets.Select(async target =>
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await http.CreateClient().GetAsync(target.Url, cts.Token);
            sw.Stop();
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return new
            {
                service = target.Name,
                status = response.IsSuccessStatusCode ? "ok" : "error",
                httpCode = (int)response.StatusCode,
                responseMs = sw.ElapsedMilliseconds,
                detail = body.Length > 160 ? body[..160] : body
            };
        }
        catch (Exception e)
        {
            sw.Stop();
            return new
            {
                service = target.Name,
                status = "error",
                httpCode = 0,
                responseMs = sw.ElapsedMilliseconds,
                detail = e.GetType().Name
            };
        }
    }));

    var overall = checks.All(c => c.status == "ok") ? "ok" : "degraded";
    return Results.Ok(new { status = overall, time = DateTime.UtcNow, services = checks });
});

// Веб-пульт (статические файлы из web/, смонтированные в /app/wwwroot).
app.UseDefaultFiles();
app.UseStaticFiles();

Console.WriteLine("  WAREHOUSE CONTROL DESK -> http://localhost:8080/");

// Все остальные запросы — проксирование.
app.MapReverseProxy();

app.Run();
