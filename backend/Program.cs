using Microsoft.Extensions.DependencyInjection;
using HeatAlert;
using Npgsql;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.GetCurrentDirectory()
});

// THIS IS THE FIX: Disable "ReloadOnChange" for all configuration sources
builder.Configuration.Sources.Clear();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

// 1. CONFIGURATION & DATABASE CONNECTION
string? rawConn = builder.Configuration.GetConnectionString("DefaultConnection") 
                  ?? Environment.GetEnvironmentVariable("DATABASE_URL");

if (string.IsNullOrEmpty(rawConn)) 
{
    Console.WriteLine("❌ ERROR: Connection string is empty!");
    return; 
}

string connString = (rawConn.StartsWith("postgres://") || rawConn.StartsWith("postgresql://"))
    ? ConvertPostgresUrlToConnString(rawConn)
    : rawConn;

string botToken = builder.Configuration["BotSettings:TelegramToken"]!;

// 2. SERVICES SETUP
builder.Services.AddCors(options => {
    options.AddPolicy("FrontendOnly", policy => {
        policy.WithOrigins("https://heatsync-zs03.onrender.com") 
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var db = new DatabaseManager(connString);
var bot = new BotAlertSender(botToken, db);

builder.Services.AddSingleton(db);
builder.Services.AddSingleton(bot);

// --- THE PING TRICK: Register the Keep-Alive Service ---
builder.Services.AddHostedService<RenderKeepAliveService>();

builder.WebHost.ConfigureKestrel(options => 
{
    var port = Environment.GetEnvironmentVariable("PORT") ?? "5000"; 
    options.ListenAnyIP(int.Parse(port));
});

var app = builder.Build();

// 3. DATABASE VERIFICATION
try {
    using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    Console.WriteLine("🚀 SUCCESS: Connected to PostgreSQL!");
} catch (Exception ex) {
    Console.WriteLine($"❌ FATAL DB ERROR: {ex.Message}");
}

app.UseCors("FrontendOnly");
app.UseRouting();
app.RegisterAlertEndpoints(db);
app.RegisterAuthEndpoints();

// 4. THE V3 SIMULATION ENGINE
_ = Task.Run(async () => {
    bot.StartBot();
    var simulator = new HeatSimulator(); 

    while (true)
    {
        try 
        {
            var sensors = await db.GetAllSensors(); 
            var currentBatch = new List<AlertResult>();

            foreach (var sensor in sensors)
            {
                if (!sensor.IsActive) continue; 

                int simTemp = simulator.GenerateReading(sensor.BaselineTemp); 

                var result = new AlertResult {
                    SensorCode = sensor.SensorCode,
                    DisplayName = sensor.DisplayName,
                    BarangayName = sensor.Barangay,
                    RelativeLocation = sensor.DisplayName,
                    Lat = sensor.Lat,
                    Lng = sensor.Lng,
                    HeatIndex = simTemp,
                    CreatedAt = GlobalData.GetPHTime() 
                };

                GlobalData.LatestAlert = result; 
                await db.SaveHeatLog(result, sensor.Id); 
                currentBatch.Add(result);
                
                Console.WriteLine($"[V3 LOG] {sensor.DisplayName}: {simTemp}°C");
            }

            await bot.BroadcastHeartbeatSummary(currentBatch);
        }
        catch (Exception ex) { Console.WriteLine($"Simulation Loop Error: {ex.Message}"); }
        
        await Task.Delay(30000); 
    }
});

app.MapMethods("/", new[] { "GET", "HEAD" }, () => "HEALERTSYS V3 API is Live."); 

app.Run();

// --- HELPERS & CLASSES ---

string ConvertPostgresUrlToConnString(string url)
{
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':');
    int port = uri.Port <= 0 ? 5432 : uri.Port; 
    return $"Host={uri.Host};Port={port};Username={userInfo[0]};Password={userInfo[1]};Database={uri.AbsolutePath.Trim('/')};SslMode=Require;Trust Server Certificate=true;";
}

// THE PING TRICK CLASS
public class RenderKeepAliveService : BackgroundService
{
    private readonly string _url;
    private readonly HttpClient _httpClient = new();

    public RenderKeepAliveService()
    {
        // 1. Try to get the dynamic URL from Render
        // 2. Fallback to your hardcoded Render URL
        // 3. Last fallback to localhost for development
        _url = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL") 
               ?? "https://backend-9lv5.onrender.com";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay to let the server start up fully before the first ping
        await Task.Delay(5000, stoppingToken);

        Console.WriteLine($"🛰️ Keep-Alive Service Active: Targeting {_url}");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // We use GetAsync to the root URL "/"
                var response = await _httpClient.GetAsync(_url, stoppingToken);
                Console.WriteLine($"[PING] {DateTime.Now:T} - Status: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                // If it fails, we want to know why (DNS, Timeout, etc.)
                Console.WriteLine($"[PING ERROR] {ex.Message}");
            }

            // Ping every 10 minutes to stay within Render's 15-min window
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }
}

public static class GlobalData {
    public static AlertResult? LatestAlert { get; set; }

    public static DateTime GetPHTime() 
    {
        var phZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
        var phTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, phZone);
        
        // This tells the Database Driver: "Don't try to offset this, it's already local."
        return DateTime.SpecifyKind(phTime, DateTimeKind.Unspecified);
    }
}

public class SensorNode 
{
    public int Id { get; set; }
    public string SensorCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Barangay { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public int BaselineTemp { get; set; }
    public string EnvironmentType { get; set; } = "Unknown"; // e.g., 'Concrete'
    public bool IsActive { get; set; } = true;
}

public class SensorUpdateDto
    {
        public string? SensorCode { get; set; }
        public string? DisplayName { get; set; }
        public string? Barangay { get; set; }
        public double? Lat { get; set; }
        public double? Lng { get; set; }
        public int? BaselineTemp { get; set; }
        public string? EnvironmentType { get; set; }
        public bool? IsActive { get; set; }
    }

    public class AdminUser {
    public int Id { get; set; }
    public string PersonnelId { get; set; }
    public string Hash { get; set; }
    public string FullName { get; set; }
}

public class LoginRequest {
    public string PersonnelId { get; set; }
    public string Passcode { get; set; }
}