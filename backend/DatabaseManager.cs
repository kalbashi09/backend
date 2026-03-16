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

        public async Task SaveHeatLog(AlertResult result)
        {
            if (result.HeatIndex >= 29 && result.HeatIndex <= 38) 
            {
                Console.WriteLine($"--- DB Skip: {result.HeatIndex}°C is in the 'Normal' range (29-38). ---");
                return; 
            }

            try 
            {
                // Changed to NpgsqlConnection
                using var connection = new NpgsqlConnection(_connString);
                await connection.OpenAsync();

                // Postgres standard uses 'NOW()' or 'CURRENT_TIMESTAMP'
                string query = @"INSERT INTO heat_logs (barangay, heat_index, latitude, longitude, created_at) 
                VALUES (@brgy, @heat, @lat, @lng, @created)";

                using var cmd = new NpgsqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@brgy", result.BarangayName ?? "Unknown");
                cmd.Parameters.AddWithValue("@heat", result.HeatIndex);
                cmd.Parameters.AddWithValue("@lat", result.Lat);
                cmd.Parameters.AddWithValue("@lng", result.Lng);
                // Just pass the UtcNow value; Postgres will shift it +8 upon entry
                cmd.Parameters.AddWithValue("@created", result.CreatedAt);
                
                await cmd.ExecuteNonQueryAsync();
                Console.WriteLine($"--- DB Saved (Postgres): {result.BarangayName} recorded at {result.HeatIndex}°C ---");

                _ = CleanupOldLogs();
            }
            catch (Exception ex) 
            {
                Console.WriteLine($"[CRITICAL PG DB ERROR]: {ex.Message}");
            }
        }

        private async Task CleanupOldLogs()
        {
            try
            {
                using var connection = new NpgsqlConnection(_connString);
                await connection.OpenAsync();

                // PostgreSQL syntax for the subquery cleanup
                string query = @"
                    DELETE FROM heat_logs 
                    WHERE id < (
                        SELECT id FROM heat_logs 
                        ORDER BY created_at DESC 
                        LIMIT 1 OFFSET 100
                    )";

                using var cmd = new NpgsqlCommand(query, connection);
                int deletedRows = await cmd.ExecuteNonQueryAsync();
                
                if (deletedRows > 0)
                {
                    Console.WriteLine($"--- DB Cleanup: Removed {deletedRows} old logs ---");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PG CLEANUP ERROR]: {ex.Message}");
            }
        }

        public async Task<List<AlertResult>> GetHistory(int limit = 100, int offset = 0)
        {
            var logs = new List<AlertResult>();
            try 
            {
                using var connection = new NpgsqlConnection(_connString);
                await connection.OpenAsync();
                
                string query = @"SELECT barangay, heat_index, latitude, longitude, created_at 
                                FROM heat_logs 
                                ORDER BY created_at DESC 
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
                        Lat = reader.GetDouble(2),
                        Lng = reader.GetDouble(3),
                        CreatedAt = reader.GetDateTime(4),
                        RelativeLocation = "Historical Record"
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
    }
}