using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AbyssCore
{
    /// <summary>
    /// Discord API v10 高性能 REST 用戶端 (旗艦全功能版)
    /// 特性:
    /// 1. SocketsHttpHandler 連線池 (長效復用、防 Socket Exhaustion)
    /// 2. 主動式動態令牌桶 (Proactive Token Bucket) 智慧預判防護
    /// 3. 自適應 429 RateLimit 雙層退避 (Retry-After + Jitter)
    /// 4. SemaphoreSlim 微秒級並發排程
    /// 5. 支援批量表情/貼圖置換、頻道/身分組/成員更名、迷宮多輪訊息廣播
    /// </summary>
    public sealed class DiscordApiClient : IDisposable
    {
        private const string BaseApiUrl = "https://discord.com/api/v10";
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _concurrencyLimiter;
        private readonly string _botToken;

        private readonly ConcurrentDictionary<string, (int Remaining, DateTime ResetAtUtc)> _bucketLimits = new();
        private readonly ConcurrentDictionary<string, string> _routeToBucketMap = new();

        public DiscordApiClient(string botToken, int maxConcurrency = 30)
        {
            _botToken = botToken.Trim();
            _concurrencyLimiter = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                MaxConnectionsPerServer = 200,
                EnableMultipleHttp2Connections = true,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(5.0)
            };

            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(15.0)
            };

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", _botToken);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "DiscordBot (https://github.com/AbyssCore, v3.2)");
        }

        private static string GetRouteKey(HttpMethod method, string endpoint)
        {
            string clean = Regex.Replace(endpoint, @"/(channels|guilds|roles|members|emojis|stickers)/\d+", "/$1/{id}");
            return $"{method.Method}:{clean}";
        }

        private async Task PreCheckRateLimitAsync(HttpMethod method, string endpoint)
        {
            string routeKey = GetRouteKey(method, endpoint);
            if (_routeToBucketMap.TryGetValue(routeKey, out string? bucketId) &&
                _bucketLimits.TryGetValue(bucketId, out var state))
            {
                if (state.Remaining <= 0)
                {
                    var delay = state.ResetAtUtc - DateTime.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(20, 80)));
                    }
                }
            }
        }

        private void UpdateBucketHeaders(HttpMethod method, string endpoint, HttpResponseHeaders headers)
        {
            if (headers.TryGetValues("X-RateLimit-Bucket", out var bVals) &&
                headers.TryGetValues("X-RateLimit-Remaining", out var remVals) &&
                headers.TryGetValues("X-RateLimit-Reset-After", out var rstVals))
            {
                string? bucketId = bVals.FirstOrDefault();
                string? remStr = remVals.FirstOrDefault();
                string? rstStr = rstVals.FirstOrDefault();

                if (!string.IsNullOrEmpty(bucketId) &&
                    int.TryParse(remStr, out int rem) &&
                    double.TryParse(rstStr, out double rstSec))
                {
                    string routeKey = GetRouteKey(method, endpoint);
                    _routeToBucketMap[routeKey] = bucketId;
                    DateTime resetAt = DateTime.UtcNow.AddSeconds(rstSec);
                    _bucketLimits[bucketId] = (rem, resetAt);
                }
            }
        }

        public async Task<HttpResponseMessage?> SafeRequestAsync(
            HttpMethod method,
            string endpoint,
            object? jsonBody = null,
            int maxRetries = 5,
            string? auditReason = null)
        {
            await PreCheckRateLimitAsync(method, endpoint);

            await _concurrencyLimiter.WaitAsync();
            try
            {
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    using var request = new HttpRequestMessage(method, $"{BaseApiUrl}/{endpoint.TrimStart('/')}");
                    if (!string.IsNullOrEmpty(auditReason))
                    {
                        request.Headers.Add("X-Audit-Log-Reason", Uri.EscapeDataString(auditReason));
                    }
                    if (jsonBody != null)
                    {
                        string json = JsonSerializer.Serialize(jsonBody);
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    }

                    try
                    {
                        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                        UpdateBucketHeaders(method, endpoint, response.Headers);

                        if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            double retryAfterSeconds = 1.0;
                            if (response.Headers.TryGetValues("Retry-After", out var values))
                            {
                                foreach (var val in values)
                                {
                                    if (double.TryParse(val, out double parsedSec))
                                    {
                                        retryAfterSeconds = parsedSec;
                                        break;
                                    }
                                }
                            }
                            response.Dispose();
                            int waitMs = (int)(retryAfterSeconds * 1000) + Random.Shared.Next(100, 300);
                            await Task.Delay(waitMs);
                            continue;
                        }

                        return response;
                    }
                    catch (HttpRequestException) when (attempt < maxRetries - 1)
                    {
                        await Task.Delay(400 * (attempt + 1));
                    }
                    catch (TaskCanceledException) when (attempt < maxRetries - 1)
                    {
                        await Task.Delay(400 * (attempt + 1));
                    }
                }
                return null;
            }
            finally
            {
                _concurrencyLimiter.Release();
            }
        }

        #region --- 基礎資訊 API ---

        public async Task<JsonElement?> GetCurrentUserAsync()
        {
            using var resp = await SafeRequestAsync(HttpMethod.Get, "/users/@me");
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                return JsonDocument.Parse(json).RootElement.Clone();
            }
            return null;
        }

        public async Task<JsonElement?> GetGuildAsync(string guildId)
        {
            using var resp = await SafeRequestAsync(HttpMethod.Get, $"/guilds/{guildId}");
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                return JsonDocument.Parse(json).RootElement.Clone();
            }
            return null;
        }

        public async Task<JsonElement?> GetGuildMemberAsync(string guildId, string userId)
        {
            using var resp = await SafeRequestAsync(HttpMethod.Get, $"/guilds/{guildId}/members/{userId}");
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                return JsonDocument.Parse(json).RootElement.Clone();
            }
            return null;
        }

        public async Task<List<JsonElement>> GetCurrentUserGuildsAsync()
        {
            var list = new List<JsonElement>();
            using var resp = await SafeRequestAsync(HttpMethod.Get, "/users/@me/guilds");
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(el.Clone());
                }
            }
            return list;
        }

        public async Task<List<JsonElement>> GetGuildMembersAsync(string guildId, int limit = 1000)
        {
            var list = new List<JsonElement>();
            using var resp = await SafeRequestAsync(HttpMethod.Get, $"/guilds/{guildId}/members?limit={limit}");
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(el.Clone());
                }
            }
            return list;
        }

        #endregion

        #region --- Webhooks 與整合 API ---

        public async Task<List<JsonElement>> GetGuildWebhooksAsync(string guildId)
        {
            var list = new List<JsonElement>();
            using var resp = await SafeRequestAsync(HttpMethod.Get, $"/guilds/{guildId}/webhooks");
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(el.Clone());
                }
            }
            return list;
        }

        public async Task<bool> DeleteWebhookAsync(string webhookId)
        {
            using var resp = await SafeRequestAsync(HttpMethod.Delete, $"/webhooks/{webhookId}");
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<int> ClearAllWebhooksAsync(string guildId)
        {
            var webhooks = await GetGuildWebhooksAsync(guildId);
            if (webhooks.Count == 0) return 0;

            int deleted = 0;
            var tasks = webhooks.Select(wh =>
            {
                string wid = wh.GetProperty("id").GetString()!;
                return Task.Run(async () =>
                {
                    if (await DeleteWebhookAsync(wid))
                    {
                        Interlocked.Increment(ref deleted);
                    }
                });
            });
            await Task.WhenAll(tasks);
            return deleted;
        }

        #endregion

        #region --- 伺服器頻道 API ---

        public async Task<List<JsonElement>> GetGuildChannelsAsync(string guildId)
        {
            var list = new List<JsonElement>();
            using var resp = await SafeRequestAsync(HttpMethod.Get, $"/guilds/{guildId}/channels");
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(el.Clone());
                }
            }
            return list;
        }

        public async Task<bool> DeleteChannelAsync(string channelId)
        {
            using var resp = await SafeRequestAsync(HttpMethod.Delete, $"/channels/{channelId}");
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<string?> CreateChannelAsync(
            string guildId,
            string name,
            int type = 0,
            int? position = null,
            string? parentId = null,
            object? permissionOverwrites = null,
            string? topic = null,
            bool? nsfw = null)
        {
            var payload = new Dictionary<string, object>
            {
                ["name"] = name,
                ["type"] = type
            };
            if (position.HasValue) payload["position"] = position.Value;
            if (!string.IsNullOrEmpty(parentId)) payload["parent_id"] = parentId;
            if (permissionOverwrites != null) payload["permission_overwrites"] = permissionOverwrites;
            if (!string.IsNullOrEmpty(topic)) payload["topic"] = topic;
            if (nsfw.HasValue) payload["nsfw"] = nsfw.Value;

            using var resp = await SafeRequestAsync(HttpMethod.Post, $"/guilds/{guildId}/channels", payload);
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString();
                }
            }
            return null;
        }

        public async Task<bool> SetChannelNsfwAsync(string channelId, bool nsfw)
        {
            var payload = new { nsfw };
            using var resp = await SafeRequestAsync(HttpMethod.Patch, $"/channels/{channelId}", payload);
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<bool> RenameChannelAsync(string channelId, string newName)
        {
            var payload = new { name = newName };
            using var resp = await SafeRequestAsync(HttpMethod.Patch, $"/channels/{channelId}", payload);
            return resp != null && resp.IsSuccessStatusCode;
        }

        #endregion

        #region --- 身分組 API ---

        public async Task<List<JsonElement>> GetGuildRolesAsync(string guildId)
        {
            var list = new List<JsonElement>();
            using var resp = await SafeRequestAsync(HttpMethod.Get, $"/guilds/{guildId}/roles");
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(el.Clone());
                }
            }
            return list;
        }

        public async Task<bool> DeleteRoleAsync(string guildId, string roleId)
        {
            using var resp = await SafeRequestAsync(HttpMethod.Delete, $"/guilds/{guildId}/roles/{roleId}");
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<string?> CreateRoleAsync(
            string guildId,
            string name,
            int color = 0,
            string? permissions = null,
            bool hoist = false,
            bool mentionable = false)
        {
            var payload = new Dictionary<string, object>
            {
                ["name"] = name,
                ["color"] = color,
                ["hoist"] = hoist,
                ["mentionable"] = mentionable
            };
            if (!string.IsNullOrEmpty(permissions))
            {
                payload["permissions"] = permissions;
            }

            using var resp = await SafeRequestAsync(HttpMethod.Post, $"/guilds/{guildId}/roles", payload);
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString();
                }
            }
            return null;
        }

        public async Task<bool> RenameRoleAsync(string guildId, string roleId, string newName)
        {
            var payload = new { name = newName };
            using var resp = await SafeRequestAsync(HttpMethod.Patch, $"/guilds/{guildId}/roles/{roleId}", payload);
            return resp != null && resp.IsSuccessStatusCode;
        }

        #endregion

        #region --- 表情包 (Emojis) 與貼圖 (Stickers) API ---

        public async Task<List<JsonElement>> GetGuildEmojisAsync(string guildId)
        {
            var list = new List<JsonElement>();
            using var resp = await SafeRequestAsync(HttpMethod.Get, $"/guilds/{guildId}/emojis");
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(el.Clone());
                }
            }
            return list;
        }

        public async Task<bool> DeleteEmojiAsync(string guildId, string emojiId)
        {
            using var resp = await SafeRequestAsync(HttpMethod.Delete, $"/guilds/{guildId}/emojis/{emojiId}");
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<bool> CreateEmojiAsync(string guildId, string name, string imageBase64DataUri)
        {
            var payload = new
            {
                name = name,
                image = imageBase64DataUri
            };
            using var resp = await SafeRequestAsync(HttpMethod.Post, $"/guilds/{guildId}/emojis", payload);
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<List<JsonElement>> GetGuildStickersAsync(string guildId)
        {
            var list = new List<JsonElement>();
            using var resp = await SafeRequestAsync(HttpMethod.Get, $"/guilds/{guildId}/stickers");
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(el.Clone());
                }
            }
            return list;
        }

        public async Task<bool> DeleteStickerAsync(string guildId, string stickerId)
        {
            using var resp = await SafeRequestAsync(HttpMethod.Delete, $"/guilds/{guildId}/stickers/{stickerId}");
            return resp != null && resp.IsSuccessStatusCode;
        }

        #endregion

        #region --- 伺服器設定與品牌更換 ---

        public async Task<bool> ModifyGuildAsync(string guildId, string name, string? description = null, string? iconB64 = null)
        {
            var payload = new Dictionary<string, object?>
            {
                ["name"] = name
            };
            if (description != null) payload["description"] = description;
            if (iconB64 != null) payload["icon"] = iconB64;

            using var resp = await SafeRequestAsync(HttpMethod.Patch, $"/guilds/{guildId}", payload);
            if (resp != null && resp.IsSuccessStatusCode)
            {
                return true;
            }

            using var fallbackResp = await SafeRequestAsync(HttpMethod.Patch, $"/guilds/{guildId}", new { name });
            return fallbackResp != null && fallbackResp.IsSuccessStatusCode;
        }

        public async Task<bool> ModifyGuildIconAsync(string guildId, string iconB64)
        {
            var payload = new { icon = iconB64 };
            using var resp = await SafeRequestAsync(HttpMethod.Patch, $"/guilds/{guildId}", payload);
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<bool> ModifyGuildNsfwLevelAsync(string guildId, int nsfwLevel)
        {
            var payload = new { nsfw_level = nsfwLevel };
            using var resp = await SafeRequestAsync(HttpMethod.Patch, $"/guilds/{guildId}", payload);
            return resp != null && resp.IsSuccessStatusCode;
        }

        #endregion

        #region --- 成員操作與私訊 ---

        public async Task<bool> TimeoutMemberAsync(string guildId, string userId, int minutes, string? reason = null)
        {
            DateTime until = DateTime.UtcNow.AddMinutes(minutes);
            var payload = new { communication_disabled_until = until.ToString("O") };
            using var resp = await SafeRequestAsync(HttpMethod.Patch, $"/guilds/{guildId}/members/{userId}", payload, auditReason: reason);
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<bool> RemoveTimeoutMemberAsync(string guildId, string userId, string? reason = null)
        {
            var payload = new Dictionary<string, object?>
            {
                ["communication_disabled_until"] = null
            };
            using var resp = await SafeRequestAsync(HttpMethod.Patch, $"/guilds/{guildId}/members/{userId}", payload, auditReason: reason);
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<bool> KickMemberAsync(string guildId, string userId, string? reason = null)
        {
            using var resp = await SafeRequestAsync(HttpMethod.Delete, $"/guilds/{guildId}/members/{userId}", null, auditReason: reason);
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<bool> ModifyMemberNickAsync(string guildId, string userId, string nick)
        {
            var payload = new { nick };
            using var resp = await SafeRequestAsync(HttpMethod.Patch, $"/guilds/{guildId}/members/{userId}", payload);
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<bool> BanMemberAsync(string guildId, string userId, string reason = "深淵階梯清理")
        {
            var payload = new { delete_message_seconds = 0 };
            using var resp = await SafeRequestAsync(HttpMethod.Put, $"/guilds/{guildId}/bans/{userId}", payload, auditReason: reason);
            return resp != null && resp.IsSuccessStatusCode;
        }

        public async Task<string?> CreateDmChannelAsync(string recipientId)
        {
            var payload = new { recipient_id = recipientId };
            using var resp = await SafeRequestAsync(HttpMethod.Post, "/users/@me/channels", payload);
            if (resp != null && resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString();
                }
            }
            return null;
        }

        public async Task<bool> SendMessageAsync(string channelId, string content)
        {
            var payload = new { content };
            using var resp = await SafeRequestAsync(HttpMethod.Post, $"/channels/{channelId}/messages", payload);
            return resp != null && resp.IsSuccessStatusCode;
        }

        #endregion

        public void Dispose()
        {
            _concurrencyLimiter.Dispose();
            _httpClient.Dispose();
        }
    }
}
