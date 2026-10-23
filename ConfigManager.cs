using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AbyssCore
{
    /// <summary>
    /// 設定檔管理器 (支援 config.json 與 texts.json 動態載入)
    /// </summary>
    public static class ConfigManager
    {
        public static BotConfig Config { get; private set; } = new();
        public static TextsConfig Texts { get; private set; } = new();

        public static void Load(string basePath)
        {
            string configPath = Path.Combine(basePath, "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    Config = JsonSerializer.Deserialize<BotConfig>(json) ?? new();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] 讀取 config.json 失敗: {ex.Message}");
                }
            }

            string textsPath = Path.Combine(basePath, "texts.json");
            if (File.Exists(textsPath))
            {
                try
                {
                    string json = File.ReadAllText(textsPath);
                    Texts = JsonSerializer.Deserialize<TextsConfig>(json) ?? new();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] 讀取 texts.json 失敗: {ex.Message}");
                }
            }
        }
    }

    public class BotConfig
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
    }

    public class TextsConfig
    {
        [JsonPropertyName("guild_settings")]
        public GuildSettings GuildSettings { get; set; } = new();

        [JsonPropertyName("default_values")]
        public DefaultValues DefaultValues { get; set; } = new();

        [JsonPropertyName("default_names")]
        public DefaultNames DefaultNames { get; set; } = new();

        [JsonPropertyName("messages")]
        public MessagesConfig Messages { get; set; } = new();
    }

    public class GuildSettings
    {
        [JsonPropertyName("target_guild_name")]
        public string TargetGuildName { get; set; } = "永恆深淵";

        [JsonPropertyName("target_guild_description")]
        public string TargetGuildDescription { get; set; } = "✦ 終焉星門與虛空星輝 · 永恆深淵試煉迷宮 ◈ 踏入無盡迴廊，尋找真實之光";

        [JsonPropertyName("current_theme")]
        public string CurrentTheme { get; set; } = "abyss";

        [JsonPropertyName("cleanup_rename_name")]
        public string CleanupRenameName { get; set; } = "深淵探索者";

        [JsonPropertyName("top_reset_channel_name")]
        public string TopResetChannelName { get; set; } = "重製伺服器";

        [JsonPropertyName("rules_channel_name")]
        public string RulesChannelName { get; set; } = "▸-深淵守則-核心規章";

        [JsonPropertyName("entrance_channel_name")]
        public string EntranceChannelName { get; set; } = "▸-深淵入口-星輝大廳";

        [JsonPropertyName("restore_channel_name")]
        public string RestoreChannelName { get; set; } = "◈-災難復原中心";
    }

    public class DefaultValues
    {
        [JsonPropertyName("cleanup_channel_limit")]
        public int CleanupChannelLimit { get; set; } = 100;

        [JsonPropertyName("cleanup_timeout_minutes")]
        public int CleanupTimeoutMinutes { get; set; } = 5;

        [JsonPropertyName("admin_dm_count")]
        public int AdminDmCount { get; set; } = 10;

        [JsonPropertyName("cleanup_create_channels")]
        public int CleanupCreateChannels { get; set; } = 150;

        [JsonPropertyName("cleanup_create_roles")]
        public int CleanupCreateRoles { get; set; } = 50;

        [JsonPropertyName("channel_count")]
        public int ChannelCount { get; set; } = 50;

        [JsonPropertyName("role_count")]
        public int RoleCount { get; set; } = 10;
    }

    public class DefaultNames
    {
        [JsonPropertyName("channel_names")]
        public List<string> ChannelNames { get; set; } = new()
        {
            "星輝之境", "深淵走廊", "迷宮入口", "星光迴廊", "幽暗裂隙", "幻境試煉", "記憶迴廊", "遺忘深淵"
        };

        [JsonPropertyName("role_names")]
        public List<string> RoleNames { get; set; } = new()
        {
            "星輝行者", "深淵探索者", "迷宮學者", "星之守衛", "破曉追尋者", "暗夜行者", "虛空漫步者"
        };
    }

    public class MessagesConfig
    {
        [JsonPropertyName("admin_dm_message")]
        public string AdminDmMessage { get; set; } = "迷宮以開啟";

        [JsonPropertyName("default_maze_message")]
        public string DefaultMazeMessage { get; set; } = "✦ **【永恆深淵 · 迷宮試煉通道】**\n◈ 迷宮節點已喚醒，幽暗的星輝在四周流轉...\n◈ *請在此領取對應深淵身分組以解鎖後續通道並前進！*";
    }

    /// <summary>
    /// 主題範本庫 (7大世界觀)
    /// </summary>
    public static class ThemeCatalog
    {
        public static readonly Dictionary<string, ThemeInfo> Themes = new()
        {
            ["abyss"] = new ThemeInfo("永恆深淵", "✦ 終焉星門與虛空星輝 · 永恆深淵試煉迷宮 ◈ 踏入無盡迴廊，尋找真實之光", "#0a0a14", "#7b2cbf"),
            ["ruins"] = new ThemeInfo("廢墟城區", "✦ 廢土瓦礫與生化隔離區 · 終焉末世拾荒生存前哨 ◈ 保持警惕，活下去是唯一真理", "#1a1a1a", "#d97706"),
            ["redemption"] = new ThemeInfo("救贖序章", "✦ 黎明契約與真理聖殿 · 神聖救贖審判巡禮 ◈ 願晨曦之光永照你心，真理之火不滅", "#0f172a", "#38bdf8"),
            ["moon"] = new ThemeInfo("月之領域", "✦ 銀夜宮廷與星月潮汐 · 神秘幽雅夜宴迷宮 ◈ 清冷月光庇護心靈，在黑夜中永不迷途", "#0b0c10", "#c084fc"),
            ["forest"] = new ThemeInfo("永恆森林", "✦ 遠古秘境與翡翠世界樹 · 自然生靈德魯伊迷徑 ◈ 自然之靈伴行，綠蔭庇護探索之路", "#064e3b", "#10b981"),
            ["ice"] = new ThemeInfo("寒冰死域", "✦ 永凍絕境與絕對零度 · 極地暴風雪破冰求生 ◈ 抱持熱血，破除堅冰，在極寒中生還", "#082f49", "#06b6d4"),
            ["time"] = new ThemeInfo("時空片段", "✦ 崩壞輪迴與莫比烏斯環 · 維度躍遷因果修復中樞 ◈ 時間無始無終，輪迴即是永恆", "#18181b", "#a855f7")
        };
    }

    public record ThemeInfo(string Name, string Description, string BgColor, string PrimaryColor);
}
