using System;

namespace HeatAlert
{
    // The bridge between your Database and your Logic
    public class AlertResult
    {

        public string SensorCode { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string BarangayName { get; set; } = string.Empty;
        public string RelativeLocation { get; set; } = string.Empty;
        public double Lat { get; set; }
        public double Lng { get; set; }
        public int HeatIndex { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class HeatSimulator
    {
        private readonly Random _rng = new();

        // V3: No more GeoJSON, no more long constructors!
        public HeatSimulator() { }

       public int GenerateReading(int baselineTemp)
        {
            // 1. Roll for frequency (1-100)
            int roll = _rng.Next(1, 101);

            int finalTemp;

            // 70% Chance: Stay near the "Normal" range (28 - 39)
            if (roll <= 70)
            {
                // Logic: Stay within -3 to +5 of the baseline, but clamp it between 28 and 39
                int normalValue = _rng.Next(baselineTemp - 3, baselineTemp + 6);
                finalTemp = Math.Clamp(normalValue, 28, 39);
            }
            else
            {
                // 30% Chance: The "Extreme" range (20 - 90)
                // Logic: Still use the baseline as an anchor, but with a much wider swing
                int extremeValue = _rng.Next(baselineTemp - 15, baselineTemp + 55);
                finalTemp = Math.Clamp(extremeValue, 20, 90);
            }

            return finalTemp;
        }

        // The Bot calls this to decide how to label the Telegram message
        public string GetDangerLevel(int heatIndex)
        {
            if (heatIndex >= 49) return "🚨 EXTREME DANGER";
            if (heatIndex >= 42) return "🔥 DANGER";
            if (heatIndex >= 39) return "⚠️ EXTREME CAUTION";
            if (heatIndex >= 30) return "✅ NORMAL";
            return "❓ UNUSUAL (Possible Sensor Error or Cold Anomaly)";
        }
    }
}