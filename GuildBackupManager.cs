using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AbyssCore
{
    /// <summary>
    /// 伺服器架構備份與極速多並發還原中樞
    /// </summary>
    public static class GuildBackupManager
    {
        public static async Task<string> BackupGuildStructureAsync(DiscordApiClient client, string guildId, string guildName, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string safeName = string.Join("_", guildName.Split(Path.GetInvalidFileNameChars()));
            string filename = $"backup_{safeName}_{guildId}_{timestamp}.json";
            string filePath = Path.Combine(outputDir, filename);

            var channels = await client.GetGuildChannelsAsync(guildId);
            var roles = await client.GetGuildRolesAsync(guildId);

            var backupData = new
            {
                guild_id = guildId,
                guild_name = guildName,
                created_at = DateTime.UtcNow.ToString("O"),
                roles = roles,
                channels = channels
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(backupData, options);
            await File.WriteAllTextAsync(filePath, json);

            return filePath;
        }

        public static async Task<(int rolesRestored, int channelsRestored)> RestoreGuildStructureAsync(
            DiscordApiClient client,
            string guildId,
            string backupFilePath,
            Action<string>? progressCallback = null)
        {
            if (!File.Exists(backupFilePath))
            {
                throw new FileNotFoundException("找不到指定的備份檔案", backupFilePath);
            }

            string json = await File.ReadAllTextAsync(backupFilePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int rolesRestored = 0;
            int channelsRestored = 0;

            // 1. 還原身分組 (平行並發建立)
            if (root.TryGetProperty("roles", out var rolesProp) && rolesProp.ValueKind == JsonValueKind.Array)
            {
                progressCallback?.Invoke("❖ 正在並發還原伺服器身分組...");
                var roleTasks = new List<Task>();
                foreach (var r in rolesProp.EnumerateArray())
                {
                    string name = r.GetProperty("name").GetString() ?? "";
                    if (name == "@everyone") continue;
                    int color = r.TryGetProperty("color", out var c) ? c.GetInt32() : 0;

                    roleTasks.Add(Task.Run(async () =>
                    {
                        var rid = await client.CreateRoleAsync(guildId, name, color);
                        if (rid != null) System.Threading.Interlocked.Increment(ref rolesRestored);
                    }));
                }
                await Task.WhenAll(roleTasks);
            }

            // 2. 還原頻道 (先建類別，再建文字/語音頻道)
            if (root.TryGetProperty("channels", out var chsProp) && chsProp.ValueKind == JsonValueKind.Array)
            {
                progressCallback?.Invoke("◈ 正在並發還原伺服器頻道架構...");
                var chTasks = new List<Task>();
                foreach (var ch in chsProp.EnumerateArray())
                {
                    string name = ch.GetProperty("name").GetString() ?? "";
                    int type = ch.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
                    int? pos = ch.TryGetProperty("position", out var p) ? p.GetInt32() : null;

                    chTasks.Add(Task.Run(async () =>
                    {
                        var cid = await client.CreateChannelAsync(guildId, name, type, pos);
                        if (cid != null) System.Threading.Interlocked.Increment(ref channelsRestored);
                    }));
                }
                await Task.WhenAll(chTasks);
            }

            return (rolesRestored, channelsRestored);
        }
    }
}
