using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace HeatAlert
{
    public class BotAlertSender
    {
        HeatSimulator _simulator = new();
        private readonly TelegramBotClient _botClient;
        private readonly DatabaseManager _db;

        private static readonly Dictionary<long, string> _pendingSimulations = new();

        // Removed: _mapData string
        public BotAlertSender(string token, DatabaseManager db)
        {
        _botClient = new TelegramBotClient(token);
        _db = db;
        }

        public void StartBot()
        {
            var receiverOptions = new ReceiverOptions { AllowedUpdates = { } };
            _botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions);
            Console.WriteLine("🤖 Bot is now listening for subscribers...");
        }

        public async Task ProcessAndBroadcastAlert(AlertResult result, int sensorId)
        {
            // 1. Update the live map for the dashboard
            GlobalData.LatestAlert = result;

            // 2. Save to Database using the new V3 method with sensorId
            await _db.SaveHeatLog(result, sensorId); 

            // 3. Prepare the Telegram message
            string level = _simulator.GetDangerLevel(result.HeatIndex);
            
            // V3 Innovation: Use the 'RelativeLocation' (Sensor Name) for better alerts
            string message = $"🌡️ *HEAT ALERT: {level}*\n\n" +
                            $"📍 Location: {result.RelativeLocation} ({result.BarangayName})\n" +
                            $"🔥 Heat Index: {result.HeatIndex}°C\n" +
                            $"⏰ Time: {result.CreatedAt:hh:mm tt}";

            // 4. Get all subscribers and send
            var subscribers = await _db.GetAllSubscriberIds();
            await BroadcastAlert(message, subscribers);
        }

        public async Task BroadcastHeartbeatSummary(List<AlertResult> allReadings)
        {
            // 1. Filter: Only include "Alarming" temps (39°C and above based on your reference)
            // 2. Sort: Highest Heat Index first
            var alarmingSpots = allReadings
                .Where(r => r.HeatIndex >= 39) 
                .OrderByDescending(r => r.HeatIndex)
                .ToList();

            // If everything is Normal (30-38), the bot stays silent.
            if (!alarmingSpots.Any()) return; 

            // 3. Build the "Heartbeat" Message
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🌡️ ***HEATSYNC: HIGH HEAT REPORT***");
            sb.AppendLine($"⏰ *Scanned at: {GlobalData.GetPHTime():hh:mm tt}*");
            sb.AppendLine("-----------------------------------");

            // Get the top hotspot for the header
            var topSpot = alarmingSpots.First();
            sb.AppendLine($"🔝 **HIGHEST:** {topSpot.HeatIndex}°C in {topSpot.BarangayName}");
            sb.AppendLine();

            foreach (var spot in alarmingSpots)
            {
                // Choose emoji based on your GetDangerLevel logic
                string emoji = spot.HeatIndex >= 49 ? "🔴" : 
                            spot.HeatIndex >= 42 ? "🟠" :spot.HeatIndex >= 38 ? "🟡" : "🔵";
                
                string level = _simulator.GetDangerLevel(spot.HeatIndex);

                sb.AppendLine($"{emoji} *{spot.HeatIndex}°C* - {level}");
                sb.AppendLine($"📍 {spot.DisplayName} ({spot.BarangayName})");
                sb.AppendLine();
            }

            sb.AppendLine("📍 *Stay hydrated! Check the live map for more info.*");

            // 4. Send the single aggregated message
            var subscribers = await _db.GetAllSubscriberIds();
            await BroadcastAlert(sb.ToString(), subscribers);
        }

        private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message?.Location != null)
            {
                await ProcessManualSensorPing(bot, update.Message, ct);
                return;
            }

            if (update.Message is not { Text: not null } message) return;

            long chatId = message.Chat.Id;
            string username = message.From?.Username ?? "UnknownUser";
            string text = message.Text.ToLower();

            string[] simCommands = { "/exdanger", "/danger", "/caution", "/normal", "/cool" };
            if (simCommands.Contains(text))
            {
                _pendingSimulations[chatId] = text;
                var keyboard = new ReplyKeyboardMarkup(new[] {
                    new KeyboardButton("📡 Confirm Sensor Location") { RequestLocation = true }
                }) { ResizeKeyboard = true, OneTimeKeyboard = true };

                await bot.SendMessage(chatId, $"🛠️ **Simulation: {text.ToUpper()}**\nTap the button below to send GPS.", 
                    replyMarkup: keyboard, cancellationToken: ct);
                return;
            }

            // Inside HandleUpdateAsync...

            if (text == "/subscribeservice" || text == "/start")
            {
                await _db.SaveSubscriber(chatId, username);
                await bot.SendMessage(chatId, "✅ **Subscription Active!**\nYou will receive real-time heat alerts for Talisay City.", 
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
            }
            else if (text == "/unsubscribeservice")
            {
                await _db.RemoveSubscriber(chatId);
                await bot.SendMessage(chatId, "🔕 **Alerts Muted.**\nYou will no longer receive heat notifications. Send `/subscribeservice` anytime to re-enable them.", 
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
            }
        }

        private async Task ProcessManualSensorPing(ITelegramBotClient bot, Message message, CancellationToken ct)
        {
            long chatId = message.Chat.Id;
            if (!_pendingSimulations.TryGetValue(chatId, out var command)) command = "/danger";

            double currentLat = message.Location!.Latitude;
            double currentLng = message.Location.Longitude;

            // --- INNOVATIVE STEP: Update the Registry first ---
            // This moves the "pin" on your map to your current phone GPS
            await _db.UpdateSensorLocation(999, currentLat, currentLng);

            int simTemp = command switch {
                "/exdanger" => 50,
                "/danger"   => 43,
                "/caution"  => 40,
                "/normal"   => 32,
                _           => 25 
            };

            var result = new AlertResult
            {
                SensorCode = "MANUAL-01",
                DisplayName = "Mobile Surveyor",
                BarangayName = "Dynamic GPS", 
                RelativeLocation = "Surveyor (Moving)",
                Lat = currentLat, 
                Lng = currentLng, 
                HeatIndex = simTemp,
                CreatedAt = DateTime.UtcNow // Use UTC for the DB
            };

            // This saves the log AND updates GlobalData.LatestAlert
            await ProcessAndBroadcastAlert(result, 999);

            await bot.SendMessage(chatId, $"📍 Location Updated & Alert Sent!\nMap Pin moved to: {currentLat}, {currentLng}", 
                replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            
            _pendingSimulations.Remove(chatId);
        }

       // Add this variable at the top of your BotAlertSender class
        private DateTime _lastLogTime = DateTime.MinValue;

        private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
        {
            // 1. Only log to the file once every 30 seconds to prevent "Error Storms"
            if ((DateTime.Now - _lastLogTime).TotalSeconds < 30)
            {
                // Still print to console so you see it, but DON'T touch the Hard Drive
                Console.WriteLine($"[RATE LIMITED] Bot still offline: {ex.Message}");
                return Task.CompletedTask;
            }

            _lastLogTime = DateTime.Now;

            string logFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Logs");
            string logPath = Path.Combine(logFolder, "bot_errors.txt");
            string errorMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Error: {ex.Message}{Environment.NewLine}";

            try 
            {
                if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);
                File.AppendAllText(logPath, errorMessage + "-----------------------------------" + Environment.NewLine);
                Console.WriteLine("📂 Error written to log file.");
            } 
            catch { /* Avoid crashing the logger */ }

            return Task.CompletedTask;
        }

        public async Task BroadcastAlert(string alertMsg, List<long> subscriberIds)
        {
            int sentCount = 0;
            foreach (var id in subscriberIds)
            {
                try {
                    await _botClient.SendMessage(chatId: id, text: alertMsg);
                    sentCount++;
                } catch { /* Ignore blocked bots */ }
            }
            Console.WriteLine($"📢 Broadcast: {sentCount} users notified.");
        }
    }
}