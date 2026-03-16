using Microsoft.Extensions.DependencyInjection;
using HeatAlert;
using Newtonsoft.Json.Linq;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// This looks for a Render Environment Variable first
// "DATABASE_URL" is the name of the key you will create in Render's dashboard
// This will automatically find "BotSettings__TelegramToken" 
// and map it to the "BotSettings" section
// 1. This checks "ConnectionStrings:DefaultConnection" in appsettings.json
// OR "ConnectionStrings__DefaultConnection" in Render Environment
// Updated Code: It checks "DefaultConnection" first, then tries "DATABASE_URL"
string? rawConn = builder.Configuration.GetConnectionString("DefaultConnection") 
                 ?? Environment.GetEnvironmentVariable("DATABASE_URL");

// 2. Safety check: If it's null, the app should tell you why before crashing
if (string.IsNullOrEmpty(rawConn)) 
{
    Console.WriteLine("❌ ERROR: Connection string is empty! Check Render Environment Variables.");
    return; 
}

// 3. Convert if it's a URL (Render), or use as-is (Local)
string connString = (rawConn.StartsWith("postgres://") || rawConn.StartsWith("postgresql://"))
    ? ConvertPostgresUrlToConnString(rawConn)
    : rawConn;

// 4. Same for the Bot Token
string botToken = builder.Configuration["BotSettings:TelegramToken"]!;

// 1. ROBUST PATH CHECKING
string baseDir = AppDomain.CurrentDomain.BaseDirectory;
// We try the most likely path first (3 levels up from bin/Debug/netX.0)
// Simplest way: Look in the current folder, then look in the subfolder
string jsonPath = Path.Combine(baseDir, "sharedresource", "talisaycitycebu.json");

// If it's not in a subfolder (sometimes Docker flattens files), look in root
if (!File.Exists(jsonPath)) {
    jsonPath = Path.Combine(baseDir, "talisaycitycebu.json");
}

string cachedJson = "";
try {
    cachedJson = File.ReadAllText(jsonPath);
    Console.WriteLine($"✅ GeoJSON loaded from: {jsonPath}");
} catch (Exception ex) {
    // If this hits, the simulation will definitely fail.
    Console.WriteLine($"❌ CRITICAL PATH ERROR: {ex.Message}");
}

builder.Services.AddCors(options => {
    options.AddPolicy("AllowAll",
        policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var db = new DatabaseManager(connString);
var bot = new BotAlertSender(botToken, db, cachedJson);

builder.Services.AddSingleton(db);
builder.Services.AddSingleton(bot);

builder.WebHost.ConfigureKestrel(options => {
    var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
    options.ListenAnyIP(int.Parse(port));
});

var app = builder.Build();

app.UseCors("AllowAll");
app.UseRouting();

app.RegisterAlertEndpoints(db);

// 2. START SIMULATION
_ = Task.Run(async () => {
    bot.StartBot();
    
    // Pass the data to the simulator once
    var simulator = new HeatSimulator(cachedJson); 
    var rng = new Random();

    using var client = new HttpClient();

    while (true)
    {

        // Inside your while(true) loop in Program.cs
        try {
            // Now we reuse the same client - Much more efficient!
            await client.GetAsync("https://backend-9lv5.onrender.com/"); 
        } catch { /* Ignore if it fails */ }

        try {
            int simTemp = rng.Next(25, 52); 

            // Pass the cached string
            var randomPoint = GetRandomTalisayPoint(cachedJson);
            
            // This method in HeatSimulator.cs must NOT take a 'path' anymore
            var result = simulator.CreateManualAlert(randomPoint.lat, randomPoint.lng, simTemp); 
            
            GlobalData.LatestAlert = result; 
            
            Console.WriteLine($"[LOG] {result.BarangayName} | {result.HeatIndex}°C | {DateTime.Now:hh:mm:ss tt}");

            if (result.HeatIndex >= 39 || result.HeatIndex < 29)
            {
                await bot.ProcessAndBroadcastAlert(result);
            }
        }
        catch (Exception ex) { Console.WriteLine($"Simulation Loop Error: {ex.Message}"); }
        
        await Task.Delay(30000); 
    }
});

// --- POSTGRES CONNECTION VERIFICATION ---
try 
{
    using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    Console.WriteLine("🚀 SUCCESS: Connected to PostgreSQL (HeatIndicator)!");
} 
catch (Exception ex) 
{
    Console.WriteLine($"❌ FATAL: Could not connect to Postgres! Error: {ex.Message}");
    // Optionally return or stop here if the DB is mandatory
}

// Allow both GET (browser) and HEAD (Render/Postman)
app.MapMethods("/", new[] { "GET", "HEAD" }, () => "HEALERTSYS API is Live and Running!");
app.Run();

// --- HELPERS ---
(double lat, double lng) GetRandomTalisayPoint(string jsonContent) {
    if (string.IsNullOrEmpty(jsonContent)) return (10.2447, 123.8480);
    
    try {
        var data = JObject.Parse(jsonContent);
        var features = (JArray)data["features"]!;
        var rng = new Random(); 
        
        // 1. Pick a random Barangay
        var randomBarangay = features[rng.Next(features.Count)];
        var geometry = randomBarangay["geometry"];
        string type = geometry?["type"]?.ToString() ?? "";

        // 2. Get the list of all corners (vertices)
        JToken? allCoords = type switch {
            "Polygon" => geometry?["coordinates"]?[0],
            "MultiPolygon" => geometry?["coordinates"]?[0]?[0],
            _ => null
        };

        if (allCoords == null || !allCoords.HasValues) return (10.2447, 123.8480);

        // --- NEW INNOVATION: BOUNDING BOX CENTER + SAFE JITTER ---
        var pointsList = allCoords.Children().ToList();

        // Find the "Box" that contains the whole Barangay
        double minLng = pointsList.Min(p => (double)p[0]);
        double maxLng = pointsList.Max(p => (double)p[0]);
        double minLat = pointsList.Min(p => (double)p[1]);
        double maxLat = pointsList.Max(p => (double)p[1]);

        // Find the center of that box
        double centerLat = (minLat + maxLat) / 2;
        double centerLng = (minLng + maxLng) / 2;

        // Add a 20% "Safe Jitter" so points aren't identical but stay near the center
        // This keeps markers away from the jagged borders/Cebu City lines
        double latRange = (maxLat - minLat) * 0.2;
        double lngRange = (maxLng - minLng) * 0.2;

        double finalLat = centerLat + ((rng.NextDouble() - 0.5) * latRange);
        double finalLng = centerLng + ((rng.NextDouble() - 0.5) * lngRange);

        return (finalLat, finalLng); 
    } catch {
        // Fallback to City Hall
        return (10.2447, 123.8480);
    }
}


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
           $"Trust Server Certificate=true;" +
           $"TimeZone=Asia/Manila;"; // <--- The Database Fix
}

public static class GlobalData {
    public static AlertResult? LatestAlert { get; set; }
}