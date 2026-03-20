using Npgsql; // Changed from MySqlConnector
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Linq;

namespace HeatAlert 
{
    public class DatabaseManager 
    {
        private readonly string _connString;

        public DatabaseManager(string connString) 
        {
            _connString = connString;
        }

        public async Task SaveHeatLog(AlertResult result, int sensorId)
        {
            // Still skip normal temps to save space if you want
            if (result.HeatIndex >= 29 && result.HeatIndex <= 38) return; 

            using var connection = new NpgsqlConnection(_connString);
            await connection.OpenAsync();

            // V3 Query: Reference sensor_id instead of raw strings
            string query = @"INSERT INTO heat_logs (sensor_id, recorded_temp, heat_index, recorded_at) 
                            VALUES (@sid, @temp, @hi, @created)";

            using var cmd = new NpgsqlCommand(query, connection);

            cmd.Parameters.AddWithValue("@sid", sensorId);
            cmd.Parameters.AddWithValue("@temp", result.HeatIndex); // Or actual temp if you separate them
            cmd.Parameters.AddWithValue("@hi", result.HeatIndex);
            cmd.Parameters.AddWithValue("@created", GlobalData.GetPHTime());

            await cmd.ExecuteNonQueryAsync();

            _ = CleanupOldLogs();
        }

        private async Task CleanupOldLogs()
        {
            try
            {
                using var connection = new NpgsqlConnection(_connString);
                await connection.OpenAsync();

                // LOGIC: Keep the newest 300, delete everything else
                string query = @"
                    DELETE FROM heat_logs 
                    WHERE id NOT IN (
                        SELECT id FROM heat_logs 
                        ORDER BY recorded_at DESC 
                        LIMIT 300
                    )";

                using var cmd = new NpgsqlCommand(query, connection);
                int deletedRows = await cmd.ExecuteNonQueryAsync();
                
                if (deletedRows > 0)
                {
                    Console.WriteLine($"--- [DB Optimization]: Purged {deletedRows} stale logs. (Cap: 300) ---");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PG CLEANUP ERROR]: {ex.Message}");
            }
        }

        public async Task UpdateSensorLocation(int id, double lat, double lng)
        {
            using var connection = new NpgsqlConnection(_connString);
            await connection.OpenAsync();

            // V3: Update the coordinates for a specific sensor ID
            string query = "UPDATE sensor_registry SET latitude = @lat, longitude = @lng WHERE id = @id";

            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@lat", (decimal)lat); // cast to decimal for Postgres
            cmd.Parameters.AddWithValue("@lng", (decimal)lng);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<SensorNode?> GetSensorById(int id)
        {
            using var connection = new NpgsqlConnection(_connString);
            await connection.OpenAsync();

            // 🔥 ADDED environment_type here too
            string query = "SELECT id, sensor_code, display_name, barangay, latitude, longitude, baseline_temp, environment_type FROM sensor_registry WHERE id = @id";
            
            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new SensorNode {
                    Id = reader.GetInt32(0),
                    SensorCode = reader.GetString(1),
                    DisplayName = reader.GetString(2),
                    Barangay = reader.GetString(3),
                    Lat = Convert.ToDouble(reader.GetDecimal(4)),
                    Lng = Convert.ToDouble(reader.GetDecimal(5)),
                    BaselineTemp = reader.GetInt32(6),
                    // 🔥 ADD THIS LINE
                    EnvironmentType = reader.IsDBNull(7) ? "Unknown" : reader.GetString(7)
                };
            }
            return null;
        }

        public async Task<List<AlertResult>> GetHistory(int limit = 100, int offset = 0)
        {
            var logs = new List<AlertResult>();
            try 
            {
                using var connection = new NpgsqlConnection(_connString);
                await connection.OpenAsync();
                
                // V3: We JOIN the tables to get coordinates and names from sensor_registry
                string query = @"
                    SELECT 
                        s.barangay, 
                        l.heat_index, 
                        s.latitude, 
                        s.longitude, 
                        l.recorded_at, 
                        s.display_name, 
                        s.sensor_code
                    FROM heat_logs l
                    JOIN sensor_registry s ON l.sensor_id = s.id
                    ORDER BY l.recorded_at DESC 
                    LIMIT @limit OFFSET @offset";
                
                using var cmd = new NpgsqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@limit", limit);
                cmd.Parameters.AddWithValue("@offset", offset);

                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    logs.Add(new AlertResult {
                        BarangayName = reader.GetString(0),
                        HeatIndex = reader.GetInt32(1),
                        // PostgreSQL 'decimal' maps to 'decimal' in C#, we convert to double
                        Lat = Convert.ToDouble(reader.GetDecimal(2)),
                        Lng = Convert.ToDouble(reader.GetDecimal(3)),
                        CreatedAt = reader.GetDateTime(4),
                        DisplayName = reader.GetString(5),
                        SensorCode = reader.GetString(6)
                    });
                }
            }
            catch (Exception ex) { Console.WriteLine($"[PG DB ERROR] {ex.Message}"); }
            return logs;
        }

        public async Task SaveSubscriber(long chatId, string username) 
        {
            using var connection = new NpgsqlConnection(_connString);
            await connection.OpenAsync();
            
            // Postgres uses 'ON CONFLICT DO NOTHING' instead of 'INSERT IGNORE'
            string query = "INSERT INTO subscribers (chat_id, username) VALUES (@id, @user) ON CONFLICT (chat_id) DO NOTHING";
            
            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@id", chatId);
            cmd.Parameters.AddWithValue("@user", username ?? "Unknown");
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task RemoveSubscriber(long chatId) 
        {
            using var connection = new NpgsqlConnection(_connString);
            await connection.OpenAsync();

            string query = "DELETE FROM subscribers WHERE chat_id = @id";
            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@id", chatId);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task CreateSensor(SensorNode sensor)
        {
            using var connection = new NpgsqlConnection(_connString);
            await connection.OpenAsync();

            string query = @"INSERT INTO sensor_registry 
                (sensor_code, display_name, barangay, latitude, longitude, baseline_temp, environment_type, is_active) 
                VALUES (@code, @name, @brgy, @lat, @lng, @base, @env, @active)";

            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@code", sensor.SensorCode);
            cmd.Parameters.AddWithValue("@name", sensor.DisplayName);
            cmd.Parameters.AddWithValue("@brgy", sensor.Barangay);
            cmd.Parameters.AddWithValue("@lat", sensor.Lat);
            cmd.Parameters.AddWithValue("@lng", sensor.Lng);
            cmd.Parameters.AddWithValue("@base", sensor.BaselineTemp);
            cmd.Parameters.AddWithValue("@env", sensor.EnvironmentType);
            cmd.Parameters.AddWithValue("@active", sensor.IsActive);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task UpdateSensorFlexible(int id, SensorUpdateDto dto)
        {
            using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync();

            var updates = new List<string>();
            using var cmd = new NpgsqlCommand();
            cmd.Connection = conn;
            cmd.Parameters.AddWithValue("@id", id);

            // 1. Check each property. If it's not null, add it to the UPDATE list.
            if (dto.SensorCode != null) { 
                updates.Add("sensor_code = @sc"); 
                cmd.Parameters.AddWithValue("@sc", dto.SensorCode); 
            }
            if (dto.DisplayName != null) { 
                updates.Add("display_name = @dn"); 
                cmd.Parameters.AddWithValue("@dn", dto.DisplayName); 
            }
            if (dto.Barangay != null) { 
                updates.Add("barangay = @brgy"); 
                cmd.Parameters.AddWithValue("@brgy", dto.Barangay); 
            }
            if (dto.Lat.HasValue) { 
                updates.Add("latitude = @lat"); 
                cmd.Parameters.AddWithValue("@lat", (decimal)dto.Lat.Value); 
            }
            if (dto.Lng.HasValue) { 
                updates.Add("longitude = @lng"); 
                cmd.Parameters.AddWithValue("@lng", (decimal)dto.Lng.Value); 
            }
            if (dto.BaselineTemp.HasValue) { 
                updates.Add("baseline_temp = @base"); 
                cmd.Parameters.AddWithValue("@base", dto.BaselineTemp.Value); 
            }
            if (dto.EnvironmentType != null) { 
                updates.Add("environment_type = @env"); 
                cmd.Parameters.AddWithValue("@env", dto.EnvironmentType); 
            }
            if (dto.IsActive.HasValue) { 
                updates.Add("is_active = @ia"); 
                cmd.Parameters.AddWithValue("@ia", dto.IsActive.Value); 
            }

            // 2. If the user sent an empty JSON {}, just exit.
            if (updates.Count == 0) return;

            // 3. Join the strings: "SET sensor_code = @sc, latitude = @lat"
            cmd.CommandText = $"UPDATE sensor_registry SET {string.Join(", ", updates)} WHERE id = @id";

            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"--- [DB Update]: Sensor ID {id} patched with {updates.Count} changes. ---");
        }

        public async Task<List<long>> GetAllSubscriberIds()
        {
            var ids = new List<long>();
            using var connection = new NpgsqlConnection(_connString);
            await connection.OpenAsync();
            string query = "SELECT chat_id FROM subscribers";
            using var cmd = new NpgsqlCommand(query, connection);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetInt64(0));
            }
            return ids;
        }


        public async Task<List<SensorNode>> GetAllSensors(bool includeInactive = false)
        {
            var list = new List<SensorNode>();
            using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync();

            // 🔥 ADDED environment_type to the SELECT
            string query = "SELECT id, sensor_code, display_name, barangay, latitude, longitude, baseline_temp, is_active, environment_type FROM sensor_registry";
            
            if (!includeInactive) {
                query += " WHERE is_active = TRUE";
            }

            using var cmd = new NpgsqlCommand(query, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new SensorNode {
                    Id = reader.GetInt32(0),
                    SensorCode = reader.GetString(1),
                    DisplayName = reader.GetString(2),
                    Barangay = reader.GetString(3),
                    Lat = (double)reader.GetDecimal(4),
                    Lng = (double)reader.GetDecimal(5),
                    BaselineTemp = reader.GetInt32(6),
                    IsActive = reader.GetBoolean(7),
                    // 🔥 MAP THE NEW COLUMN (Index 8)
                    EnvironmentType = reader.IsDBNull(8) ? "Unknown" : reader.GetString(8)
                });
            }
            return list;
        }

        public async Task<AdminUser?> GetAdminByPersonnelId(string personnelId)
        {
            using var connection = new NpgsqlConnection(_connString);
            await connection.OpenAsync();

            // We only want active personnel
            string query = "SELECT AdminUID, PersonnelID, PasscodeHash, FullName FROM AuthPersonnel WHERE PersonnelID = @pid AND IsActive = TRUE";

            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@pid", personnelId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new AdminUser
                {
                    Id = reader.GetInt32(0),
                    PersonnelId = reader.GetString(1),
                    Hash = reader.GetString(2),
                    FullName = reader.GetString(3)
                };
            }
            return null;
        }

        // Update Last Login timestamp
        public async Task UpdateAdminLoginTime(int adminUid)
        {
            using var connection = new NpgsqlConnection(_connString);
            await connection.OpenAsync();
            string query = "UPDATE AuthPersonnel SET LastLogin = @now WHERE AdminUID = @id";
            using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@now", GlobalData.GetPHTime());
            cmd.Parameters.AddWithValue("@id", adminUid);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}