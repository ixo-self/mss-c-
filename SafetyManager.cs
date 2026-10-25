using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AbyssCore
{
    /// <summary>
    /// 安全防護與審計中樞 (前置權限試跑、階級防護校驗、危險操作確認與 Channel 非同步審計日誌)
    /// </summary>
    public static class SafetyManager
    {
        private const string LogDirectory = "backups";
        private const string AuditLogFile = "audit.log";

        private static readonly Channel<string> _auditChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        static SafetyManager()
        {
            Task.Run(ProcessAuditLogQueueAsync);
        }

        private static async Task ProcessAuditLogQueueAsync()
        {
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(LogDirectory, AuditLogFile);
            var reader = _auditChannel.Reader;
            var batch = new List<string>(64);

            while (await reader.WaitToReadAsync())
            {
                batch.Clear();
                while (reader.TryRead(out var item))
                {
                    batch.Add(item);
                    if (batch.Count >= 256) break;
                }

                if (batch.Count > 0)
                {
                    try
                    {
                        await File.AppendAllLinesAsync(path, batch);
                    }
                    catch
                    {
                        // 忽略磁碟例外，確保不影響核心 API 吞吐運作
                    }
                }
            }
        }

        /// <summary>
        /// 記錄敏感操作至本地審計日誌 (非同步 Channel 無鎖寫入，極速微秒級響應)
        /// </summary>
        public static async Task LogAuditAsync(string trigger, string action, string target, string details = "")
        {
            string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] [AUDIT] 觸發者: {trigger} | 動作: {action} | 目標: {target}" +
                          (string.IsNullOrEmpty(details) ? "" : $" | 說明: {details}");
            _auditChannel.Writer.TryWrite(line);
            await Task.CompletedTask;
        }

        /// <summary>
        /// 檢驗指令是否帶有二次確認標記 (--confirm 或 -y)
        /// </summary>
        public static bool HasConfirmFlag(string[] args)
        {
            return args.Any(a => a.Equals("--confirm", StringComparison.OrdinalIgnoreCase) ||
                                 a.Equals("-y", StringComparison.OrdinalIgnoreCase) ||
                                 a.Equals("force", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 機器人前置權限試跑與安全巡檢報告
        /// </summary>
        public static async Task<SafetyCheckResult> CheckBotSafetyAsync(DiscordApiClient client, string guildId)
        {
            var result = new SafetyCheckResult();

            var me = await client.GetCurrentUserAsync();
            if (!me.HasValue)
            {
                result.Errors.Add("無法取得機器人自身的帳號資訊。");
                return result;
            }

            result.BotId = me.Value.GetProperty("id").GetString() ?? "";
            result.BotUsername = me.Value.GetProperty("username").GetString() ?? "AbyssBot";

            var guild = await client.GetGuildAsync(guildId);
            if (!guild.HasValue)
            {
                result.Errors.Add("無法取得目標伺服器資訊，請確認機器人是否在該伺服器內。");
                return result;
            }

            result.OwnerId = guild.Value.TryGetProperty("owner_id", out var oProp) ? oProp.GetString() ?? "" : "";
            result.GuildName = guild.Value.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";

            var roles = await client.GetGuildRolesAsync(guildId);
            var rolePosMap = new Dictionary<string, int>();
            var rolePermMap = new Dictionary<string, ulong>();

            foreach (var r in roles)
            {
                string rid = r.GetProperty("id").GetString()!;
                int pos = r.TryGetProperty("position", out var pProp) ? pProp.GetInt32() : 0;
                ulong perm = r.TryGetProperty("permissions", out var pmProp) && ulong.TryParse(pmProp.GetString(), out var pv) ? pv : 0;
                rolePosMap[rid] = pos;
                rolePermMap[rid] = perm;
            }

            var botMember = await client.GetGuildMemberAsync(guildId, result.BotId);
            ulong combinedPerms = 0;
            int highestPos = 0;

            if (botMember.HasValue && botMember.Value.TryGetProperty("roles", out var bRoles))
            {
                foreach (var rEl in bRoles.EnumerateArray())
                {
                    string rid = rEl.GetString()!;
                    if (rolePosMap.TryGetValue(rid, out int pos) && pos > highestPos)
                    {
                        highestPos = pos;
                    }
                    if (rolePermMap.TryGetValue(rid, out ulong perm))
                    {
                        combinedPerms |= perm;
                    }
                }
            }

            // 納入 @everyone 預設權限
            var everyoneRole = roles.FirstOrDefault(r => r.GetProperty("name").GetString() == "@everyone");
            if (everyoneRole.ValueKind == JsonValueKind.Object &&
                everyoneRole.TryGetProperty("permissions", out var evProp) &&
                ulong.TryParse(evProp.GetString(), out ulong evPerm))
            {
                combinedPerms |= evPerm;
            }

            result.HighestRolePosition = highestPos;
            result.PermissionsBitfield = combinedPerms;

            // 權限位元判定 (Discord v10)
            result.IsAdministrator = (combinedPerms & 0x8) != 0;
            result.CanKick = result.IsAdministrator || (combinedPerms & 0x2) != 0;
            result.CanBan = result.IsAdministrator || (combinedPerms & 0x4) != 0;
            result.CanManageChannels = result.IsAdministrator || (combinedPerms & 0x10) != 0;
            result.CanManageRoles = result.IsAdministrator || (combinedPerms & 0x10000000) != 0;
            result.CanModerateMembers = result.IsAdministrator || (combinedPerms & 0x10000000000) != 0;
            result.CanManageGuild = result.IsAdministrator || (combinedPerms & 0x20) != 0;

            return result;
        }

        /// <summary>
        /// 比對目標成員是否可被處分 (階級安全防護校驗)
        /// </summary>
        public static async Task<(bool CanAct, string Reason)> ValidateHierarchyAsync(
            DiscordApiClient client,
            string guildId,
            string targetUserId)
        {
            var safety = await CheckBotSafetyAsync(client, guildId);
            if (safety.Errors.Count > 0)
            {
                return (false, string.Join("; ", safety.Errors));
            }

            if (targetUserId == safety.BotId)
            {
                return (false, "無法對機器人自身執行此處分操作。");
            }

            if (targetUserId == safety.OwnerId)
            {
                return (false, "目標為伺服器擁有人 (Server Owner)，受 Discord 系統最高安全限制保護，無法處分。");
            }

            var targetMember = await client.GetGuildMemberAsync(guildId, targetUserId);
            if (!targetMember.HasValue)
            {
                return (false, "找不到指定的目標成員，可能已離開伺服器或 ID 無效。");
            }

            var roles = await client.GetGuildRolesAsync(guildId);
            var rolePosMap = roles.ToDictionary(
                r => r.GetProperty("id").GetString()!,
                r => r.TryGetProperty("position", out var pProp) ? pProp.GetInt32() : 0);

            int targetHighestPos = 0;
            if (targetMember.Value.TryGetProperty("roles", out var tRoles))
            {
                foreach (var rEl in tRoles.EnumerateArray())
                {
                    string rid = rEl.GetString()!;
                    if (rolePosMap.TryGetValue(rid, out int pos) && pos > targetHighestPos)
                    {
                        targetHighestPos = pos;
                    }
                }
            }

            if (targetHighestPos >= safety.HighestRolePosition && !safety.IsAdministrator)
            {
                return (false, $"目標成員的最高身分組位階 (Lv.{targetHighestPos}) 高於或等於機器人身分組位階 (Lv.{safety.HighestRolePosition})，Discord 拒絕操作 (403 Forbidden)。");
            }

            return (true, "安全校驗通過");
        }
    }

    /// <summary>
    /// 安全巡檢結果資料模型
    /// </summary>
    public class SafetyCheckResult
    {
        public string BotId { get; set; } = "";
        public string BotUsername { get; set; } = "";
        public string GuildName { get; set; } = "";
        public string OwnerId { get; set; } = "";
        public int HighestRolePosition { get; set; }
        public ulong PermissionsBitfield { get; set; }

        public bool IsAdministrator { get; set; }
        public bool CanKick { get; set; }
        public bool CanBan { get; set; }
        public bool CanManageChannels { get; set; }
        public bool CanManageRoles { get; set; }
        public bool CanModerateMembers { get; set; }
        public bool CanManageGuild { get; set; }

        public List<string> Errors { get; set; } = new();

        public string FormatSummary()
        {
            return "✦ **【永恆深淵 · 機器人安全與權限巡檢報告】** ✦\n" +
                   $"• 伺服器: `{GuildName}`\n" +
                   $"• 機器人最高位階: `Lv.{HighestRolePosition}`\n" +
                   $"• 最高管理員權限: `{(IsAdministrator ? "✔ 已取得 (不受個別限制)" : "▲ 未取得")}`\n" +
                   $"• 頻道管理權限: `{(CanManageChannels ? "✔ 正常" : "✖ 缺失")}`\n" +
                   $"• 身分組管理權限: `{(CanManageRoles ? "✔ 正常" : "✖ 缺失")}`\n" +
                   $"• 踢出成員權限: `{(CanKick ? "✔ 正常" : "✖ 缺失")}`\n" +
                   $"• 封鎖成員權限: `{(CanBan ? "✔ 正常" : "✖ 缺失")}`\n" +
                   $"• 禁言處分權限: `{(CanModerateMembers ? "✔ 正常" : "✖ 缺失")}`\n" +
                   $"• 伺服器設定權限: `{(CanManageGuild ? "✔ 正常" : "✖ 缺失")}`";
        }
    }
}
