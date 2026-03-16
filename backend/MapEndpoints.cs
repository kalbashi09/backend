using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace HeatAlert
{
    public static class MapEndpoints
    {
        public static void RegisterAlertEndpoints(this IEndpointRouteBuilder app, DatabaseManager db)
        {
            // --- THE BOUNCER HELPER ---
            bool IsNotAuthorized(HttpContext context)
            {
                var config = context.RequestServices.GetRequiredService<IConfiguration>();
                var secretKey = config["ApiSettings:ApiKey"];
                
                if (!context.Request.Headers.TryGetValue("X-API-KEY", out var extractedKey) || 
                    extractedKey != secretKey)
                {
                    return true; 
                }
                return false; 
            }

            // 1. GET: Fetch current data (Public)
            app.MapGet("/api/current-alert", () => {
                var alert = GlobalData.LatestAlert;
                if (alert == null) return Results.NotFound("No data yet.");

                if (alert.HeatIndex >= 29 && alert.HeatIndex <= 38)
                {
                    return Results.Ok(new { 
                        Status = "Stable", 
                        Message = "Normal range.",
                        LastReading = alert.HeatIndex,
                        Barangay = alert.BarangayName
                    });
                }
                return Results.Ok(alert);
            });

            // 2. GET: Heat History (SECURED)
            app.MapGet("/api/live-heat-history", async (HttpContext context, DatabaseManager db, int? limit) => 
            {
                if (IsNotAuthorized(context)) return Results.Unauthorized();

                try {
                    var history = await db.GetHistory(limit ?? 100);
                    if (!history.Any()) return Results.NotFound("Database is empty.");

                   var friendlyHistory = history.Select(h => {
                    // 1. Get the Manila Time Zone
                    var phZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
                    
                    // 2. Convert the DB time (which C# thinks is UTC) to PH Time
                    DateTime phTime = TimeZoneInfo.ConvertTimeFromUtc(h.CreatedAt, phZone);

                    return new {
                        h.BarangayName,
                        h.HeatIndex,
                        // ... other fields
                        Date = phTime.ToString("MMM dd, yyyy"),
                        Time = phTime.ToString("hh:mm tt"), // This will now show 12:49 PM
                        RawTimestamp = phTime
                    };
                });

                    return Results.Ok(friendlyHistory);
                }
                catch (Exception ex) {
                    return Results.Problem($"Database Error: {ex.Message}");
                }
            });

            // 3. POST: Log Heat (SECURED)
            app.MapPost("/api/log-heat", async (HttpContext context, AlertResult data, DatabaseManager db, BotAlertSender bot) => 
            {
                if (IsNotAuthorized(context)) return Results.Unauthorized();

                try {
                    await db.SaveHeatLog(data);
                    await bot.ProcessAndBroadcastAlert(data);
                    return Results.Ok(new { message = "Authorized: Log saved!", data });
                }
                catch (Exception ex) {
                    return Results.Problem($"API Error: {ex.Message}");
                }
            });

            // 4. POST: Subscribe (SECURED)
            app.MapPost("/api/subscribe", async (HttpContext context, SubscriberRequest request, DatabaseManager db) => 
            {
                if (IsNotAuthorized(context)) return Results.Unauthorized();

                if (request.ChatId == 0 || string.IsNullOrEmpty(request.Username))
                    return Results.BadRequest("Invalid subscriber data.");

                try {
                    await db.SaveSubscriber(request.ChatId, request.Username);
                    return Results.Ok(new { message = "Successfully subscribed via API!" });
                }
                catch (Exception ex) {
                    return Results.Problem($"Database Error: {ex.Message}");
                }
            });
        }
    }

    public record SubscriberRequest(long ChatId, string Username);
}