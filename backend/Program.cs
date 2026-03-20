using Microsoft.Extensions.DependencyInjection;
using HeatAlert;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

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
    options.AddPolicy("AllowAll", policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var db = new DatabaseManager(connString);
var bot = new BotAlertSender(botToken, db);

builder.Services.AddSingleton(db);
builder.Services.AddSingleton(bot);

builder.WebHost.ConfigureKestrel(options => 
{
    // Change "8080" to "5000"
    var port = Environment.GetEnvironmentVariable("PORT") ?? "5000"; 
    options.ListenAnyIP(int.Parse(port));
});

var app = builder.Build();

// 3. DATABASE VERIFICATION (Run this BEFORE app.Run)
try {
    using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    Console.WriteLine("🚀 SUCCESS: Connected to PostgreSQL!");
} catch (Exception ex) {
    Console.WriteLine($"❌ FATAL DB ERROR: {ex.Message}");
}

app.UseCors("AllowAll");
app.UseRouting();
app.RegisterAlertEndpoints(db);
app.RegisterAuthEndpoints();

// 4. THE V3 SIMULATION ENGINE
// 4. THE V3 SIMULATION ENGINE
_ = Task.Run(async () => {
    bot.StartBot();
    
    var simulator = new HeatSimulator(); 

    // Temporary line to run once to see a valid hash in your console:
    Console.WriteLine($"New Hash for 123456: {BCrypt.Net.BCrypt.HashPassword("123456")}");

    while (true)
    {
        try 
        {
            var sensors = await db.GetAllSensors(); 
            var currentBatch = new List<AlertResult>(); // 1. Create a batch list

            foreach (var sensor in sensors)
            {

                // Fail-safe: Skip if somehow an inactive sensor got into the list
                if (!sensor.IsActive) 
                {
                    Console.WriteLine($"❄️ [FREEZE] {sensor.DisplayName} is inactive. Skipping...");
                    continue; 
                }

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

                // Update the single most recent alert for the dashboard
                GlobalData.LatestAlert = result; 

                // 2. Save EVERYTHING to DB for history/graphs
                await db.SaveHeatLog(result, sensor.Id); 

                // 3. Add to our batch for the Telegram summary
                currentBatch.Add(result);
                
                Console.WriteLine($"[V3 LOG] {sensor.DisplayName}: {simTemp}°C");
            }

            // 4. Send ONE heartbeat for all "Alarming" spots (Sorted High to Low)
            // This replaces the 'if' statement inside the loop
            await bot.BroadcastHeartbeatSummary(currentBatch);
        }
        catch (Exception ex) { Console.WriteLine($"Simulation Loop Error: {ex.Message}"); }
        
        // Wait 30 seconds before the next full city scan
        await Task.Delay(30000); 
    }
});

app.MapMethods("/", new[] { "GET", "HEAD" }, () => "HEALERTSYS V3 API is Live."); 

app.Run();


string ConvertPostgresUrlToConnString(string url)
{
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':');
    
    int port = uri.Port <= 0 ? 5432 : uri.Port; 

    // REMOVED: Integrated Security
    // ADDED: No Kerberos and basic SSL settings
    return $"Host={uri.Host};" +
           $"Port={port};" + 
           $"Username={userInfo[0]};" +
           $"Password={userInfo[1]};" +
           $"Database={uri.AbsolutePath.Trim('/')};" +
           $"SslMode=Require;" +
           $"Trust Server Certificate=true;";
}

public static class GlobalData {
    public static AlertResult? LatestAlert { get; set; }

    public static DateTime GetPHTime() 
    {
        // PostgreSQL prefers Unspecified or UTC kind to avoid auto-conversions
        return DateTime.UtcNow; 
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