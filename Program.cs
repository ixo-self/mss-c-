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
                                     "◈ `.mss>清理` 啟動全服全景深度重構 (私訊邀請碼->架構備份->清理成員->重塑類別頻道身分組->建迷宮->頂部頻道發布日誌)\n" +
                                     "◈ `.mss>私訊 [次數] [內容]` 批量向全體管理員連續發送私訊通知 (預設 10 次)\n" +
                                     "◈ `.mss>頻道 [數量]` 批量建立迷宮頻道 (預設 50，最高 500)\n" +
                                     "◈ `.mss>身分組 [數量]` 批量建立迷宮身分組 (預設 20，最高 100)\n" +
                                     "◈ `.mss>訊息 [輪數] [內容]` 批量向文字頻道廣播迷宮提示\n" +
                                     "◈ `.mss>禁言 [分鐘]` 批量禁言低於機器人身分組之成員\n" +
                                     "◈ `.mss>更名 [目標名稱]` 批量修改頻道名稱\n" +
                                     "◈ `.mss>備份` 立即備份伺服器架構並存檔\n" +
                                     "◈ `.mss>主題 [1-7]` 切換世界觀主題 (1.深淵 2.廢墟 3.救贖 4.月之 5.森林 6.寒冰 7.時空)";
                    await client.SendMessageAsync(channelId, helpMsg);
                    break;

                case "清理":
                case "重構":
                case "overhaul":
                    await client.SendMessageAsync(channelId, "✦ 正在透過 C# 旗艦引擎啟動【全景深度重構】流水線（私訊邀請碼 -> 架構備份 -> 拔除 Webhook -> 封鎖外部 Bot -> 處分低階成員 -> 刪除類別頻道身分組 -> 建立 150 頻道 + 50 身分組 -> 頂部頻道發布日誌）...");
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
                                       $"• 已發送防誤踢邀請碼 (wWB2RcR7pX) 私訊: `{report.DmInvitedMembers}` 位成員\n" +
                                       $"• 拔除 Webhook: `{report.DeletedWebhooks}` 個\n" +
                                       $"• 刪除舊身分組: `{report.DeletedRoles}` 個\n" +
                                       $"★ 新建迷宮頻道: `{report.CreatedChannels}` 個\n" +
                                       $"★ 新建迷宮身分組: `{report.CreatedRoles}` 個\n" +
                                       $"◈ 頂部日誌輸出: {(report.TopChannelLogged ? $"✔ 已寫入最頂端頻道 (ID: {report.TopChannelId})" : "▲ 未能寫入")}\n" +
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

                case "禁言":
                case "timeout":
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
                            if (await client.TimeoutMemberAsync(guildId, uid, mins))
                            {
                                System.Threading.Interlocked.Increment(ref timeoutOk);
                            }
                        });
                    });
                    await Task.WhenAll(tTasks);
                    await client.SendMessageAsync(channelId, $"✔ 禁言處分完成！共處分 `{timeoutOk}` 位成員。");
                    break;

                case "備份":
                case "backup":
                    await client.SendMessageAsync(channelId, "✦ 正在全量序列化伺服器身分組、頻道與排序至 JSON 檔案...");
                    string bFile = await GuildBackupManager.BackupGuildStructureAsync(client, guildId, "Guild", "backups");
                    await client.SendMessageAsync(channelId, $"✔ 伺服器架構已成功備份至本地: `{Path.GetFileName(bFile)}`");
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
                Console.WriteLine("  [4] ✦ 全景深度重構 (私訊邀請碼->架構備份->清理成員->重塑類別頻道->建迷宮->頂端發日誌)");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("  [D] ◈ 私訊全體管理員 (向所有真人管理員連續發送私訊通知，預設 10 次)");
                Console.WriteLine("  [5] ✧ 批量發送迷宮訊息 (全服文字頻道多輪廣播)");
                Console.WriteLine("  [6] ◈ 全服批量禁言處分 (可處生成員設定禁言分鐘數)");
                Console.WriteLine("  [7] ❖ 全服批量統一名稱 (一鍵批量修改頻道 / 身分組 / 成員暱稱)");
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("  [E] ❖ 滿額表情包置換 (清理舊表情並灌入 50 個專屬可愛表情包)");
                Console.WriteLine("  [M] ✦ 切換主題範本預設集 (7大世界觀：永恆深淵/廢墟城區/救贖序章...)");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine("  [B] ✦ 立即備份伺服器完整架構 (身分組/頻道/排序 JSON 導出)");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [R] ✦ 一鍵架構復原 (從本地 backups/ 備份檔多併發極速還原)");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine("  [8] ◈ 切換目標伺服器");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [0] ✖ 返回上一層");
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("═════════════════════════════════════════════════════════════════════");
                Console.ResetColor();

                Console.Write("▶ 請輸入行動代碼 [1, 2, 4, D, 5, 6, 7, E, M, B, R, 8, 0]: ");
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

                    case "E":
                        await HandleRebuildEmojisAsync(client, guildId);
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
            Console.WriteLine("▲【警告】即將啟動三段式階梯清理與兩階段建置流水線：");
            Console.WriteLine("  [步驟 1/6] 私訊身分組在機器人自身之下之成員防誤踢邀請碼 (wWB2RcR7pX)！");
            Console.WriteLine("  [步驟 2/6] 全量備份伺服器架構 JSON 檔案！");
            Console.WriteLine("  [步驟 3/6] 清理成員（拔除 Webhook -> 封鎖外部 Bot -> 處分低階成員）！");
            Console.WriteLine("  [步驟 4/6] 舊類別清理、舊頻道清理、舊身分組刪除、伺服器更名同步進行！");
            Console.WriteLine("  [步驟 5/6] 同步自動建立 150 個迷宮文字頻道 + 50 個迷宮身分組！");
            Console.WriteLine("  [步驟 6/6] 將重構報告輸出至伺服器最頂端第一個頻道！");
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
            Console.WriteLine($"  ✦ 頂部頻道發布日誌: {(report.TopChannelLogged ? $"✔ 成功發布至頻道 {report.TopChannelId}" : "▲ 略過")}");
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
            Console.Write("\n▶ 請輸入每頻道發送輪數 [1-20] (預設 2): ");
            int rounds = int.TryParse(Console.ReadLine(), out int r) && r > 0 ? Math.Min(20, r) : 2;

            string defaultMsg = ConfigManager.Texts.Messages.DefaultMazeMessage;
            Console.WriteLine($"▶ 訊息內容 (直接按 Enter 使用預設迷宮廣播):");
            Console.Write("  Message: ");
            string inputMsg = Console.ReadLine()?.Trim() ?? "";
            string message = string.IsNullOrEmpty(inputMsg) ? defaultMsg : inputMsg;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"✦ 正在取得全服文字頻道並廣播 {rounds} 輪訊息...");
            Console.ResetColor();

            var channels = await client.GetGuildChannelsAsync(guildId);
            var textChannels = channels.Where(ch => ch.TryGetProperty("type", out var t) && t.GetInt32() == 0).ToList();

            if (textChannels.Count == 0)
            {
                Console.WriteLine("▲ 伺服器內無文字頻道可供發送。");
                return;
            }

            int sentCount = 0;
            var tasks = textChannels.Select(ch =>
            {
                string cid = ch.GetProperty("id").GetString()!;
                return Task.Run(async () =>
                {
                    for (int i = 0; i < rounds; i++)
                    {
                        if (await client.SendMessageAsync(cid, message))
                        {
                            System.Threading.Interlocked.Increment(ref sentCount);
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
            Console.WriteLine("\n✦ 正在清理伺服器現存自訂表情包...");
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
            await Task.WhenAll(delTasks);

            Console.WriteLine($"✔ 已刪除 {delCount} 個舊表情包。正在準備填滿專屬表情包...");

            string cutePngBase64 = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

            int createCount = 0;
            var createTasks = Enumerable.Range(1, 25).Select(i =>
            {
                string ename = $"cute_star_{i:D2}";
                return Task.Run(async () =>
                {
                    if (await client.CreateEmojiAsync(guildId, ename, cutePngBase64))
                    {
                        System.Threading.Interlocked.Increment(ref createCount);
                    }
                });
            });
            await Task.WhenAll(createTasks);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ 表情包置換完畢！已灌入 {createCount} 個專屬表情包。");
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

                var (roles, chs) = await GuildBackupManager.RestoreGuildStructureAsync(client, guildId, targetFile, msg =>
                {
                    Console.WriteLine($"  ▶ {msg}");
                });

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✔ 還原作業完成！成功還原: {roles} 個身分組，{chs} 個頻道。");
                Console.ResetColor();
            }
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
