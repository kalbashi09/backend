using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Collections.Generic;

namespace HeatAlert
{
    public static class MapEndpoints
    {
        // OLD (Causing the error)

        public static void RegisterAuthEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/api/auth/login", async (LoginRequest request, DatabaseManager db) =>
            {
                var admin = await db.GetAdminByPersonnelId(request.PersonnelId);

                // Even if admin is null, we verify against a 'fake' hash 
                // to ensure the CPU work is roughly the same.
                string hashToVerify = admin?.Hash ?? "$2a$11$SimulatedHashForTimingProtection";
                bool isValid = BCrypt.Net.BCrypt.Verify(request.Passcode, hashToVerify);

                // Now check both conditions at once
                if (admin == null || !isValid) 
                {
                    return Results.Json(new { message = "Invalid Credential" }, statusCode: 401);
                }

                await db.UpdateAdminLoginTime(admin.Id);
                return Results.Ok(new { message = "Success", user = admin.FullName });
            });
        }

        public static void RegisterAlertEndpoints(this IEndpointRouteBuilder app, DatabaseManager db)
        {
            // 1. GET: Fetch current data (Public)
            app.MapGet("/api/current-alert", () => {
                var alert = GlobalData.LatestAlert;
                if (alert == null) return Results.NotFound("No data yet.");
                return Results.Ok(alert);
            });

            // 2. GET: Sensors List
            app.MapGet("/api/sensors", async (DatabaseManager db, bool includeInactive = false) => {
                try {
                    // Now we pass the 'includeInactive' from the URL into the DB method
                    var sensors = await db.GetAllSensors(includeInactive); 
                    return Results.Ok(sensors);
                }
                catch (Exception ex) { return Results.Problem(ex.Message); }
            });

            // 3. GET: Heat History (SECURED)
            app.MapGet("/api/live-heat-history", async (HttpContext context, DatabaseManager db, int? limit) => {
                try {
                    var history = await db.GetHistory(limit ?? 300);
                    if (!history.Any()) return Results.NotFound("No heat logs found.");

                    var friendlyHistory = history.Select(h => {
                    // Let the RawTime remain exactly what is in the DB (UTC)
                    // We will let the JavaScript frontend handle the +8 display.
                    return new {
                        h.SensorCode,
                        h.DisplayName,
                        h.BarangayName,
                        h.HeatIndex,
                        h.Lat,
                        h.Lng,
                        Date = h.CreatedAt.ToString("MMM dd, yyyy"),
                        Time = h.CreatedAt.ToString("hh:mm tt"), 
                        RawTime = h.CreatedAt 
                    };
                });
                    return Results.Ok(friendlyHistory);
                }
                catch (Exception ex) { return Results.Problem($"Database Error: {ex.Message}"); }
            });

            // 3. PATCH: Update Sensor
            app.MapPatch("/api/sensors/{id}", async (int id, SensorUpdateDto dto, DatabaseManager db) => 
            {
                try 
                {
                    // 1. The "Space" Check (Validation)
                    // Use .GetValueOrDefault() to treat null as 0, or check .HasValue
                    if (dto.Lat.HasValue && Math.Abs(dto.Lat.Value) > 90) 
                    {
                        return Results.Json(new { message = "❌ Latitude is outside Earth's boundaries!" }, statusCode: 400);
                    }

                    if (dto.Lng.HasValue && Math.Abs(dto.Lng.Value) > 180) 
                    {
                        return Results.Json(new { message = "❌ Longitude is outside Earth's boundaries!" }, statusCode: 400);
                    }

                    if (dto.Lat.HasValue && dto.Lng.HasValue)
                    {
                        // Only overwrite if they didn't manually provide a new specific barangay name
                        if (string.IsNullOrWhiteSpace(dto.Barangay))
                        {
                            dto.Barangay = GeoService.GetBarangay(dto.Lat.Value, dto.Lng.Value);
                        }
                    }

                    var existing = await db.GetSensorById(id);
                    if (existing == null) 
                        return Results.Json(new { message = $"❌ Sensor {id} not found." }, statusCode: 404);

                    await db.UpdateSensorFlexible(id, dto);
                    return Results.Ok(new { message = $"✅ {dto.SensorCode} updated successfully." });
                }
                catch (Exception ex) when (ex.Message.Contains("23505") || ex.Message.Contains("DUPLICATE"))
                {
                    // Catches PostgreSQL unique constraint (23505) or custom "DUPLICATE_CODE"
                    return Results.Json(new { message = $"❌ Conflict: The code \"{dto.SensorCode}\" is already assigned to another sensor." }, statusCode: 409);
                }
                catch (Exception ex) 
                { 
                    // Tell it like it is for other errors
                    return Results.Json(new { message = $"❌ Server Error: {ex.Message}" }, statusCode: 500); 
                }
            });

            // 5. POST: Register Sensor
            app.MapPost("/api/register-sensor", async (SensorNode newSensor, DatabaseManager db) => 
            {
                try 
                {
                    if (Math.Abs(newSensor.Lat) > 90 || Math.Abs(newSensor.Lng) > 180)
                    {
                        return Results.Json(new { message = "❌ Error: Coordinates are outside Earth's limits." }, statusCode: 400);
                    }
                    if (string.IsNullOrWhiteSpace(newSensor.Barangay) || newSensor.Barangay == "string")
                    {
                        newSensor.Barangay = GeoService.GetBarangay(newSensor.Lat, newSensor.Lng);
                        Console.WriteLine($"--- [Auto-Geo]: Assigned {newSensor.Barangay} to {newSensor.SensorCode} ---");
                    }

                    newSensor.IsActive = true; 
                    await db.CreateSensor(newSensor); 
                    return Results.Ok(new { message = $"✅ {newSensor.SensorCode} registered in {newSensor.Barangay}!" });

                    // 🔥 FORCE the sensor to be active so it shows up in your lists
                    newSensor.IsActive = true; 

                    await db.CreateSensor(newSensor); 
                    return Results.Ok(new { message = $"✅ Sensor {newSensor.SensorCode} registered and ACTIVE!" });
                }
                catch (Exception ex) when (ex.Message.Contains("23505") || ex.Message.Contains("Conflict"))
                {
                    return Results.Json(new { message = $"❌ Conflict: Code \"{newSensor.SensorCode}\" already exists." }, statusCode: 409);
                }
                catch (Exception ex) 
                { 
                    return Results.Json(new { message = $"❌ Registration Error: {ex.Message}" }, statusCode: 500); 
                }
            });

            // 4. POST: Log Heat
            app.MapPost("/api/log-heat", async (HttpContext context, SensorReportRequest request, DatabaseManager db, BotAlertSender bot) => {
                if (IsNotAuthorized(context)) return Results.Unauthorized();
                try {
                    var sensor = await db.GetSensorById(request.SensorId);
                    if (sensor == null) return Results.BadRequest("Sensor not found.");

                    var data = new AlertResult {
                        SensorCode = sensor.SensorCode, // V3 Logic
                        DisplayName = sensor.DisplayName,
                        BarangayName = sensor.Barangay,
                        Lat = sensor.Lat,
                        Lng = sensor.Lng,
                        HeatIndex = request.Temperature,
                        CreatedAt = DateTime.UtcNow // Store as UTC
                    };

                    await bot.ProcessAndBroadcastAlert(data, sensor.Id);
                    return Results.Ok(new { message = "Report processed!", sensor = sensor.DisplayName });
                }
                catch (Exception ex) { return Results.Problem($"API Error: {ex.Message}"); }
            });


            // 6. DELETE: Permanent removal of a sensor and its logs
            app.MapDelete("/api/sensors/{id}", async (int id, DatabaseManager db) => 
            {
                try 
                {
                    // First, check if the sensor even exists
                    var existing = await db.GetSensorById(id);
                    if (existing == null) 
                    {
                        return Results.NotFound(new { message = $"Sensor with ID {id} does not exist." });
                    }

                    // Execute the "Nuclear" delete we wrote earlier
                    bool success = await db.DeleteSensorOnly(id);

                    if (success) 
                    {
                        Console.WriteLine($"--- [DB]: Sensor {id} ({existing.DisplayName}) has been purged from the system. ---");
                        return Results.Ok(new { message = $"Sensor {id} and its history were deleted." });
                    }

                    return Results.Problem("Failed to delete sensor. Check database logs.");
                }
                catch (Exception ex) 
                { 
                    return Results.Problem($"API Delete Error: {ex.Message}"); 
                }
            });
            
        }

        // --- HELPERS (Moved outside the method, inside the class) ---

        private static bool IsNotAuthorized(HttpContext context)
        {
            var config = context.RequestServices.GetRequiredService<IConfiguration>();
            var secretKey = config["ApiSettings:ApiKey"];
            if (!context.Request.Headers.TryGetValue("X-API-KEY", out var extractedKey) || extractedKey != secretKey)
            {
                return true; 
            }
            return false; 
        }

        private static string GetRelativeTime(DateTime time)
        {
            // ❌ Change DateTime.UtcNow.AddHours(8) to just DateTime.UtcNow
            var delta = DateTime.UtcNow - time; 
            if (delta.TotalMinutes < 1) return "Just now";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
            if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
            return time.ToString("MMM dd");
        }
    }

    public record SensorReportRequest(int SensorId, int Temperature);

    public record LoginRequest(string PersonnelId, string Passcode);
    public record SubscriberRequest(long ChatId, string Username);
}