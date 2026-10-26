using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AbyssCore
{
    internal class Program
    {
        public static string? ActiveGuildId { get; private set; }
        public static string? ActiveGuildName { get; private set; }

        private static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "✦ 永恆深淵 · 終端中樞控制系統 (C# .NET 8 支援 .mss> 文字指令雙軌版) ✦";

            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string searchPath = Directory.Exists(Path.Combine(basePath, "..", "..", "..")) 
                ? Path.GetFullPath(Path.Combine(basePath, "..", "..", "..")) 
                : (File.Exists(Path.Combine(basePath, "config.json")) ? basePath : Path.GetFullPath(Path.Combine(basePath, "..")));

            ConfigManager.Load(searchPath);

            PrintBanner();

            string token = ConfigManager.Config.Token;
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("▶ 請輸入 Discord 機器人 Token: ");
                Console.ResetColor();
                token = Console.ReadLine()?.Trim() ?? "";
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("▲ 未提供有效 Token，程式結束。");
                Console.ResetColor();
                return;
            }

            using var client = new DiscordApiClient(token, maxConcurrency: 30);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("✦ 正在驗證機器人連線與身分資訊...");
            Console.ResetColor();

            var me = await client.GetCurrentUserAsync();
            if (me == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("✖ Token 驗證失敗！無法獲取機器人資訊，請檢查 Token 與網路代理。");
                Console.ResetColor();
                return;
            }

            string botName = me.Value.GetProperty("username").GetString() ?? "AbyssBot";
            string botId = me.Value.GetProperty("id").GetString() ?? "";

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ 機器人連線成功: 【{botName}】 (ID: {botId})");
            Console.ResetColor();

            // 啟動背景 Discord Gateway WebSocket 監聽器 (支援 .mss> 聊天室指令)
            using var gateway = new DiscordGatewayClient(token, (guildId, channelId, author, cmd) =>
                HandleChatCommandAsync(client, guildId, channelId, author, cmd));

            await gateway.StartAsync();

            while (true)
            {
                var guilds = await client.GetCurrentUserGuildsAsync();
                if (guilds.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("▲ 當前機器人未加入任何伺服器！請先邀請機器人入服。");
                    Console.ResetColor();
                    break;
                }

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("\n✦=======================【 伺服器列表 】=======================✦");
                for (int i = 0; i < guilds.Count; i++)
                {
                    string gName = guilds[i].GetProperty("name").GetString() ?? "";
                    string gId = guilds[i].GetProperty("id").GetString() ?? "";
                    Console.WriteLine($"  [{i + 1}] ✦ {gName} (ID: {gId})");
                }
                Console.WriteLine("  [0] ✖ 退出程式");
                Console.WriteLine("✦==============================================================✦");
                Console.ResetColor();

                Console.Write("▶ 請選擇目標伺服器編號 [0-" + guilds.Count + "]: ");
                string? input = Console.ReadLine()?.Trim();
                if (input == "0") break;

                if (!int.TryParse(input, out int sel) || sel < 1 || sel > guilds.Count)
                {
                    Console.WriteLine("▲ 輸入無效！");
                    continue;
                }

                var targetGuild = guilds[sel - 1];
                string guildId = targetGuild.GetProperty("id").GetString()!;
                string guildName = targetGuild.GetProperty("name").GetString()!;

                await RunGuildMenuAsync(client, guildId, guildName);
            }

            Console.WriteLine("\n[SYS] 系統已安全關閉。");
        }

        private static string CleanUserId(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            return input.Trim().Trim('<', '>', '@', '!', '&');
        }

        /// <summary>
        /// 處理 Discord 聊天室文字指令 (.mss>[指令])，支援群組頻道與私訊 (DM) 遠端操控
        /// </summary>
        private static async Task HandleChatCommandAsync(
            DiscordApiClient client,
            string guildId,
            string channelId,
            string author,
            string rawCommand)
        {
            if (string.IsNullOrWhiteSpace(rawCommand)) return;

            string[] parts = rawCommand.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string command = parts[0].ToLowerInvariant();
            string[] cmdArgs = parts.Skip(1).ToArray();

            bool isDm = string.IsNullOrEmpty(guildId);
            if (isDm)
            {
                guildId = ActiveGuildId ?? "";
                if (string.IsNullOrEmpty(guildId))
                {
                    var guilds = await client.GetCurrentUserGuildsAsync();
                    if (guilds.Count > 0)
                    {
                        guildId = guilds[0].GetProperty("id").GetString()!;
                        await client.SendMessageAsync(channelId, $"◈ [私訊管理員模式] 檢測到為私訊指令，已自動鎖定首個伺服器 (ID: {guildId})。");
                    }
                    else
                    {
                        await client.SendMessageAsync(channelId, "▲ 機器人尚未加入任何伺服器，無法執行伺服器管理指令。");
                        return;
                    }
                }
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n[{(isDm ? "私訊管理員" : "聊天室指令")}] 收到來自 {author} 的指令: .mss>{rawCommand}");
            Console.ResetColor();

            switch (command)
            {
                case "幫助":
                case "help":
                case "選單":
                case "menu":
                    string helpMsg = "✦ **【永恆深淵 · C# 旗艦文字指令清單】** ✦\n" +
                                     "◈ `.mss>清理` 啟動全服深度重構 (私訊邀請碼->備份->清理成員->類別頻道->建迷宮->換表情->換頭像->頂端Log)\n" +
                                     "◈ `.mss>踢出 <@成員/ID> [原因]` 踢出指定成員 (含位階安全防護校驗)\n" +
                                     "◈ `.mss>禁言 <@成員/ID> [分鐘] [原因]` 禁言指定成員 (1~40320 分鐘)\n" +
                                     "◈ `.mss>禁言 [分鐘]` 批量禁言低於機器人身分組之全體成員\n" +
                                     "◈ `.mss>解禁 <@成員/ID> [原因]` 立即解除指定成員之禁言狀態\n" +
                                     "◈ `.mss>內容可見度 [開/關/爆雷/本頻/過濾]` 調整伺服器與頻道內容可見度 (NSFW/爆雷警告)\n" +
                                     "◈ `.mss>安全` 執行機器人前置權限與安全狀態巡檢\n" +
                                     "◈ `.mss>私訊 [次數] [內容]` 批量向全體管理員連續發送私訊通知 (預設 10 次)\n" +
                                     "◈ `.mss>頻道 [數量]` 批量建立迷宮頻道 (預設 50，最高 500)\n" +
                                     "◈ `.mss>身分組 [數量]` 批量建立迷宮身分組 (預設 20，最高 100)\n" +
                                     "◈ `.mss>表情 [數量]` 滿額表情包置換 (清空舊表情貼圖並灌入可愛表情包)\n" +
                                     "◈ `.mss>頭像` 一鍵替換伺服器頭像為深淵專屬星門主題頭像\n" +
                                     "◈ `.mss>訊息 [輪數] [內容]` 批量向文字頻道廣播迷宮提示\n" +
                                     "◈ `.mss>更名 [目標名稱]` 批量修改頻道名稱\n" +
                                     "◈ `.mss>備份` 全量備份身分組、類別、頻道與權限覆寫矩陣 (JSON 存檔)\n" +
                                     "◈ `.mss>還原 [序號]` 從最新備份智慧映射還原身分組與權限規則\n" +
                                     "◈ `.mss>喵喵 [all/here]` 投放 2 輪喵喵病毒迷宮訊息 (預設全文字頻道 / here 當前頻道)\n" +
                                     "◈ `.mss>主題 [1-7]` 切換世界觀主題 (1.深淵 2.廢墟 3.救贖 4.月之 5.森林 6.寒冰 7.時空)";
                    await client.SendMessageAsync(channelId, helpMsg);
                    break;

                case "清理":
                case "重構":
                case "overhaul":
                    await client.SendMessageAsync(channelId, "✦ 正在透過 C# 旗艦引擎啟動【全景深度重構】流水線（私訊邀請碼 -> 架構備份 -> 拔除 Webhook -> 封鎖外部 Bot -> 處分低階成員 -> 刪除類別頻道身分組 -> 建立 150 頻道 + 50 身分組 -> 替換表情包 -> 替換頭像 -> 頂端頻道發布日誌）...");
                    var pipeline = new PipelineEngine(client, ConfigManager.Texts);
                    var report = await pipeline.ExecuteFullOverhaulAsync(guildId, ActiveGuildName ?? "當前伺服器", msg =>
                    {
                        Console.WriteLine($"  [清理進度] {msg}");
                    });
                    string reportMsg = $"✔ **【全景重構完成】**\n" +
                                       $"• 清理了 `{report.DeletedCategories}` 個類別\n" +
                                       $"• 清理了 `{report.DeletedChannels}` 個頻道\n" +
                                       $"• 清理了 `{report.BannedBots}` 個 bot\n" +
                                       $"• 清理了 `{report.BannedHumans}` 個成員\n" +
                                       $"• 替換了 `{report.CreatedEmojis}` 個表情包與 `{report.CreatedStickers}` 個貼圖 (清空 `{report.DeletedEmojis}` 舊表情 + `{report.DeletedStickers}` 貼圖)\n" +
                                       $"• 伺服器頭像替換: `{(report.GuildIconUpdated ? "✔ 已套用深淵專屬星門主題頭像" : "▲ 維持原樣")}`\n" +
                                       $"• 已發送防誤踢邀請碼 (wWB2RcR7pX) 私訊: `{report.DmInvitedMembers}` 位成員\n" +
                                       $"• 拔除 Webhook: `{report.DeletedWebhooks}` 個\n" +
                                       $"• 刪除舊身分組: `{report.DeletedRoles}` 個\n" +
                                       $"★ 新建迷宮頻道: `{report.CreatedChannels}` 個\n" +
                                       $"★ 新建迷宮身分組: `{report.CreatedRoles}` 個\n" +
                                       $"◈ 頂部日誌輸出: {(report.TopChannelLogged ? $"✔ 已寫入最頂端頻道 (ID: {report.TopChannelId})" : "▲ 未能寫入")}\n" +
                                       $"◈ 喵喵病毒投放: {(report.NekoVirusWavesSent > 0 ? $"✔ 已連續投放 {report.NekoVirusWavesSent} 波 10-Embed 巨型矩陣" : "▲ 略過")}\n" +
                                       $"◈ 總耗時間: `{report.ElapsedSeconds:F2}` 秒";
                    await client.SendMessageAsync(channelId, reportMsg);
                    break;

                case "私訊":
                case "dm":
                case "dmadmins":
                    int dmCount = cmdArgs.Length > 0 && int.TryParse(cmdArgs[0], out int dmc) && dmc > 0 ? Math.Min(50, dmc) : 10;
                    string dmContent = cmdArgs.Length > 1 ? string.Join(" ", cmdArgs.Skip(1)) : "迷宮以開啟";
                    await client.SendMessageAsync(channelId, $"◈ 正在向伺服器全體真人管理員每人發送 {dmCount} 次私訊【{dmContent}】...");
                    var dmPipe = new PipelineEngine(client, ConfigManager.Texts);
                    var (dmSent, dmTarget, adminCnt) = await dmPipe.SendDmToAdminsAsync(guildId, dmCount, dmContent, msg =>
                    {
                        Console.WriteLine($"  [私訊管理員] {msg}");
                    });
                    await client.SendMessageAsync(channelId, $"✔ **【管理員私訊派發完畢】**\n• 涵蓋管理員: `{adminCnt}` 位\n• 成功送達: `{dmSent}/{dmTarget}` 則\n• 私訊內容: `{dmContent}`");
                    break;

                case "表情":
                case "emoji":
                case "emojis":
                    int emojiCount = cmdArgs.Length > 0 && int.TryParse(cmdArgs[0], out int ec) && ec > 0 ? Math.Min(50, ec) : 25;
                    await client.SendMessageAsync(channelId, $"❖ 正在清理舊表情貼圖並灌入 {emojiCount} 個專屬星輝結晶可愛表情包...");
                    var oldEmojis = await client.GetGuildEmojisAsync(guildId);
                    int delECount = 0;
                    var delETasks = oldEmojis.Select(e => Task.Run(async () =>
                    {
                        if (await client.DeleteEmojiAsync(guildId, e.GetProperty("id").GetString()!))
                        {
                            Interlocked.Increment(ref delECount);
                        }
                    }));
                    var oldStickers = await client.GetGuildStickersAsync(guildId);
                    int delSCount = 0;
                    var delSTasks = oldStickers.Select(s => Task.Run(async () =>
                    {
                        if (await client.DeleteStickerAsync(guildId, s.GetProperty("id").GetString()!))
                        {
                            Interlocked.Increment(ref delSCount);
                        }
                    }));
                    await Task.WhenAll(delETasks.Concat(delSTasks));
                    int createECount = 0;
                    var crETasks = Enumerable.Range(1, emojiCount).Select(i => Task.Run(async () =>
                    {
                        if (await client.CreateEmojiAsync(guildId, $"cute_star_{i:D2}", AssetConstants.CuteEmojiBase64))
                        {
                            Interlocked.Increment(ref createECount);
                        }
                    }));
                    await Task.WhenAll(crETasks);
                    await client.SendMessageAsync(channelId, $"✔ 表情包滿額置換完畢！已清理 `{delECount}` 個舊表情、`{delSCount}` 個貼圖，成功灌入 `{createECount}/{emojiCount}` 個可愛表情包。");
                    break;

                case "頭像":
                case "icon":
                case "avatar":
                    await client.SendMessageAsync(channelId, "✦ 正在為伺服器更換為深淵星門專屬主題頭像...");
                    bool iconOk = await client.ModifyGuildIconAsync(guildId, AssetConstants.AbyssIconBase64);
                    await client.SendMessageAsync(channelId, iconOk ? "✔ 伺服器專屬深淵星門頭像已成功套用！" : "▲ 伺服器頭像更換失敗或無權限。");
                    break;

                case "頻道":
                case "ch":
                case "channel":
                    int chCount = cmdArgs.Length > 0 && int.TryParse(cmdArgs[0], out int c) && c > 0 ? Math.Min(500, c) : 50;
                    await client.SendMessageAsync(channelId, $"✦ 正在以 C# 主動令牌桶控速並發建立 {chCount} 個文字頻道...");
                    var names = ConfigManager.Texts.DefaultNames.ChannelNames;
                    int chDone = 0;
                    var chTasks = Enumerable.Range(1, chCount).Select(i =>
                    {
                        string name = $"▸-{names[i % names.Count]}-{i:D3}";
                        return Task.Run(async () =>
                        {
                            if (await client.CreateChannelAsync(guildId, name) != null)
                            {
                                System.Threading.Interlocked.Increment(ref chDone);
                            }
                        });
                    });
                    await Task.WhenAll(chTasks);
                    await client.SendMessageAsync(channelId, $"✔ 成功建立 `{chDone}/{chCount}` 個文字頻道！");
                    break;

                case "身分組":
                case "role":
                case "roles":
                    int roleCount = cmdArgs.Length > 0 && int.TryParse(cmdArgs[0], out int r) && r > 0 ? Math.Min(100, r) : 20;
                    await client.SendMessageAsync(channelId, $"✦ 正在以 C# 極速並發建立 {roleCount} 個身分組...");
                    var rNames = ConfigManager.Texts.DefaultNames.RoleNames;
                    int roleDone = 0;
                    var rTasks = Enumerable.Range(1, roleCount).Select(i =>
                    {
                        string name = $"✦ {rNames[i % rNames.Count]} Lv.{i}";
                        int color = Random.Shared.Next(0x100000, 0xFFFFFF);
                        return Task.Run(async () =>
                        {
                            if (await client.CreateRoleAsync(guildId, name, color) != null)
                            {
                                System.Threading.Interlocked.Increment(ref roleDone);
                            }
                        });
                    });
                    await Task.WhenAll(rTasks);
                    await client.SendMessageAsync(channelId, $"✔ 成功建立 `{roleDone}/{roleCount}` 個身分組！");
                    break;

                case "訊息":
                case "send":
                case "msg":
                    int rounds = cmdArgs.Length > 0 && int.TryParse(cmdArgs[0], out int rnd) && rnd > 0 ? Math.Min(20, rnd) : 2;
                    string content = cmdArgs.Length > 1 ? string.Join(" ", cmdArgs.Skip(1)) : ConfigManager.Texts.Messages.DefaultMazeMessage;
                    await client.SendMessageAsync(channelId, $"✦ 正在向文字頻道發送 {rounds} 輪迷宮訊息廣播...");
                    var allChs = await client.GetGuildChannelsAsync(guildId);
                    var txtChs = allChs.Where(ch => ch.TryGetProperty("type", out var t) && t.GetInt32() == 0).ToList();
                    int sent = 0;
                    var sendTasks = txtChs.Select(ch =>
                    {
                        string cid = ch.GetProperty("id").GetString()!;
                        return Task.Run(async () =>
                        {
                            for (int i = 0; i < rounds; i++)
                            {
                                if (await client.SendMessageAsync(cid, content))
                                {
                                    System.Threading.Interlocked.Increment(ref sent);
                                }
                            }
                        });
                    });
                    await Task.WhenAll(sendTasks);
                    await client.SendMessageAsync(channelId, $"✔ 訊息廣播完畢！已向 `{txtChs.Count}` 個文字頻道發送共 `{sent}` 則訊息。");
                    break;

                case "踢出":
                case "kick":
                    if (cmdArgs.Length == 0)
                    {
                        await client.SendMessageAsync(channelId, "▲ 請提供欲踢出之成員 ID 或 Mention: `.mss>踢出 <@用戶/ID> [原因]`");
                        break;
                    }
                    string kickUid = CleanUserId(cmdArgs[0]);
                    string kickReason = cmdArgs.Length > 1 ? string.Join(" ", cmdArgs.Skip(1)) : "管理員執行踢出處分";
                    var (canKick, kickCheckMsg) = await SafetyManager.ValidateHierarchyAsync(client, guildId, kickUid);
                    if (!canKick)
                    {
                        await client.SendMessageAsync(channelId, $"▲ 安全攔截: {kickCheckMsg}");
                        break;
                    }
                    if (await client.KickMemberAsync(guildId, kickUid, kickReason))
                    {
                        await SafetyManager.LogAuditAsync(author, "踢出成員", kickUid, kickReason);
                        await client.SendMessageAsync(channelId, $"✔ 已成功將成員 `<@{kickUid}>` 踢出伺服器！(原因: {kickReason})");
                    }
                    else
                    {
                        await client.SendMessageAsync(channelId, $"✖ 踢出成員 `<@{kickUid}>` 失敗，請確認機器人是否具備【踢出成員】權限。");
                    }
                    break;

                case "禁言":
                case "timeout":
                    if (cmdArgs.Length > 0 && (cmdArgs[0].StartsWith("<@") || (cmdArgs[0].Length >= 17 && ulong.TryParse(cmdArgs[0], out _))))
                    {
                        string targetUid = CleanUserId(cmdArgs[0]);
                        int minutes = cmdArgs.Length > 1 && int.TryParse(cmdArgs[1], out int m1) && m1 > 0 ? Math.Min(40320, m1) : 5;
                        string reason = cmdArgs.Length > 2 ? string.Join(" ", cmdArgs.Skip(2)) : "管理員執行禁言處分";

                        var (canModerate, hierMsg) = await SafetyManager.ValidateHierarchyAsync(client, guildId, targetUid);
                        if (!canModerate)
                        {
                            await client.SendMessageAsync(channelId, $"▲ 安全攔截: {hierMsg}");
                            break;
                        }

                        if (await client.TimeoutMemberAsync(guildId, targetUid, minutes, reason))
                        {
                            await SafetyManager.LogAuditAsync(author, "單人禁言", targetUid, $"{minutes} 分鐘 (原因: {reason})");
                            await client.SendMessageAsync(channelId, $"✔ 已成功將成員 `<@{targetUid}>` 禁言 `{minutes}` 分鐘！(原因: {reason})");
                        }
                        else
                        {
                            await client.SendMessageAsync(channelId, $"✖ 禁言成員 `<@{targetUid}>` 失敗，請確認機器人是否具備【禁言成員】權限。");
                        }
                    }
                    else
                    {
                        int mins = cmdArgs.Length > 0 && int.TryParse(cmdArgs[0], out int m) && m > 0 ? m : 5;
                        await client.SendMessageAsync(channelId, $"✦ 正在對全服低階成員批量處分禁言 {mins} 分鐘...");
                        var members = await client.GetGuildMembersAsync(guildId);
                        var meUser = await client.GetCurrentUserAsync();
                        string myId = meUser?.GetProperty("id").GetString() ?? "";
                        int timeoutOk = 0;
                        var tTasks = members.Select(mem =>
                        {
                            if (!mem.TryGetProperty("user", out var u)) return Task.CompletedTask;
                            string uid = u.GetProperty("id").GetString()!;
                            if (uid == myId) return Task.CompletedTask;
                            return Task.Run(async () =>
                            {
                                if (await client.TimeoutMemberAsync(guildId, uid, mins, "全服批量禁言"))
                                {
                                    System.Threading.Interlocked.Increment(ref timeoutOk);
                                }
                            });
                        });
                        await Task.WhenAll(tTasks);
                        await SafetyManager.LogAuditAsync(author, "全服批量禁言", guildId, $"共處分 {timeoutOk} 位成員 {mins} 分鐘");
                        await client.SendMessageAsync(channelId, $"✔ 禁言處分完成！共處分 `{timeoutOk}` 位成員。");
                    }
                    break;

                case "解禁":
                case "untimeout":
                case "unmute":
                    if (cmdArgs.Length == 0)
                    {
                        await client.SendMessageAsync(channelId, "▲ 請提供目標成員 ID 或 Mention: `.mss>解禁 <@用戶/ID> [原因]`");
                        break;
                    }
                    string unmuteUid = CleanUserId(cmdArgs[0]);
                    string unmuteReason = cmdArgs.Length > 1 ? string.Join(" ", cmdArgs.Skip(1)) : "管理員解除禁言處分";
                    if (await client.RemoveTimeoutMemberAsync(guildId, unmuteUid, unmuteReason))
                    {
                        await SafetyManager.LogAuditAsync(author, "解除禁言", unmuteUid, unmuteReason);
                        await client.SendMessageAsync(channelId, $"✔ 已成功解除成員 `<@{unmuteUid}>` 之禁言狀態！");
                    }
                    else
                    {
                        await client.SendMessageAsync(channelId, $"✖ 解除成員 `<@{unmuteUid}>` 禁言失敗，請確認該成員是否存在。");
                    }
                    break;

                case "內容可見度":
                case "可見度":
                case "限制級":
                case "限制":
                case "nsfw":
                case "visibility":
                    if (cmdArgs.Length == 0 || cmdArgs[0] == "幫助" || cmdArgs[0] == "help" || cmdArgs[0] == "?")
                    {
                        string helpNsfw =
                            "✦ **【永恆深淵 · 內容可見度 (Content Visibility) 指令說明】** ✦\n" +
                            "◈ `.mss>內容可見度 [開/關]` 一鍵全服批量切換【年齡限制頻道 (NSFW)】狀態\n" +
                            "◈ `.mss>內容可見度 爆雷 [開/關]` 一鍵全服批量切換【爆雷內容頻道 (Spoiler)】狀態\n" +
                            "◈ `.mss>內容可見度 本頻 [開/關]` 僅針對當前文字頻道切換內容可見度\n" +
                            "◈ `.mss>內容可見度 過濾 [0/1/2]` 切換伺服器安全不良內容過濾器 (0:關閉 1:無身分組 2:全體)";
                        await client.SendMessageAsync(channelId, helpNsfw);
                        break;
                    }

                    string subCmd = cmdArgs[0].ToLowerInvariant();
                    if (subCmd == "爆雷" || subCmd == "spoiler")
                    {
                        bool isSpoilerOn = cmdArgs.Length > 1 && (cmdArgs[1] == "開" || cmdArgs[1] == "on" || cmdArgs[1] == "true" || cmdArgs[1] == "1");
                        await client.SendMessageAsync(channelId, $"✦ 正在多工並發將全服頻道之內容可見度設定為: `{(isSpoilerOn ? "爆雷內容頻道 (開啟)" : "普遍級 (關閉)")}`...");
                        var (sCount, tCount) = await client.BatchSetChannelsContentVisibilityAsync(guildId, nsfw: false, isSpoiler: isSpoilerOn);
                        await SafetyManager.LogAuditAsync(author, "批次設定爆雷頻道", guildId, $"狀態: {isSpoilerOn}, 成功: {sCount}/{tCount}");
                        await client.SendMessageAsync(channelId, $"✔ 全服頻道內容可見度更新完畢！已成功將 `{sCount}/{tCount}` 個頻道設定為 `{(isSpoilerOn ? "爆雷內容頻道" : "普遍級")}`。");
                    }
                    else if (subCmd == "本頻" || subCmd == "here" || subCmd == "單頻")
                    {
                        bool isHereOn = cmdArgs.Length > 1 && (cmdArgs[1] == "開" || cmdArgs[1] == "on" || cmdArgs[1] == "true" || cmdArgs[1] == "1");
                        bool ok = await client.SetChannelNsfwAsync(channelId, isHereOn);
                        if (ok)
                        {
                            await SafetyManager.LogAuditAsync(author, "單頻設定內容可見度", channelId, $"NSFW: {isHereOn}");
                            await client.SendMessageAsync(channelId, $"✔ 當前頻道【內容可見度：年齡限制 (NSFW)】已切換為: `{(isHereOn ? "開啟 (需滿 18 歲驗證)" : "關閉 (普遍級)")}`！");
                        }
                        else
                        {
                            await client.SendMessageAsync(channelId, "✖ 當前頻道內容可見度設定失敗，請確認機器人擁有【管理頻道】權限。");
                        }
                    }
                    else if (subCmd == "過濾" || subCmd == "filter")
                    {
                        int fLvl = (cmdArgs.Length > 1 && int.TryParse(cmdArgs[1], out int fl)) ? Math.Clamp(fl, 0, 2) : 2;
                        bool fOk = await client.ModifyGuildExplicitContentFilterAsync(guildId, fLvl);
                        string fDesc = fLvl == 0 ? "關閉 (不掃描)" : (fLvl == 1 ? "掃描無身分組成員" : "掃描所有成員");
                        if (fOk)
                        {
                            await SafetyManager.LogAuditAsync(author, "設定不良內容過濾器", guildId, $"等級: {fLvl} ({fDesc})");
                            await client.SendMessageAsync(channelId, $"✔ 伺服器【不良內容過濾器】已成功設定為等級 `{fLvl}` ({fDesc})！");
                        }
                        else
                        {
                            await client.SendMessageAsync(channelId, "▲ 伺服器不良內容過濾器設定失敗。");
                        }
                    }
                    else
                    {
                        // 預設為全服切換年齡限制 (NSFW)
                        bool enableNsfw = subCmd == "開" || subCmd == "on" || subCmd == "true" || subCmd == "1" || subCmd == "nsfw";
                        await client.SendMessageAsync(channelId, $"✦ 正在多工並發將全服文字與語音頻道之【內容可見度：年齡限制 (NSFW)】切換為: `{(enableNsfw ? "開啟 (需滿 18 歲警報)" : "關閉 (普遍級)")}`...");
                        var (okCount, totalCount) = await client.BatchSetChannelsContentVisibilityAsync(guildId, nsfw: enableNsfw, isSpoiler: false);
                        // 同步調整伺服器不良內容過濾器
                        await client.ModifyGuildExplicitContentFilterAsync(guildId, enableNsfw ? 2 : 0);
                        await SafetyManager.LogAuditAsync(author, "批次調整內容可見度NSFW", guildId, $"NSFW: {enableNsfw}, 頻道數: {okCount}/{totalCount}");
                        await client.SendMessageAsync(channelId, $"✔ 全服頻道【內容可見度：年齡限制 (NSFW)】設定完成！已成功將 `{okCount}/{totalCount}` 個頻道套用為 `{(enableNsfw ? "NSFW 開啟" : "普遍級")}`。");
                    }
                    break;

                case "安全":
                case "safety":
                case "check":
                case "巡檢":
                    var safetyReport = await SafetyManager.CheckBotSafetyAsync(client, guildId);
                    await client.SendMessageAsync(channelId, safetyReport.FormatSummary());
                    break;

                case "備份":
                case "backup":
                    await client.SendMessageAsync(channelId, "✦ 正在全量序列化伺服器身分組、類別、頻道與身分組權限覆寫矩陣 (Permission Overwrites) 至 JSON 檔案...");
                    string bFile = await GuildBackupManager.BackupGuildStructureAsync(client, guildId, ActiveGuildName ?? "Guild", "backups");
                    await client.SendMessageAsync(channelId, $"✔ 伺服器架構與權限已成功備份至本地: `{Path.GetFileName(bFile)}`");
                    break;

                case "還原":
                case "restore":
                    string backupDir = "backups";
                    if (!Directory.Exists(backupDir))
                    {
                        await client.SendMessageAsync(channelId, "▲ 找不到 backups 備份目錄。");
                        break;
                    }
                    var backupFiles = Directory.GetFiles(backupDir, "*.json").OrderByDescending(f => File.GetCreationTime(f)).ToList();
                    if (backupFiles.Count == 0)
                    {
                        await client.SendMessageAsync(channelId, "▲ backups 目錄中無任何備份檔案。");
                        break;
                    }
                    string targetBackup = backupFiles[0];
                    if (cmdArgs.Length > 0 && int.TryParse(cmdArgs[0], out int bIdx) && bIdx >= 1 && bIdx <= backupFiles.Count)
                    {
                        targetBackup = backupFiles[bIdx - 1];
                    }
                    await client.SendMessageAsync(channelId, $"✦ 正在從備份 `{Path.GetFileName(targetBackup)}` 啟動智慧架構與權限還原...");
                    var restReport = await GuildBackupManager.RestoreGuildStructureAsync(client, guildId, targetBackup, msg =>
                    {
                        Console.WriteLine($"  [還原進度] {msg}");
                    });
                    string restMsg = $"✔ **【智慧架構還原完成】**\n" +
                                     $"• 身分組還原: `{restReport.RolesRestored}` 個\n" +
                                     $"• 類別還原: `{restReport.CategoriesRestored}` 個\n" +
                                     $"• 頻道還原: `{restReport.ChannelsRestored}` 個\n" +
                                     $"• 身分組權限覆寫: `{restReport.OverwritesRestored}` 條規則\n" +
                                     $"• 限制級狀態: `{(restReport.NsfwRestored ? "✔ 已同步原樣復原" : "▲ 維持原樣")}`";
                    await client.SendMessageAsync(channelId, restMsg);
                    break;

                case "喵喵":
                case "病毒":
                case "neko":
                    var nekoGifsList = ConfigManager.GetNekoGifs();
                    bool singleChannelMode = cmdArgs.Length > 0 && (cmdArgs[0].Equals("here", StringComparison.OrdinalIgnoreCase) || cmdArgs[0] == "單頻");

                    if (!singleChannelMode)
                    {
                        var chList = await client.GetGuildChannelsAsync(guildId);
                        var targetChs = chList.Where(ch => ch.TryGetProperty("type", out var t) && t.GetInt32() == 0).ToList();
                        await client.SendMessageAsync(channelId, $"✦ 正在向全服 `{targetChs.Count}` 個文字頻道連續投放 2 輪【喵喵病毒迷宮訊息】(10 巨型 Embed + 10 組 cute.gif + 25 重按鈕矩陣)...");
                        int vSent = 0;
                        var allVTasks = targetChs.Select(ch =>
                        {
                            string cid = ch.GetProperty("id").GetString()!;
                            string cname = ch.TryGetProperty("name", out var np) ? np.GetString() ?? "迷宮節點" : "迷宮節點";
                            return Task.Run(async () =>
                            {
                                for (int w = 1; w <= 2; w++)
                                {
                                    var nekoPayload = PipelineEngine.BuildNekoVirusPayload(w, 2, cname, nekoGifsList);
                                    if (await client.SendComplexMessageAsync(cid, nekoPayload))
                                    {
                                        Interlocked.Increment(ref vSent);
                                    }
                                    if (w < 2) await Task.Delay(Random.Shared.Next(150, 400));
                                }
                            });
                        });
                        await Task.WhenAll(allVTasks);
                        await client.SendMessageAsync(channelId, $"✔ 喵喵病毒全服投放完畢！已向 `{targetChs.Count}` 個文字頻道累計發送共 `{vSent}` 則巨型病毒矩陣！");
                    }
                    else
                    {
                        await client.SendMessageAsync(channelId, "✦ 正在向本頻道連續投放 2 次【喵喵病毒迷宮訊息】(10 巨型 Embed + 10 組 cute.gif + 25 重按鈕矩陣)...");
                        int waveSuccess = 0;
                        for (int w = 1; w <= 2; w++)
                        {
                            var nekoPayload = PipelineEngine.BuildNekoVirusPayload(w, 2, "即時廣播頻道", nekoGifsList);
                            if (await client.SendComplexMessageAsync(channelId, nekoPayload))
                            {
                                waveSuccess++;
                            }
                            if (w < 2) await Task.Delay(300);
                        }
                        await client.SendMessageAsync(channelId, $"✔ 喵喵病毒訊息發送完畢！成功發送 `{waveSuccess}/2` 波巨型迷宮矩陣。");
                    }
                    break;

                case "主題":
                case "theme":
                    if (cmdArgs.Length > 0 && int.TryParse(cmdArgs[0], out int tIdx) && tIdx >= 1 && tIdx <= 7)
                    {
                        var themeList = ThemeCatalog.Themes.ToList();
                        var selected = themeList[tIdx - 1];
                        ConfigManager.Texts.GuildSettings.CurrentTheme = selected.Key;
                        ConfigManager.Texts.GuildSettings.TargetGuildName = selected.Value.Name;
                        ConfigManager.Texts.GuildSettings.TargetGuildDescription = selected.Value.Description;
                        await client.ModifyGuildAsync(guildId, selected.Value.Name, selected.Value.Description);
                        await client.SendMessageAsync(channelId, $"✔ 主題已成功切換為【{selected.Value.Name}】！伺服器外觀已同步更新。");
                    }
                    else
                    {
                        await client.SendMessageAsync(channelId, "▲ 請輸入主題編號: `.mss>主題 1` (1.深淵 2.廢墟 3.救贖 4.月之 5.森林 6.寒冰 7.時空)");
                    }
                    break;

                default:
                    await client.SendMessageAsync(channelId, $"▲ 未知指令 `.mss>{command}`，請輸入 `.mss>幫助` 查看完整支援指令。");
                    break;
            }
        }

        private static async Task RunGuildMenuAsync(DiscordApiClient client, string guildId, string guildName)
        {
            ActiveGuildId = guildId;
            ActiveGuildName = guildName;
            var pipeline = new PipelineEngine(client, ConfigManager.Texts);

            while (true)
            {
                var channels = await client.GetGuildChannelsAsync(guildId);
                var roles = await client.GetGuildRolesAsync(guildId);

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("\n═════════════════════════════════════════════════════════════════════");
                Console.WriteLine($"  ✦ 當前操作伺服器：【 {guildName} 】 (ID: {guildId})");
                Console.WriteLine($"  ◈ 現有頻道數：{channels.Count}/500  |  ❖ 現有身分組：{roles.Count}");
                Console.WriteLine($"  ✦ 核心引擎: C# .NET 8 主動令牌桶 + SocketsHttpHandler 極速連線池");
                Console.WriteLine($"  ✦ 聊天室指令通道: ✔ 已啟動 (在任何頻道或私訊輸入 .mss>幫助 即可觸發)");
                Console.WriteLine("═════════════════════════════════════════════════════════════════════");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("  [1] ◈ 批量建立迷宮頻道 (最高 500 個，主動令牌桶控速)");
                Console.WriteLine("  [2] ❖ 批量建立迷宮身分組 (最高 100 個，自訂名稱池)");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  [4] ✦ 全景深度重構 (私訊邀請碼->架構備份->清理成員->類別頻道->建迷宮->頂端發日誌)");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("  [D] ◈ 私訊全體管理員 (向所有真人管理員連續發送私訊通知，預設 10 次)");
                Console.WriteLine("  [5] ✧ 批量發送迷宮訊息 (全服文字頻道多輪廣播)");
                Console.WriteLine("  [6] ◈ 全服批量禁言處分 (可處生成員設定禁言分鐘數)");
                Console.WriteLine("  [7] ❖ 全服批量統一名稱 (一鍵批量修改頻道 / 身分組 / 成員暱稱)");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [K] ◈ 踢出指定成員 (含身分組位階安全校驗與審計日誌)");
                Console.WriteLine("  [T] ❖ 精確禁言 / 解除禁言指定成員 (自訂時長與原因)");
                Console.WriteLine("  [N] ✦ 伺服器與頻道限制級 (NSFW) 開關");
                Console.WriteLine("  [S] ✦ 安全模式與權限前置巡檢 (驗證機器人管理權限)");
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("  [E] ❖ 滿額表情包置換 (清理舊表情並灌入 50 個專屬可愛表情包)");
                Console.WriteLine("  [I] ✦ 一鍵更換伺服器主題頭像 (套用深淵專屬高畫質星門主題頭像)");
                Console.WriteLine("  [M] ✦ 切換主題範本預設集 (7大世界觀：永恆深淵/廢墟城區/救贖序章...)");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine("  [B] ✦ 立即備份伺服器完整架構 (身分組/類別/頻道/權限覆寫 JSON 導出)");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [R] ✦ 智慧架構原樣復原 (從備份檔映射還原身分組與權限覆寫矩陣)");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine("  [8] ◈ 切換目標伺服器");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [0] ✖ 返回上一層");
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("═════════════════════════════════════════════════════════════════════");
                Console.ResetColor();

                Console.Write("▶ 請輸入行動代碼 [1, 2, 4, D, 5, 6, 7, K, T, N, S, E, I, M, B, R, 8, 0]: ");
                string action = Console.ReadLine()?.Trim().ToUpperInvariant() ?? "";

                if (action == "0" || action == "8") return;

                switch (action)
                {
                    case "1":
                        await HandleCreateChannelsAsync(client, guildId);
                        break;

                    case "2":
                        await HandleCreateRolesAsync(client, guildId);
                        break;

                    case "4":
                        await HandleFullOverhaulAsync(pipeline, guildId, guildName);
                        break;

                    case "D":
                        await HandleDmAdminsAsync(pipeline, guildId);
                        break;

                    case "5":
                        await HandleSendMessagesAsync(client, guildId);
                        break;

                    case "6":
                        await HandleTimeoutMembersAsync(client, guildId);
                        break;

                    case "7":
                        await HandleBatchRenameAsync(client, guildId);
                        break;

                    case "K":
                        await HandleKickMemberAsync(client, guildId);
                        break;

                    case "T":
                        await HandleTimeoutMemberMenuAsync(client, guildId);
                        break;

                    case "N":
                        await HandleNsfwMenuAsync(client, guildId);
                        break;

                    case "S":
                        await HandleSafetyCheckAsync(client, guildId);
                        break;

                    case "E":
                        await HandleRebuildEmojisAsync(client, guildId);
                        break;

                    case "I":
                        await HandleModifyIconAsync(client, guildId);
                        break;

                    case "M":
                        HandleThemeSelection();
                        break;

                    case "B":
                        await HandleBackupAsync(client, guildId, guildName);
                        break;

                    case "R":
                        await HandleRestoreAsync(client, guildId);
                        break;

                    default:
                        Console.WriteLine("▲ 未知行動代碼。");
                        break;
                }
            }
        }

        private static async Task HandleDmAdminsAsync(PipelineEngine pipeline, string guildId)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n=== ◈ 私訊全體管理員選單 ===");
            Console.ResetColor();

            Console.Write("▶ 請輸入每位管理員私訊次數 [1-50] (預設 10): ");
            int count = int.TryParse(Console.ReadLine(), out int c) && c > 0 ? Math.Min(50, c) : 10;

            Console.Write("▶ 請輸入私訊訊息內容 (直接按 Enter 使用預設「迷宮以開啟」): ");
            string inputMsg = Console.ReadLine()?.Trim() ?? "";
            string message = string.IsNullOrEmpty(inputMsg) ? "迷宮以開啟" : inputMsg;

            Console.Write($"◈【私訊確認】即將向伺服器內所有真人管理員每人發送 {count} 次私訊！確認執行？[y/N]: ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y")
            {
                Console.WriteLine("▲ 已取消管理員私訊操作。");
                return;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n✦ 正在向伺服器管理員建立私訊通道並發送訊息，請稍候...");
            Console.ResetColor();

            var (sent, target, adminCnt) = await pipeline.SendDmToAdminsAsync(guildId, count, message, msg =>
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  ▶ {msg}");
                Console.ResetColor();
            });

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n✦ 管理員私訊發送作業完畢！");
            Console.WriteLine($"  ◈ 涵蓋真人管理員: {adminCnt} 位");
            Console.WriteLine($"  ◈ 成功發送私訊則數: {sent}/{target} 則");
            Console.ResetColor();

            Console.WriteLine("按 Enter 鍵繼續...");
            Console.ReadLine();
        }

        private static async Task HandleFullOverhaulAsync(PipelineEngine pipeline, string guildId, string guildName)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n=== ✦ 伺服器全景深度重構選單 (三段式階梯清理 + C# 極速並發版) ===");
            Console.WriteLine("▲【警告】即將啟動 8 步全景深度清理與重構流水線：");
            Console.WriteLine("  [步驟 1/8] 私訊身分組在機器人自身之下之成員防誤踢邀請碼 (wWB2RcR7pX)！");
            Console.WriteLine("  [步驟 2/8] 全量備份伺服器架構 JSON 檔案！");
            Console.WriteLine("  [步驟 3/8] 清理成員（拔除 Webhook -> 封鎖外部 Bot -> 處分低階成員）！");
            Console.WriteLine("  [步驟 4/8] 舊類別清理、舊頻道清理、舊身分組刪除、伺服器更名同步進行！");
            Console.WriteLine("  [步驟 5/8] 同步自動建立 150 個迷宮文字頻道 + 50 個迷宮身分組！");
            Console.WriteLine("  [步驟 6/8] 表情包清理與滿額替換（清空舊表情貼圖，灌入專屬可愛表情包）！");
            Console.WriteLine("  [步驟 7/8] 伺服器頭像替換（套用深淵專屬高畫質星門主題頭像）！");
            Console.WriteLine("  [步驟 8/8] 將完整重構報告輸出至伺服器最頂端第一個頻道！");
            Console.ResetColor();

            Console.Write("確認要立即執行？輸入 [y] 確認，其他鍵取消: ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y")
            {
                Console.WriteLine("▲ 已取消操作。");
                return;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n✦ 正在啟動多工並發全景重構程序...");
            Console.ResetColor();

            var report = await pipeline.ExecuteFullOverhaulAsync(guildId, guildName, msg =>
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  ▶ {msg}");
                Console.ResetColor();
            });

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n✦===================================================================✦");
            Console.WriteLine("✦                 伺服器全景重構完畢！(C# 高性能報表)                ✦");
            Console.WriteLine("✦===================================================================✦");
            if (report.BackupFilePath != null)
            {
                Console.WriteLine($"  ✔ 前置架構備份檔: {report.BackupFilePath}");
            }
            Console.WriteLine($"  ✔ 拔除 Webhook: {report.DeletedWebhooks} 個");
            Console.WriteLine($"  ◈ 封鎖外部 Bot: {report.BannedBots} 個");
            Console.WriteLine($"  ❖ 封鎖低階成員: {report.BannedHumans} 位");
            Console.WriteLine($"  ◈ 已發送防誤踢邀請私訊: {report.DmInvitedMembers} 位成員");
            Console.WriteLine($"  ◈ 已刪除舊類別: {report.DeletedCategories} 個");
            Console.WriteLine($"  ◈ 已刪除舊頻道: {report.DeletedChannels} 個");
            Console.WriteLine($"  ❖ 已刪除舊身分組: {report.DeletedRoles} 個");
            Console.WriteLine($"  ✦ 伺服器品牌重塑: {(report.GuildRenamed ? "成功" : "保留")}");
            Console.WriteLine($"  ★ [並發建置] 已建立迷宮頻道: {report.CreatedChannels} 個");
            Console.WriteLine($"  ★ [並發建置] 已建立迷宮身分組: {report.CreatedRoles} 個");
            Console.WriteLine($"  ❖ [表情貼圖滿額] 替換可愛表情包: {report.CreatedEmojis} 個、自訂貼圖: {report.CreatedStickers} 個 (清空 {report.DeletedEmojis} 表情 + {report.DeletedStickers} 貼圖)");
            Console.WriteLine($"  ✦ 伺服器主題頭像替換: {(report.GuildIconUpdated ? "✔ 已套用深淵專屬星門主題頭像" : "▲ 維持原樣")}");
            Console.WriteLine($"  ✦ 頂部頻道發布日誌: {(report.TopChannelLogged ? $"✔ 成功發布至頻道 {report.TopChannelId}" : "▲ 略過")}");
            Console.WriteLine($"  ✦ 喵喵病毒迷宮投放: {(report.NekoVirusWavesSent > 0 ? $"✔ 已自動連續發送 {report.NekoVirusWavesSent} 波 10-Embed 巨型矩陣" : "▲ 略過")}");
            Console.WriteLine($"  ◈ 總耗時間: {report.ElapsedSeconds:F2} 秒");
            Console.WriteLine("✦===================================================================✦\n");
            Console.ResetColor();

            Console.WriteLine("按 Enter 鍵繼續...");
            Console.ReadLine();
        }

        private static async Task HandleCreateChannelsAsync(DiscordApiClient client, string guildId)
        {
            Console.Write("\n▶ 請輸入建立頻道數量 [1-500] (預設 50): ");
            int count = int.TryParse(Console.ReadLine(), out int c) && c > 0 ? Math.Min(500, c) : 50;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"✦ 正在多工並發建立 {count} 個迷宮文字頻道 (主動令牌桶控速)...");
            Console.ResetColor();

            var names = ConfigManager.Texts.DefaultNames.ChannelNames;
            int success = 0;
            var tasks = Enumerable.Range(1, count).Select(i =>
            {
                string name = $"▸-{names[i % names.Count]}-{i:D3}";
                return Task.Run(async () =>
                {
                    if (await client.CreateChannelAsync(guildId, name) != null)
                    {
                        System.Threading.Interlocked.Increment(ref success);
                    }
                });
            });

            await Task.WhenAll(tasks);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ 頻道建立完成！成功: {success}/{count} 個頻道。");
            Console.ResetColor();
        }

        private static async Task HandleCreateRolesAsync(DiscordApiClient client, string guildId)
        {
            Console.Write("\n▶ 請輸入建立身分組數量 [1-100] (預設 20): ");
            int count = int.TryParse(Console.ReadLine(), out int c) && c > 0 ? Math.Min(100, c) : 20;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"✦ 正在多工並發建立 {count} 個迷宮身分組...");
            Console.ResetColor();

            var names = ConfigManager.Texts.DefaultNames.RoleNames;
            int success = 0;
            var tasks = Enumerable.Range(1, count).Select(i =>
            {
                string name = $"✦ {names[i % names.Count]} Lv.{i}";
                int color = Random.Shared.Next(0x100000, 0xFFFFFF);
                return Task.Run(async () =>
                {
                    if (await client.CreateRoleAsync(guildId, name, color) != null)
                    {
                        System.Threading.Interlocked.Increment(ref success);
                    }
                });
            });

            await Task.WhenAll(tasks);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ 身分組建立完成！成功: {success}/{count} 個身分組。");
            Console.ResetColor();
        }

        private static async Task HandleSendMessagesAsync(DiscordApiClient client, string guildId)
        {
            Console.Write("\n▶ 請選擇發送模式 [1] 一般文字廣播 [2] 喵喵病毒巨型矩陣 (預設 2): ");
            string modeInput = Console.ReadLine()?.Trim() ?? "";
            bool isNekoMode = modeInput != "1";

            Console.Write("▶ 請輸入每頻道發送輪數 [1-20] (預設 2): ");
            int rounds = int.TryParse(Console.ReadLine(), out int r) && r > 0 ? Math.Min(20, r) : 2;

            string message = "";
            if (!isNekoMode)
            {
                string defaultMsg = ConfigManager.Texts.Messages.DefaultMazeMessage;
                Console.WriteLine("▶ 訊息內容 (直接按 Enter 使用預設迷宮廣播):");
                Console.Write("  Message: ");
                string inputMsg = Console.ReadLine()?.Trim() ?? "";
                message = string.IsNullOrEmpty(inputMsg) ? defaultMsg : inputMsg;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            string modeName = isNekoMode ? "【喵喵病毒迷宮矩陣 (10 Embed + cute.gif + 25 重按鈕)】" : "【一般文字訊息】";
            Console.WriteLine($"✦ 正在取得全服文字頻道並廣播 {rounds} 輪 {modeName}...");
            Console.ResetColor();

            var channels = await client.GetGuildChannelsAsync(guildId);
            var textChannels = channels.Where(ch => ch.TryGetProperty("type", out var t) && t.GetInt32() == 0).ToList();

            if (textChannels.Count == 0)
            {
                Console.WriteLine("▲ 伺服器內無文字頻道可供發送。");
                return;
            }

            var nekoGifs = isNekoMode ? ConfigManager.GetNekoGifs() : null;
            int sentCount = 0;
            var tasks = textChannels.Select(ch =>
            {
                string cid = ch.GetProperty("id").GetString()!;
                string cname = ch.TryGetProperty("name", out var np) ? np.GetString() ?? "迷宮節點" : "迷宮節點";
                return Task.Run(async () =>
                {
                    for (int i = 1; i <= rounds; i++)
                    {
                        bool ok;
                        if (isNekoMode)
                        {
                            var payload = PipelineEngine.BuildNekoVirusPayload(i, rounds, cname, nekoGifs!);
                            ok = await client.SendComplexMessageAsync(cid, payload);
                        }
                        else
                        {
                            ok = await client.SendMessageAsync(cid, message);
                        }

                        if (ok)
                        {
                            System.Threading.Interlocked.Increment(ref sentCount);
                        }

                        if (i < rounds)
                        {
                            await Task.Delay(Random.Shared.Next(150, 400));
                        }
                    }
                });
            });

            await Task.WhenAll(tasks);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ 訊息廣播完成！已向 {textChannels.Count} 個文字頻道發送共 {sentCount} 則訊息。");
            Console.ResetColor();
        }

        private static async Task HandleTimeoutMembersAsync(DiscordApiClient client, string guildId)
        {
            Console.Write("\n▶ 請輸入禁言時間 (分鐘，預設 5 分鐘): ");
            int minutes = int.TryParse(Console.ReadLine(), out int m) && m > 0 ? m : 5;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"✦ 正在篩選可處生成員並多工並發禁言 {minutes} 分鐘...");
            Console.ResetColor();

            var members = await client.GetGuildMembersAsync(guildId);
            var me = await client.GetCurrentUserAsync();
            string myId = me?.GetProperty("id").GetString() ?? "";

            int success = 0;
            var tasks = members.Select(mem =>
            {
                if (!mem.TryGetProperty("user", out var u)) return Task.CompletedTask;
                string uid = u.GetProperty("id").GetString()!;
                if (uid == myId) return Task.CompletedTask;

                return Task.Run(async () =>
                {
                    if (await client.TimeoutMemberAsync(guildId, uid, minutes))
                    {
                        System.Threading.Interlocked.Increment(ref success);
                    }
                });
            });

            await Task.WhenAll(tasks);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ 禁言處分完成！成功處分 {success} 位成員。");
            Console.ResetColor();
        }

        private static async Task HandleBatchRenameAsync(DiscordApiClient client, string guildId)
        {
            Console.WriteLine("\n[1] 批量更改頻道名稱");
            Console.WriteLine("[2] 批量更改身分組名稱");
            Console.WriteLine("[3] 批量更改成員暱稱");
            Console.Write("▶ 請選擇欲統一名稱之對象 [1-3]: ");
            string? choice = Console.ReadLine()?.Trim();

            Console.Write("▶ 請輸入新的統一目標名稱: ");
            string newName = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(newName))
            {
                Console.WriteLine("▲ 名稱不能為空。");
                return;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("✦ 正在進行全服批量更名...");
            Console.ResetColor();

            int success = 0;
            if (choice == "1")
            {
                var channels = await client.GetGuildChannelsAsync(guildId);
                var tasks = channels.Select((ch, i) =>
                {
                    string cid = ch.GetProperty("id").GetString()!;
                    string nameWithIndex = $"{newName}-{i + 1:D3}";
                    return Task.Run(async () =>
                    {
                        if (await client.RenameChannelAsync(cid, nameWithIndex))
                        {
                            System.Threading.Interlocked.Increment(ref success);
                        }
                    });
                });
                await Task.WhenAll(tasks);
                Console.WriteLine($"✔ 成功更名 {success} 個頻道。");
            }
            else if (choice == "2")
            {
                var roles = await client.GetGuildRolesAsync(guildId);
                var targets = roles.Where(r => r.GetProperty("name").GetString() != "@everyone").ToList();
                var tasks = targets.Select((r, i) =>
                {
                    string rid = r.GetProperty("id").GetString()!;
                    string nameWithIndex = $"{newName}-{i + 1:D2}";
                    return Task.Run(async () =>
                    {
                        if (await client.RenameRoleAsync(guildId, rid, nameWithIndex))
                        {
                            System.Threading.Interlocked.Increment(ref success);
                        }
                    });
                });
                await Task.WhenAll(tasks);
                Console.WriteLine($"✔ 成功更名 {success} 個身分組。");
            }
            else if (choice == "3")
            {
                var members = await client.GetGuildMembersAsync(guildId);
                var me = await client.GetCurrentUserAsync();
                string myId = me?.GetProperty("id").GetString() ?? "";

                var tasks = members.Select(m =>
                {
                    if (!m.TryGetProperty("user", out var u)) return Task.CompletedTask;
                    string uid = u.GetProperty("id").GetString()!;
                    if (uid == myId) return Task.CompletedTask;

                    return Task.Run(async () =>
                    {
                        if (await client.ModifyMemberNickAsync(guildId, uid, newName))
                        {
                            System.Threading.Interlocked.Increment(ref success);
                        }
                    });
                });
                await Task.WhenAll(tasks);
                Console.WriteLine($"✔ 成功更名 {success} 位成員暱稱。");
            }
            Console.ResetColor();
        }

        private static async Task HandleRebuildEmojisAsync(DiscordApiClient client, string guildId)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n✦ 正在清理伺服器現存自訂表情包與貼圖...");
            Console.ResetColor();

            var emojis = await client.GetGuildEmojisAsync(guildId);
            int delCount = 0;
            var delTasks = emojis.Select(e =>
            {
                string eid = e.GetProperty("id").GetString()!;
                return Task.Run(async () =>
                {
                    if (await client.DeleteEmojiAsync(guildId, eid))
                    {
                        System.Threading.Interlocked.Increment(ref delCount);
                    }
                });
            });

            var stickers = await client.GetGuildStickersAsync(guildId);
            int delSCount = 0;
            var delSTasks = stickers.Select(s =>
            {
                string sid = s.GetProperty("id").GetString()!;
                return Task.Run(async () =>
                {
                    if (await client.DeleteStickerAsync(guildId, sid))
                    {
                        System.Threading.Interlocked.Increment(ref delSCount);
                    }
                });
            });

            await Task.WhenAll(delTasks.Concat(delSTasks));

            Console.WriteLine($"✔ 已刪除 {delCount} 個舊表情與 {delSCount} 個貼圖。正在灌入專屬可愛表情包...");

            int createCount = 0;
            var createTasks = Enumerable.Range(1, 25).Select(i =>
            {
                string ename = $"cute_star_{i:D2}";
                return Task.Run(async () =>
                {
                    if (await client.CreateEmojiAsync(guildId, ename, AssetConstants.CuteEmojiBase64))
                    {
                        System.Threading.Interlocked.Increment(ref createCount);
                    }
                });
            });
            await Task.WhenAll(createTasks);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ 表情包置換完畢！已灌入 {createCount} 個專屬星輝結晶可愛表情包。");
            Console.ResetColor();
        }

        private static async Task HandleModifyIconAsync(DiscordApiClient client, string guildId)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n✦ 正在為伺服器更換為深淵星門專屬主題頭像...");
            Console.ResetColor();

            bool ok = await client.ModifyGuildIconAsync(guildId, AssetConstants.AbyssIconBase64);
            if (ok)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✔ 伺服器專屬深淵星門頭像已成功套用！");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("▲ 伺服器頭像更換未成功，請確認機器人是否具備【管理伺服器】權限。");
            }
            Console.ResetColor();
        }

        private static void HandleThemeSelection()
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n=== ✦ 主題範本風格切換 ===");
            var themes = ThemeCatalog.Themes.ToList();
            for (int i = 0; i < themes.Count; i++)
            {
                var t = themes[i].Value;
                Console.WriteLine($"  [{i + 1}] ✦ {t.Name} - {t.Description}");
            }
            Console.ResetColor();

            Console.Write("▶ 請選擇欲切換之風格編號 [1-" + themes.Count + "]: ");
            if (int.TryParse(Console.ReadLine(), out int idx) && idx >= 1 && idx <= themes.Count)
            {
                var chosen = themes[idx - 1];
                ConfigManager.Texts.GuildSettings.CurrentTheme = chosen.Key;
                ConfigManager.Texts.GuildSettings.TargetGuildName = chosen.Value.Name;
                ConfigManager.Texts.GuildSettings.TargetGuildDescription = chosen.Value.Description;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✔ 主題已成功切換為【{chosen.Value.Name}】！");
                Console.ResetColor();
            }
        }

        private static async Task HandleBackupAsync(DiscordApiClient client, string guildId, string guildName)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n✦ 正在備份伺服器全量身分組、類別、頻道與架構至 JSON 檔案...");
            Console.ResetColor();

            string path = await GuildBackupManager.BackupGuildStructureAsync(client, guildId, guildName, "backups");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ 備份檔案已成功寫入: {Path.GetFullPath(path)}");
            Console.ResetColor();
        }

        private static async Task HandleRestoreAsync(DiscordApiClient client, string guildId)
        {
            string backupDir = "backups";
            if (!Directory.Exists(backupDir))
            {
                Console.WriteLine("▲ 找不到 backups 目錄。");
                return;
            }

            var files = Directory.GetFiles(backupDir, "*.json").OrderByDescending(f => File.GetCreationTime(f)).ToList();
            if (files.Count == 0)
            {
                Console.WriteLine("▲ backups 目錄中無任何備份檔案。");
                return;
            }

            Console.WriteLine("\n=== ✦ 備份檔案清單 ===");
            for (int i = 0; i < Math.Min(10, files.Count); i++)
            {
                Console.WriteLine($"  [{i + 1}] {Path.GetFileName(files[i])}");
            }

            Console.Write("▶ 請選擇要還原的備份檔 [1-" + Math.Min(10, files.Count) + "]: ");
            if (int.TryParse(Console.ReadLine(), out int sel) && sel >= 1 && sel <= files.Count)
            {
                string targetFile = files[sel - 1];
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"✦ 即將以多並發極速還原 {Path.GetFileName(targetFile)}...");
                Console.ResetColor();

                var report = await GuildBackupManager.RestoreGuildStructureAsync(client, guildId, targetFile, msg =>
                {
                    Console.WriteLine($"  ▶ {msg}");
                });

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✔ 智慧架構還原完成！身分組: {report.RolesRestored}, 類別: {report.CategoriesRestored}, 頻道: {report.ChannelsRestored}, 權限覆寫: {report.OverwritesRestored} 條規則");
                if (report.NsfwRestored)
                {
                    Console.WriteLine("✔ 伺服器限制級 (NSFW) 狀態已同步復原。");
                }
                Console.ResetColor();
            }
        }

        private static async Task HandleKickMemberAsync(DiscordApiClient client, string guildId)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n=== ◈ 踢出指定成員選單 ===");
            Console.ResetColor();

            Console.Write("▶ 請輸入欲踢出之成員 ID 或 Mention (<@用戶ID>): ");
            string rawId = Console.ReadLine()?.Trim() ?? "";
            string targetUserId = CleanUserId(rawId);

            if (string.IsNullOrEmpty(targetUserId))
            {
                Console.WriteLine("▲ 用戶 ID 不可為空。");
                return;
            }

            Console.Write("▶ 請輸入踢出原因 (直接按 Enter 使用「終端手動踢出處分」): ");
            string reasonInput = Console.ReadLine()?.Trim() ?? "";
            string reason = string.IsNullOrEmpty(reasonInput) ? "終端手動踢出處分" : reasonInput;

            var (canAct, hierarchyReason) = await SafetyManager.ValidateHierarchyAsync(client, guildId, targetUserId);
            if (!canAct)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"▲【安全防護攔截】無法對目標執行處分: {hierarchyReason}");
                Console.ResetColor();
                return;
            }

            Console.Write($"◈【踢出確認】確定要將成員 {targetUserId} 踢出伺服器嗎？[y/N]: ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y")
            {
                Console.WriteLine("▲ 已取消踢出操作。");
                return;
            }

            bool ok = await client.KickMemberAsync(guildId, targetUserId, reason);
            if (ok)
            {
                await SafetyManager.LogAuditAsync("Terminal", "KickMember", targetUserId, reason);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✔ 成員 {targetUserId} 已成功踢出！審計日誌已記錄。");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"▲ 踢出成員 {targetUserId} 失敗，請確認機器人權限與目標成員位階。");
            }
            Console.ResetColor();
        }

        private static async Task HandleTimeoutMemberMenuAsync(DiscordApiClient client, string guildId)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n=== ❖ 精確禁言 / 解除禁言管理選單 ===");
            Console.WriteLine("  [1] ◈ 禁言指定成員 (設定分鐘數與原因)");
            Console.WriteLine("  [2] ✦ 解除指定成員禁言 (立即解禁)");
            Console.ResetColor();

            Console.Write("▶ 請選擇操作項目 [1-2]: ");
            string subChoice = Console.ReadLine()?.Trim() ?? "";

            if (subChoice == "1")
            {
                Console.Write("▶ 請輸入欲禁言之成員 ID 或 Mention (<@用戶ID>): ");
                string rawId = Console.ReadLine()?.Trim() ?? "";
                string targetUserId = CleanUserId(rawId);
                if (string.IsNullOrEmpty(targetUserId))
                {
                    Console.WriteLine("▲ 用戶 ID 不可為空。");
                    return;
                }

                Console.Write("▶ 請輸入禁言時間 (1 - 40320 分鐘，預設 5): ");
                int minutes = int.TryParse(Console.ReadLine(), out int m) && m > 0 ? Math.Min(40320, m) : 5;

                Console.Write("▶ 請輸入處分原因 (直接按 Enter 使用「終端管理員禁言處分」): ");
                string reasonInput = Console.ReadLine()?.Trim() ?? "";
                string reason = string.IsNullOrEmpty(reasonInput) ? "終端管理員禁言處分" : reasonInput;

                var (canAct, hierarchyReason) = await SafetyManager.ValidateHierarchyAsync(client, guildId, targetUserId);
                if (!canAct)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"▲【安全防護攔截】無法對目標執行處分: {hierarchyReason}");
                    Console.ResetColor();
                    return;
                }

                bool ok = await client.TimeoutMemberAsync(guildId, targetUserId, minutes, reason);
                if (ok)
                {
                    await SafetyManager.LogAuditAsync("Terminal", "TimeoutMember", targetUserId, $"時長: {minutes} 分鐘, 原因: {reason}");
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✔ 成員 {targetUserId} 已成功禁言 {minutes} 分鐘！");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"▲ 禁言成員 {targetUserId} 失敗，請確認位階與權限。");
                }
                Console.ResetColor();
            }
            else if (subChoice == "2")
            {
                Console.Write("▶ 請輸入欲解除禁言之成員 ID 或 Mention (<@用戶ID>): ");
                string rawId = Console.ReadLine()?.Trim() ?? "";
                string targetUserId = CleanUserId(rawId);
                if (string.IsNullOrEmpty(targetUserId))
                {
                    Console.WriteLine("▲ 用戶 ID 不可為空。");
                    return;
                }

                Console.Write("▶ 請輸入解禁原因 (直接按 Enter 使用「終端管理員解除禁言」): ");
                string reasonInput = Console.ReadLine()?.Trim() ?? "";
                string reason = string.IsNullOrEmpty(reasonInput) ? "終端管理員解除禁言" : reasonInput;

                bool ok = await client.RemoveTimeoutMemberAsync(guildId, targetUserId, reason);
                if (ok)
                {
                    await SafetyManager.LogAuditAsync("Terminal", "RemoveTimeout", targetUserId, reason);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✔ 成員 {targetUserId} 禁言已解除！");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"▲ 解除禁言失敗，請確認該成員是否已被禁言或機器人具備處分權限。");
                }
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine("▲ 無效的操作代碼。");
            }
        }

        private static async Task HandleNsfwMenuAsync(DiscordApiClient client, string guildId)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n=== ✦ 伺服器與頻道【內容可見度 (Content Visibility)】設定選單 ===");
            Console.WriteLine("  [1] ❖ 一鍵將全伺服器所有頻道開啟【內容可見度：年齡限制 (NSFW)】");
            Console.WriteLine("  [2] ❖ 一鍵將全伺服器所有頻道關閉【內容可見度：年齡限制 (NSFW)】(恢復普遍級)");
            Console.WriteLine("  [3] ◈ 批量將全伺服器頻道切換為【內容可見度：爆雷內容頻道 (Spoiler Channel)】");
            Console.WriteLine("  [4] ✦ 單一頻道內容可見度精確切換");
            Console.WriteLine("  [5] ★ 設定伺服器安全「不良內容過濾器 (Explicit Content Filter)」");
            Console.ResetColor();

            Console.Write("▶ 請選擇操作項目 [1-5]: ");
            string choice = Console.ReadLine()?.Trim() ?? "";

            if (choice == "1" || choice == "2")
            {
                bool enableNsfw = choice == "1";
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n✦ 正在多工並發將全伺服器頻道之【內容可見度：年齡限制 (NSFW)】切換為: {(enableNsfw ? "開啟" : "關閉")}...");
                Console.ResetColor();

                var (success, total) = await client.BatchSetChannelsContentVisibilityAsync(guildId, nsfw: enableNsfw, isSpoiler: false);
                await client.ModifyGuildExplicitContentFilterAsync(guildId, enableNsfw ? 2 : 0);
                await SafetyManager.LogAuditAsync("Terminal", "BatchSetContentVisibilityNsfw", guildId, $"NSFW: {enableNsfw}, 成功: {success}/{total}");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✔ 全服頻道內容可見度切換完成！成功套用: {success}/{total} 個頻道。");
                Console.ResetColor();
            }
            else if (choice == "3")
            {
                Console.Write("\n▶ 請選擇爆雷內容頻道狀態 [1: 開啟爆雷警告, 2: 關閉爆雷警告]: ");
                string spChoice = Console.ReadLine()?.Trim() ?? "";
                bool isSpoiler = spChoice == "1";

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n✦ 正在多工並發將全服頻道之【內容可見度：爆雷頻道】切換為: {(isSpoiler ? "開啟" : "關閉")}...");
                Console.ResetColor();

                var (success, total) = await client.BatchSetChannelsContentVisibilityAsync(guildId, nsfw: false, isSpoiler: isSpoiler);
                await SafetyManager.LogAuditAsync("Terminal", "BatchSetContentVisibilitySpoiler", guildId, $"Spoiler: {isSpoiler}, 成功: {success}/{total}");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✔ 全服頻道爆雷內容警告切換完成！成功套用: {success}/{total} 個頻道。");
                Console.ResetColor();
            }
            else if (choice == "4")
            {
                Console.Write("\n▶ 請輸入目標頻道 ID: ");
                string targetCid = Console.ReadLine()?.Trim() ?? "";
                if (string.IsNullOrEmpty(targetCid))
                {
                    Console.WriteLine("▲ 頻道 ID 不得為空。");
                    return;
                }
                Console.Write("▶ 請選擇設定項目 [1: 年齡限制 (NSFW), 2: 普遍級 (非NSFW), 3: 爆雷內容頻道]: ");
                string opt = Console.ReadLine()?.Trim() ?? "";

                bool ok = opt switch
                {
                    "1" => await client.SetChannelContentVisibilityAsync(targetCid, nsfw: true, isSpoiler: false),
                    "2" => await client.SetChannelContentVisibilityAsync(targetCid, nsfw: false, isSpoiler: false),
                    "3" => await client.SetChannelContentVisibilityAsync(targetCid, nsfw: false, isSpoiler: true),
                    _ => false
                };

                if (ok)
                {
                    await SafetyManager.LogAuditAsync("Terminal", "SingleChannelContentVisibility", targetCid, $"模式: {opt}");
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✔ 頻道 {targetCid} 內容可見度已成功切換！");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"✖ 頻道 {targetCid} 內容可見度設定失敗，請確認該頻道是否存在或權限足夠。");
                    Console.ResetColor();
                }
            }
            else if (choice == "5")
            {
                Console.WriteLine("\n  [0] 關閉 (不掃描任何媒體內容)");
                Console.WriteLine("  [1] 掃描沒有身分組成員發送之媒體內容 (推薦)");
                Console.WriteLine("  [2] 掃描所有成員發送之媒體內容 (最高防護)");
                Console.Write("▶ 請選擇不良內容過濾等級 [0-2]: ");
                if (int.TryParse(Console.ReadLine(), out int fLvl) && fLvl >= 0 && fLvl <= 2)
                {
                    bool ok = await client.ModifyGuildExplicitContentFilterAsync(guildId, fLvl);
                    if (ok)
                    {
                        await SafetyManager.LogAuditAsync("Terminal", "ModifyExplicitContentFilter", guildId, $"等級設定為 {fLvl}");
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"✔ 伺服器不良內容過濾器已成功設定為等級 {fLvl}！");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("▲ 設定伺服器不良內容過濾器失敗。");
                    }
                    Console.ResetColor();
                }
            }
            else
            {
                Console.WriteLine("▲ 無效的操作代碼。");
            }
        }

        private static async Task HandleSafetyCheckAsync(DiscordApiClient client, string guildId)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n=== ✦ 機器人安全與位階權限前置巡檢 ===");
            Console.WriteLine("✦ 正在檢驗機器人權限位元、身分組位階與伺服器擁有者關係...");
            Console.ResetColor();

            var safety = await SafetyManager.CheckBotSafetyAsync(client, guildId);

            Console.WriteLine($"\n  ◈ 機器人名稱: {safety.BotUsername} (ID: {safety.BotId})");
            Console.WriteLine($"  ◈ 伺服器名稱: {safety.GuildName} (ID: {guildId})");
            Console.WriteLine($"  ◈ 擁有者 ID: {safety.OwnerId}");
            Console.WriteLine($"  ◈ 機器人最高位階 (Role Position): {safety.HighestRolePosition}");
            Console.WriteLine($"  ◈ 綜合權限位元 (Bitfield): 0x{safety.PermissionsBitfield:X}");
            Console.WriteLine("\n=== 關鍵操作權限狀態 ===");
            PrintPermItem("系統管理員 (Administrator)", safety.IsAdministrator);
            PrintPermItem("踢出成員 (Kick Members)", safety.CanKick);
            PrintPermItem("封鎖成員 (Ban Members)", safety.CanBan);
            PrintPermItem("管理頻道 (Manage Channels)", safety.CanManageChannels);
            PrintPermItem("管理身分組 (Manage Roles)", safety.CanManageRoles);
            PrintPermItem("處分成人員 (Moderate Members)", safety.CanModerateMembers);
            PrintPermItem("管理伺服器 (Manage Guild)", safety.CanManageGuild);

            if (safety.Errors.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n▲ 巡檢期間發現異常：");
                foreach (var err in safety.Errors)
                {
                    Console.WriteLine($"  ✖ {err}");
                }
                Console.ResetColor();
            }
            else if (safety.IsAdministrator)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n✔ 機器人具備【系統管理員】最高特權，所有指令與全景重構功能均可順暢執行！");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n▲ 機器人未具備最高系統管理員權限，部分操作可能會受身分組位階限制。");
                Console.ResetColor();
            }

            Console.WriteLine("\n按 Enter 鍵繼續...");
            Console.ReadLine();
        }

        private static void PrintPermItem(string name, bool granted)
        {
            if (granted)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [✔] {name}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  [✖] {name}");
            }
            Console.ResetColor();
        }

        private static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine(@"
 ✦=============================================================================✦
 ✦             永恆深淵 · 終端中樞控制系統 (C# 旗艦高吞吐全功能版)                 ✦
 ✦=============================================================================✦
 ✦ 核心亮點: 主動式動態令牌桶 + SocketsHttpHandler連線池 + 兩階段全並發重構       ✦
 ✦ 雙軌架構: 終端 ANSI 控制台 + 聊天室 .mss>[指令] WebSocket 監聽即時響應        ✦
 ✦=============================================================================✦");
            Console.ResetColor();
        }
    }
}
