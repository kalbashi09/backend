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
                Console.WriteLine($"--- [AUTH ATTEMPT]: ID: {request.PersonnelId} ---"); // DEBUG
                
                var admin = await db.GetAdminByPersonnelId(request.PersonnelId);

                if (admin == null) 
                {
                    Console.WriteLine("--- [AUTH ERROR]: Personnel ID not found in DB ---"); // DEBUG
                    return Results.Json(new { message = "Invalid ID" }, statusCode: 401);
                }

                bool isValid = BCrypt.Net.BCrypt.Verify(request.Passcode, admin.Hash);
                Console.WriteLine($"--- [AUTH RESULT]: Password Match = {isValid} ---"); // DEBUG

                if (!isValid) return Results.Json(new { message = "Incorrect Passcode" }, statusCode: 401);

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
                if (IsNotAuthorized(context)) return Results.Unauthorized();
                try {
                    var history = await db.GetHistory(limit ?? 100);
                    if (!history.Any()) return Results.NotFound("No heat logs found.");

                    var friendlyHistory = history.Select(h => {
                        // Adjust for Philippine Time (UTC+8)
                        DateTime phTime = h.CreatedAt.AddHours(8);

                        return new {
                            h.SensorCode,
                            h.DisplayName,
                            h.BarangayName,
                            h.HeatIndex,
                            h.Lat,
                            h.Lng,
                            Date = phTime.ToString("MMM dd, yyyy"),
                            Time = phTime.ToString("hh:mm tt"), 
                            // NEW: Provide the machine-readable timestamp
                            RawTime = phTime 
                        };
                    });
                    return Results.Ok(friendlyHistory);
                }
                catch (Exception ex) { return Results.Problem($"Database Error: {ex.Message}"); }
            });

            app.MapPatch("/api/sensors/{id}", async (int id, SensorUpdateDto dto, DatabaseManager db) => 
            {
                var existing = await db.GetSensorById(id);
                if (existing == null) return Results.NotFound($"Sensor {id} not found.");

                await db.UpdateSensorFlexible(id, dto);
                return Results.Ok(new { message = "Sensor updated successfully." });
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

            // 5. POST: Register Sensor
            app.MapPost("/api/register-sensor", async (HttpContext context, SensorNode newSensor, DatabaseManager db) => {
                if (IsNotAuthorized(context)) return Results.Unauthorized();
                try {
                    await db.CreateSensor(newSensor); 
                    return Results.Ok(new { message = $"Sensor {newSensor.SensorCode} registered!" });
                }
                catch (Exception ex) { return Results.Problem($"Registration Error: {ex.Message}"); }
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
            var delta = DateTime.UtcNow.AddHours(8) - time;
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