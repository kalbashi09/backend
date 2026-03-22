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

        // 5-cycle manual sensor stage: sensorId -> (remainingCycles, fixedHeatIndex)
        public static readonly Dictionary<int, (int remainingCycles, int fixedHeatIndex)> ManualSensorSessions = new();

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
            var alarmingSpots = allReadings
                .Where(r => r.HeatIndex >= 39) 
                .OrderByDescending(r => r.HeatIndex)
                .ToList();

            if (!alarmingSpots.Any()) return; 

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🌡️ ***HEATSYNC: HIGH HEAT REPORT***");
            sb.AppendLine($"⏰ *Scanned at: {GlobalData.GetPHTime():hh:mm tt}*");
            sb.AppendLine("-----------------------------------");

            var topSpot = alarmingSpots.First();
            sb.AppendLine($"🔝 **HIGHEST:** {topSpot.HeatIndex}°C in {topSpot.BarangayName}");
            sb.AppendLine();

            foreach (var spot in alarmingSpots)
            {
                string emoji = spot.HeatIndex >= 49 ? "🔴" : 
                            spot.HeatIndex >= 42 ? "🟠" : "🟡";
                
                string level = _simulator.GetDangerLevel(spot.HeatIndex);
                sb.AppendLine($"{emoji} *{spot.HeatIndex}°C* - {level}");
                sb.AppendLine($"📍 {spot.DisplayName} ({spot.BarangayName})");
                sb.AppendLine();
            }

            sb.AppendLine("📍 *Tap the button below for the live interactive radar.*");

            // --- WEB APP BUTTON CONFIGURATION ---
            // IMPORTANT: This MUST be an HTTPS URL. 
            // Use an ngrok/Cloudflare tunnel for your local 'frontend/mapUI.html'
            string webAppUrl = "https://heatsync-zs03.onrender.com/mapUI.html";

            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                new []
                {
                    InlineKeyboardButton.WithWebApp("🌍 OPEN LIVE RADAR", new WebAppInfo { Url = webAppUrl })
                }
            });

            var subscribers = await _db.GetAllSubscriberIds();
            
            // Pass the keyboard to the broadcast method
            await BroadcastAlert(sb.ToString(), subscribers, inlineKeyboard);
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

            string username = message.From?.Username ?? "UnknownUser";

            // 1) Ensure every subscriber has an allocated sensor row
            await _db.EnsureSubscriberSensor(chatId, username);
            string sensorCode = $"MOBILE_{chatId}";
            var sensor = await _db.GetSensorByCode(sensorCode);
            if (sensor == null)
            {
                await bot.SendMessage(chatId, "⚠️ Sensor creation failed. Please try again.", cancellationToken: ct);
                return;
            }

            int targetHeat = command switch
            {
                "/exdanger" => 50,
                "/danger" => 43,
                "/caution" => 40,
                "/normal" => 32,
                _ => 25
            };

            // 2) Activate the sensor for the next 5 simulation cycles
            var sensorDto = new SensorUpdateDto
            {
                IsActive = true,
                BaselineTemp = targetHeat // lock-in the heat for those cycles too
            };
            await _db.UpdateSensorFlexible(sensor.Id, sensorDto);

            ManualSensorSessions[sensor.Id] = (remainingCycles: 5, fixedHeatIndex: targetHeat);

            // 3) Update location to current ping position
            double currentLat = message.Location!.Latitude;
            double currentLng = message.Location.Longitude;
            await _db.UpdateSensorLocation(sensor.Id, currentLat, currentLng);

            var result = new AlertResult
            {
                SensorCode = sensor.SensorCode,
                DisplayName = sensor.DisplayName,
                BarangayName = sensor.Barangay,
                RelativeLocation = "Mobile Surveyor",
                Lat = currentLat,
                Lng = currentLng,
                HeatIndex = targetHeat,
                CreatedAt = GlobalData.GetPHTime()
            };

            await ProcessAndBroadcastAlert(result, sensor.Id);

            await bot.SendMessage(chatId,
                $"📍 Mobile sensor activated (5 cycles) at {currentLat:F5}, {currentLng:F5} with {targetHeat}°C.",
                cancellationToken: ct);

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

        public async Task BroadcastAlert(string alertMsg, List<long> subscriberIds, InlineKeyboardMarkup? keyboard = null)
        {
            int sentCount = 0;
            foreach (var id in subscriberIds)
            {
                try 
                {
                    await _botClient.SendMessage(
                        chatId: id, 
                        text: alertMsg, 
                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                        replyMarkup: keyboard // This is where the magic happens
                    );
                    sentCount++;
                } 
                catch (Exception ex) 
                { 
                    Console.WriteLine($"[BROADCAST ERROR] User {id}: {ex.Message}"); 
                }
            }
            Console.WriteLine($"📢 Broadcast: {sentCount} users notified.");
        }
    }
}