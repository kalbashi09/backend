using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HeatAlert
{
    public class AlertResult
    {
        public string BarangayName { get; set; } = string.Empty;
        public string RelativeLocation { get; set; } = string.Empty;
        public double Lat { get; set; }
        public double Lng { get; set; }
        public int HeatIndex { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class HeatSimulator
    {
        private readonly JArray? _features;

        // CONSTRUCTOR: This runs only ONCE when you do 'new HeatSimulator(cachedJson)'
        public HeatSimulator(string jsonContent)
        {
            try
            {
                if (string.IsNullOrEmpty(jsonContent)) return;
                
                JObject data = JObject.Parse(jsonContent);
                _features = data["features"] as JArray;
                Console.WriteLine("✅ HeatSimulator: Map features loaded into memory.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SIM-INIT-ERROR] Failed to parse GeoJSON: {ex.Message}");
            }
        }

        public string GetDangerLevel(int heatIndex)
        {
            if (heatIndex >= 49) return "🚨 EXTREME DANGER";
            if (heatIndex >= 42) return "🔥 DANGER";
            if (heatIndex >= 39) return "⚠️ EXTREME CAUTION"; 
            if (heatIndex >= 29) return "✅ NORMAL";           
            return "❄️ COOL";
        }

        public string IdentifyBarangay(double lat, double lng) 
        {
            if (_features == null) return "Map Data Uninitialized";

            string closestBarangay = "Talisay City";
            double minDistance = double.MaxValue;

            foreach (var feature in _features)
            {
                string name = feature["properties"]?["NAME_3"]?.ToString() ?? "Unknown";
                
                // GeoJSON coordinates can be inside "geometry"
                var geometry = feature["geometry"];
                var type = geometry?["type"]?.ToString();
                
                // Get the correct coordinate array based on Polygon or MultiPolygon
                JToken? coords = type == "MultiPolygon" 
                    ? geometry?["coordinates"]?[0]?[0] 
                    : geometry?["coordinates"]?[0];

                if (coords != null && coords.HasValues)
                {
                    // 1. Ray Casting Check (Inside the boundary?)
                    if (IsPointInPolygon(lat, lng, coords)) return name;

                    // 2. Fallback distance check
                    var firstPoint = coords[0];
                    if (firstPoint != null && firstPoint.Count() >= 2)
                    {
                        double firstLng = (double)firstPoint[0]!;
                        double firstLat = (double)firstPoint[1]!;
                        double dist = Math.Sqrt(Math.Pow(lat - firstLat, 2) + Math.Pow(lng - firstLng, 2));

                        if (dist < minDistance)
                        {
                            minDistance = dist;
                            closestBarangay = name;
                        }
                    }
                }
            }
            return (minDistance < 0.005) ? closestBarangay : "Talisay (Outside)";
        }

        private bool IsPointInPolygon(double lat, double lng, JToken polygon)
        {
            bool isInside = false;
            var points = polygon.Children().ToList(); 
            int j = points.Count - 1;

            for (int i = 0; i < points.Count; i++)
            {
                double piLng = (double)points[i][0]!;
                double piLat = (double)points[i][1]!;
                double pjLng = (double)points[j][0]!;
                double pjLat = (double)points[j][1]!;

                if ((((piLat <= lat) && (lat < pjLat)) || ((pjLat <= lat) && (lat < piLat))) &&
                    (lng < (pjLng - piLng) * (lat - piLat) / (pjLat - piLat) + piLng))
                {
                    isInside = !isInside;
                }
                j = i;
            }
            return isInside;
        }

        // Removed jsonPath from parameters because the data is already in memory!
        public AlertResult CreateManualAlert(double lat, double lng, int heatIndex)
        {
            return new AlertResult
            {
                BarangayName = IdentifyBarangay(lat, lng),
                Lat = lat,
                Lng = lng,
                HeatIndex = heatIndex,
                CreatedAt = DateTime.Now,
                RelativeLocation = "Live Mobile Sensor"
            };
        }
    }
}