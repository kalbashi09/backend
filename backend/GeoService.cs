using System.Text.Json;

namespace HeatAlert
{
    public static class GeoService
    {
        private static List<GeoJsonFeature> _barangayFeatures = new();

        static GeoService()
        {
            // Load once when the application starts
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sharedresource", "talisaycitycebu.json");
            if (File.Exists(jsonPath))
            {
                string json = File.ReadAllText(jsonPath);
                var collection = JsonSerializer.Deserialize<FeatureCollection>(json);
                _barangayFeatures = collection?.features ?? new List<GeoJsonFeature>();
                Console.WriteLine($"🌍 [GeoService]: Loaded {_barangayFeatures.Count} barangays.");
            }
        }

        public static string GetBarangay(double lat, double lng)
        {
            foreach (var feature in _barangayFeatures)
            {
                if (feature.geometry?.type == "Polygon" && feature.geometry.coordinates?.Length > 0)
                {
                    var polygon = feature.geometry.coordinates[0];
                    if (IsPointInPolygon(lng, lat, polygon))
                    {
                        return feature.properties?.NAME_3 ?? "Unknown Barangay";
                    }
                }
            }
            return "Outside of Talisay City";
        }

        private static bool IsPointInPolygon(double x, double y, double[][] polygon)
        {
            int n = polygon.Length;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = polygon[i][0], yi = polygon[i][1];
                double xj = polygon[j][0], yj = polygon[j][1];
                if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi))
                {
                    inside = !inside;
                }
            }
            return inside;
        }
    }
}