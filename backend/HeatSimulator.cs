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
            int roll = _rng.Next(1, 101);
            int finalTemp;

            // 70% Chance: Standard day-to-day fluctuations
            if (roll <= 70)
            {
                // Now allows for "Normal" and "Caution" ranges (25°C to 41°C)
                int normalValue = _rng.Next(baselineTemp - 5, baselineTemp + 8);
                finalTemp = Math.Clamp(normalValue, 25, 41);
            }
            else
            {
                // 30% Chance: The "Extreme" swings (Cool anomalies or Heatwaves)
                // Logic: Wide swing from 15°C to 65°C
                int extremeValue = _rng.Next(baselineTemp - 20, baselineTemp + 40);
                finalTemp = Math.Clamp(extremeValue, 15, 89);
            }

            return finalTemp;
        }

        // UPDATED: Now perfectly matches your 5 Frontend/Map states
        public string GetDangerLevel(int heatIndex)
        {
            if (heatIndex >= 49) return "🚨 EXTREME DANGER"; // RED
            if (heatIndex >= 42) return "🔥 DANGER";         // BRAND ORANGE
            if (heatIndex >= 38) return "⚠️ CAUTION";        // AMBER
            if (heatIndex >= 26) return "✅ NORMAL";         // EMERALD
            
            // Anything below 26 is the Blue "Cool" state
            return "❄️ COOL (Below Baseline)";               // BLUE
        }
    }
}