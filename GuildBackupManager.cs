using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AbyssCore
{
    /// <summary>
    /// 伺服器架構全量備份與智慧權限覆寫還原中樞
    /// 核心特性:
    /// 1. 完整序列化身分組、類別、頻道、權限覆寫矩陣 (Permission Overwrites) 與 NSFW 屬性
    /// 2. 智慧映射矩陣 (Role Mapping Table): 解決身分組 ID 變更導致權限失效之難題
    /// 3. 類別父子結構串聯 (Category-Channel Parent Hierarchy Sync)
    /// </summary>
    public static class GuildBackupManager
    {
        public static async Task<string> BackupGuildStructureAsync(
            DiscordApiClient client,
            string guildId,
            string guildName,
            string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string safeName = string.Join("_", guildName.Split(Path.GetInvalidFileNameChars()));
            string filename = $"backup_{safeName}_{guildId}_{timestamp}.json";
            string filePath = Path.Combine(outputDir, filename);

            var guildObj = await client.GetGuildAsync(guildId);
            int? nsfwLevel = guildObj.HasValue && guildObj.Value.TryGetProperty("nsfw_level", out var nsfwP)
                ? nsfwP.GetInt32()
                : null;

            var rolesJson = await client.GetGuildRolesAsync(guildId);
            var channelsJson = await client.GetGuildChannelsAsync(guildId);

            var roleIdToNameMap = new Dictionary<string, string>();
            var rolesList = new List<BackupRole>();

            foreach (var r in rolesJson)
            {
                string rid = r.GetProperty("id").GetString()!;
                string rname = r.GetProperty("name").GetString() ?? "";
                roleIdToNameMap[rid] = rname;

                rolesList.Add(new BackupRole
                {
                    Id = rid,
                    Name = rname,
                    Color = r.TryGetProperty("color", out var c) ? c.GetInt32() : 0,
                    Position = r.TryGetProperty("position", out var p) ? p.GetInt32() : 0,
                    Permissions = r.TryGetProperty("permissions", out var pm) ? pm.GetString() ?? "0" : "0",
                    Hoist = r.TryGetProperty("hoist", out var h) && h.GetBoolean(),
                    Mentionable = r.TryGetProperty("mentionable", out var m) && m.GetBoolean()
                });
            }

            var categoryList = new List<BackupChannel>();
            var channelList = new List<BackupChannel>();

            foreach (var ch in channelsJson)
            {
                string cid = ch.GetProperty("id").GetString()!;
                string cname = ch.GetProperty("name").GetString() ?? "";
                int type = ch.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
                int? pos = ch.TryGetProperty("position", out var p) ? p.GetInt32() : null;
                string? parentId = ch.TryGetProperty("parent_id", out var pi) ? pi.GetString() : null;
                string? topic = ch.TryGetProperty("topic", out var tp) ? tp.GetString() : null;
                bool? nsfw = ch.TryGetProperty("nsfw", out var nf) ? nf.GetBoolean() : null;

                var overwritesList = new List<BackupOverwrite>();
                if (ch.TryGetProperty("permission_overwrites", out var owProp) && owProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ow in owProp.EnumerateArray())
                    {
                        string owId = ow.GetProperty("id").GetString()!;
                        int owType = ow.GetProperty("type").GetInt32();
                        string allow = ow.TryGetProperty("allow", out var aProp) ? aProp.GetString() ?? "0" : "0";
                        string deny = ow.TryGetProperty("deny", out var dProp) ? dProp.GetString() ?? "0" : "0";

                        string? rName = null;
                        if (owType == 0 && roleIdToNameMap.TryGetValue(owId, out var mappedName))
                        {
                            rName = mappedName;
                        }

                        overwritesList.Add(new BackupOverwrite
                        {
                            Id = owId,
                            Type = owType,
                            RoleName = rName,
                            Allow = allow,
                            Deny = deny
                        });
                    }
                }

                var item = new BackupChannel
                {
                    Id = cid,
                    Name = cname,
                    Type = type,
                    Position = pos,
                    ParentId = parentId,
                    Topic = topic,
                    Nsfw = nsfw,
                    Overwrites = overwritesList
                };

                if (type == 4)
                {
                    categoryList.Add(item);
                }
                else
                {
                    channelList.Add(item);
                }
            }

            var backupData = new AdvancedBackupModel
            {
                GuildId = guildId,
                GuildName = guildName,
                CreatedAt = DateTime.UtcNow.ToString("O"),
                NsfwLevel = nsfwLevel,
                Roles = rolesList,
                Categories = categoryList,
                Channels = channelList
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(backupData, options);
            await File.WriteAllTextAsync(filePath, json);

            await SafetyManager.LogAuditAsync("System", "架構完整備份", guildName, $"存檔路徑: {filePath}");

            return filePath;
        }

        public static async Task<RestoreReport> RestoreGuildStructureAsync(
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
            var report = new RestoreReport();

            AdvancedBackupModel? backup;
            try
            {
                backup = JsonSerializer.Deserialize<AdvancedBackupModel>(json);
            }
            catch
            {
                backup = null;
            }

            // 若備份為舊格式 JSON，自動向下相容
            if (backup == null || (backup.Roles.Count == 0 && backup.Channels.Count == 0 && backup.Categories.Count == 0))
            {
                return await RestoreLegacyBackupAsync(client, guildId, json, progressCallback);
            }

            // 0. 還原伺服器層級 NSFW 限制級設定
            if (backup.NsfwLevel.HasValue)
            {
                await client.ModifyGuildNsfwLevelAsync(guildId, backup.NsfwLevel.Value);
                report.NsfwRestored = true;
            }

            // 1. 還原身分組 (平行建立並構建 舊名稱/舊ID -> 新ID 映射表)
            progressCallback?.Invoke($"❖ 正在還原 {backup.Roles.Count} 個伺服器身分組與權限...");
            var roleNameToNewId = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 取得現有 @everyone 身分組 ID
            var currentRoles = await client.GetGuildRolesAsync(guildId);
            var everyoneRole = currentRoles.FirstOrDefault(r => r.GetProperty("name").GetString() == "@everyone");
            if (everyoneRole.ValueKind == JsonValueKind.Object)
            {
                string evId = everyoneRole.GetProperty("id").GetString()!;
                roleNameToNewId["@everyone"] = evId;
            }

            var roleTasks = backup.Roles.Where(r => r.Name != "@everyone").Select(r => Task.Run(async () =>
            {
                var newId = await client.CreateRoleAsync(
                    guildId,
                    r.Name,
                    r.Color,
                    r.Permissions,
                    r.Hoist,
                    r.Mentionable);

                if (newId != null)
                {
                    roleNameToNewId[r.Name] = newId;
                    System.Threading.Interlocked.Increment(ref report.RolesRestored);
                }
            }));
            await Task.WhenAll(roleTasks);
            progressCallback?.Invoke($"  ✔ 身分組還原完成: 成功建立 {report.RolesRestored} 個身分組，並建立權限映射字典。");

            // 2. 還原類別 (Categories, type 4) 並串接權限覆寫
            var oldCatIdToNewCatId = new ConcurrentDictionary<string, string>();
            if (backup.Categories.Count > 0)
            {
                progressCallback?.Invoke($"◈ 正在還原 {backup.Categories.Count} 個類別及其專屬身分組權限設定...");
                var catTasks = backup.Categories.Select(cat => Task.Run(async () =>
                {
                    var mappedOw = MapOverwrites(cat.Overwrites, roleNameToNewId, ref report.OverwritesRestored);
                    var newCatId = await client.CreateChannelAsync(
                        guildId,
                        cat.Name,
                        type: 4,
                        position: cat.Position,
                        permissionOverwrites: mappedOw);

                    if (newCatId != null)
                    {
                        oldCatIdToNewCatId[cat.Id] = newCatId;
                        System.Threading.Interlocked.Increment(ref report.CategoriesRestored);
                    }
                }));
                await Task.WhenAll(catTasks);
                progressCallback?.Invoke($"  ✔ 類別還原完成: 成功建立 {report.CategoriesRestored} 個類別目錄。");
            }

            // 3. 還原頻道 (文字與語音) 並綁定對應類別與身分組權限
            if (backup.Channels.Count > 0)
            {
                progressCallback?.Invoke($"◈ 正在還原 {backup.Channels.Count} 個頻道、類別層級與身分組存取規則...");
                var chTasks = backup.Channels.Select(ch => Task.Run(async () =>
                {
                    string? newParentId = null;
                    if (!string.IsNullOrEmpty(ch.ParentId) && oldCatIdToNewCatId.TryGetValue(ch.ParentId, out var mappedParent))
                    {
                        newParentId = mappedParent;
                    }

                    var mappedOw = MapOverwrites(ch.Overwrites, roleNameToNewId, ref report.OverwritesRestored);
                    var newCid = await client.CreateChannelAsync(
                        guildId,
                        ch.Name,
                        type: ch.Type,
                        position: ch.Position,
                        parentId: newParentId,
                        permissionOverwrites: mappedOw,
                        topic: ch.Topic,
                        nsfw: ch.Nsfw);

                    if (newCid != null)
                    {
                        System.Threading.Interlocked.Increment(ref report.ChannelsRestored);
                    }
                }));
                await Task.WhenAll(chTasks);
                progressCallback?.Invoke($"  ✔ 頻道還原完成: 成功建立 {report.ChannelsRestored} 個頻道，並綁定 {report.OverwritesRestored} 條權限覆寫規則！");
            }

            await SafetyManager.LogAuditAsync("System", "架構智慧還原", backup.GuildName, $"還原身分組: {report.RolesRestored}, 類別: {report.CategoriesRestored}, 頻道: {report.ChannelsRestored}, 權限覆寫: {report.OverwritesRestored}");

            return report;
        }

        private static List<object> MapOverwrites(
            List<BackupOverwrite> overwrites,
            ConcurrentDictionary<string, string> roleNameToNewId,
            ref int overwriteCounter)
        {
            var list = new List<object>();
            foreach (var ow in overwrites)
            {
                string targetId = ow.Id;
                if (ow.Type == 0 && !string.IsNullOrEmpty(ow.RoleName))
                {
                    if (roleNameToNewId.TryGetValue(ow.RoleName, out var mappedId))
                    {
                        targetId = mappedId;
                    }
                    else
                    {
                        continue; // 若該身分組未在新伺服器建立，跳過避免報錯
                    }
                }

                list.Add(new
                {
                    id = targetId,
                    type = ow.Type,
                    allow = ow.Allow,
                    deny = ow.Deny
                });
                System.Threading.Interlocked.Increment(ref overwriteCounter);
            }
            return list;
        }

        private static async Task<RestoreReport> RestoreLegacyBackupAsync(
            DiscordApiClient client,
            string guildId,
            string json,
            Action<string>? progressCallback)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var report = new RestoreReport();

            if (root.TryGetProperty("roles", out var rolesProp) && rolesProp.ValueKind == JsonValueKind.Array)
            {
                progressCallback?.Invoke("❖ 正在還原相容舊版身分組...");
                var roleTasks = rolesProp.EnumerateArray().Select(r => Task.Run(async () =>
                {
                    string name = r.GetProperty("name").GetString() ?? "";
                    if (name == "@everyone") return;
                    int color = r.TryGetProperty("color", out var c) ? c.GetInt32() : 0;
                    var rid = await client.CreateRoleAsync(guildId, name, color);
                    if (rid != null) System.Threading.Interlocked.Increment(ref report.RolesRestored);
                }));
                await Task.WhenAll(roleTasks);
            }

            if (root.TryGetProperty("channels", out var chsProp) && chsProp.ValueKind == JsonValueKind.Array)
            {
                progressCallback?.Invoke("◈ 正在還原相容舊版頻道架構...");
                var chTasks = chsProp.EnumerateArray().Select(ch => Task.Run(async () =>
                {
                    string name = ch.GetProperty("name").GetString() ?? "";
                    int type = ch.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
                    int? pos = ch.TryGetProperty("position", out var p) ? p.GetInt32() : null;
                    var cid = await client.CreateChannelAsync(guildId, name, type, pos);
                    if (cid != null) System.Threading.Interlocked.Increment(ref report.ChannelsRestored);
                }));
                await Task.WhenAll(chTasks);
            }

            return report;
        }
    }

    public class AdvancedBackupModel
    {
        [JsonPropertyName("guild_id")]
        public string GuildId { get; set; } = "";

        [JsonPropertyName("guild_name")]
        public string GuildName { get; set; } = "";

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = "";

        [JsonPropertyName("nsfw_level")]
        public int? NsfwLevel { get; set; }

        [JsonPropertyName("roles")]
        public List<BackupRole> Roles { get; set; } = new();

        [JsonPropertyName("categories")]
        public List<BackupChannel> Categories { get; set; } = new();

        [JsonPropertyName("channels")]
        public List<BackupChannel> Channels { get; set; } = new();
    }

    public class BackupRole
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("color")]
        public int Color { get; set; }

        [JsonPropertyName("position")]
        public int Position { get; set; }

        [JsonPropertyName("permissions")]
        public string Permissions { get; set; } = "0";

        [JsonPropertyName("hoist")]
        public bool Hoist { get; set; }

        [JsonPropertyName("mentionable")]
        public bool Mentionable { get; set; }
    }

    public class BackupChannel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("type")]
        public int Type { get; set; }

        [JsonPropertyName("position")]
        public int? Position { get; set; }

        [JsonPropertyName("parent_id")]
        public string? ParentId { get; set; }

        [JsonPropertyName("topic")]
        public string? Topic { get; set; }

        [JsonPropertyName("nsfw")]
        public bool? Nsfw { get; set; }

        [JsonPropertyName("permission_overwrites")]
        public List<BackupOverwrite> Overwrites { get; set; } = new();
    }

    public class BackupOverwrite
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("type")]
        public int Type { get; set; }

        [JsonPropertyName("role_name")]
        public string? RoleName { get; set; }

        [JsonPropertyName("allow")]
        public string Allow { get; set; } = "0";

        [JsonPropertyName("deny")]
        public string Deny { get; set; } = "0";
    }

    public class RestoreReport
    {
        public int RolesRestored;
        public int CategoriesRestored;
        public int ChannelsRestored;
        public int OverwritesRestored;
        public bool NsfwRestored;
    }
}
