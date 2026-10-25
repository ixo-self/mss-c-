using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// 全域動態令牌桶 (Global Token Bucket Rate Limiter)
    /// 嚴格確保全專案所有 REST 請求總速率維持在 Discord 規範之 45 req/sec 上限內，消除全域 429 衝撞
    /// </summary>
    public sealed class GlobalRateLimiter
    {
        private readonly double _rate;
        private readonly double _capacity;
        private double _tokens;
        private long _lastUpdateTicks;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public GlobalRateLimiter(double requestsPerSecond = 45.0)
        {
            _rate = requestsPerSecond;
            _capacity = requestsPerSecond;
            _tokens = requestsPerSecond;
            _lastUpdateTicks = Stopwatch.GetTimestamp();
        }

        public async Task AcquireAsync()
        {
            while (true)
            {
                int sleepMs = 0;
                await _lock.WaitAsync();
                try
                {
                    long nowTicks = Stopwatch.GetTimestamp();
                    double elapsed = (double)(nowTicks - _lastUpdateTicks) / Stopwatch.Frequency;
                    _lastUpdateTicks = nowTicks;

                    _tokens = Math.Min(_capacity, _tokens + elapsed * _rate);

                    if (_tokens >= 1.0)
                    {
                        _tokens -= 1.0;
                        return;
                    }

                    double waitSec = (1.0 - _tokens) / _rate;
                    sleepMs = Math.Max(1, (int)(waitSec * 1000));
                }
                finally
                {
                    _lock.Release();
                }

                if (sleepMs > 0)
                {
                    await Task.Delay(sleepMs);
                }
            }
        }
    }

    /// <summary>
    /// Discord API v10 高性能 REST 用戶端 (旗艦全功能版)
    /// 特性:
    /// 1. SocketsHttpHandler 連線池 (長效復用、防 Socket Exhaustion)
    /// 2. GlobalRateLimiter 全域動態令牌桶 (45 req/s 完美壓制，杜絕全域 429)
    /// 3. 每路由獨立動態令牌桶與本地預扣 (Local Token Reservation): 各 API 路由隔離，防併發群發崩潰
    /// 4. 自適應 429 RateLimit 雙層退避與精確 JSON retry_after 解析
    /// 5. 記憶體零分配 (Zero-Allocation UTF-8 Stream): 直接送出 ByteArrayContent，避免 GC 負擔
    /// 6. 支援巨型複合訊息 (SendComplexMessageAsync: 10 嵌入式 Embed + 25 重按鈕矩陣)
    /// </summary>
    public sealed class DiscordApiClient : IDisposable
    {
        private const string BaseApiUrl = "https://discord.com/api/v10";
        private readonly HttpClient _httpClient;
        private readonly string _botToken;
        private readonly GlobalRateLimiter _globalLimiter = new(45.0);
        private readonly int _maxRouteConcurrency;

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _routeSemaphores = new();
        private readonly ConcurrentDictionary<string, (int Remaining, DateTime ResetAtUtc)> _bucketLimits = new();
        private readonly ConcurrentDictionary<string, string> _routeToBucketMap = new();
        private static long _globalResetTicks = 0;
        private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json") { CharSet = "utf-8" };

        public DiscordApiClient(string botToken, int maxConcurrency = 30)
        {
            _botToken = botToken.Trim();
            _maxRouteConcurrency = maxConcurrency > 0 ? maxConcurrency : 30;

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                MaxConnectionsPerServer = 250,
                EnableMultipleHttp2Connections = true,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(5.0)
            };

            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(15.0)
            };

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", _botToken);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "DiscordBot (https://github.com/AbyssCore, v4.0-AOT)");
        }

        private static string GetRouteKey(HttpMethod method, string endpoint)
        {
            // 頻道訊息為 Major Parameter Route，每個頻道擁有獨立 Bucket，互不阻塞
            var msgMatch = Regex.Match(endpoint, @"^/channels/(\d+)/messages");
            if (msgMatch.Success)
            {
                return $"POST:/channels/{msgMatch.Groups[1].Value}/messages";
            }

            string clean = Regex.Replace(endpoint, @"/(channels|guilds|roles|members|emojis|stickers)/\d+", "/$1/{id}");
            return $"{method.Method}:{clean}";
        }

        private SemaphoreSlim GetRouteSemaphore(string routeKey)
        {
            return _routeSemaphores.GetOrAdd(routeKey, _ => new SemaphoreSlim(_maxRouteConcurrency, _maxRouteConcurrency));
        }

        private async Task PreCheckRateLimitAsync(string routeKey)
        {
            // 1. 檢查全域限速標記 (Global Rate Limit)
            long globalTicks = Interlocked.Read(ref _globalResetTicks);
            if (globalTicks > 0)
            {
                DateTime globalResetAt = new DateTime(globalTicks, DateTimeKind.Utc);
                var globalDelay = globalResetAt - DateTime.UtcNow;
                if (globalDelay > TimeSpan.Zero)
                {
                    await Task.Delay(globalDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(20, 60)));
                }
            }

            // 2. 路由 Bucket 狀態與本地預扣 (Local Token Reservation)
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
                else
                {
                    // 本地預扣額度，防止多協程同時發射觸發 429
                    _bucketLimits[bucketId] = (state.Remaining - 1, state.ResetAtUtc);
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
            int maxRetries = 10,
            string? auditReason = null)
        {
            string routeKey = GetRouteKey(method, endpoint);
            var routeSemaphore = GetRouteSemaphore(routeKey);

            byte[]? bodyBytes = null;
            if (jsonBody != null)
            {
                if (jsonBody is byte[] rawBytes)
                {
                    bodyBytes = rawBytes;
                }
                else
                {
                    bodyBytes = JsonSerializer.SerializeToUtf8Bytes(jsonBody);
                }
            }

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                // 1. 路由 Bucket 預檢與預扣 (若該路由正在冷卻，先在此等待冷卻結束)
                await PreCheckRateLimitAsync(routeKey);

                // 2. 全域速率限制器：精確控速於 45 req/s 內
                await _globalLimiter.AcquireAsync();

                await routeSemaphore.WaitAsync();
                try
                {
                    using var request = new HttpRequestMessage(method, $"{BaseApiUrl}/{endpoint.TrimStart('/')}");
                    if (!string.IsNullOrEmpty(auditReason))
                    {
                        request.Headers.Add("X-Audit-Log-Reason", Uri.EscapeDataString(auditReason));
                    }
                    if (bodyBytes != null)
                    {
                        var content = new ByteArrayContent(bodyBytes);
                        content.Headers.ContentType = JsonMediaType;
                        request.Content = content;
                    }

                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    UpdateBucketHeaders(method, endpoint, response.Headers);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        double retryAfterSeconds = 1.0;
                        try
                        {
                            string respText = await response.Content.ReadAsStringAsync();
                            if (!string.IsNullOrEmpty(respText))
                            {
                                using var doc = JsonDocument.Parse(respText);
                                if (doc.RootElement.TryGetProperty("retry_after", out var raProp))
                                {
                                    retryAfterSeconds = raProp.GetDouble();
                                }
                                if (doc.RootElement.TryGetProperty("global", out var gProp) && gProp.GetBoolean())
                                {
                                    DateTime gReset = DateTime.UtcNow.AddSeconds(retryAfterSeconds);
                                    Interlocked.Exchange(ref _globalResetTicks, gReset.Ticks);
                                }
                            }
                        }
                        catch
                        {
                            if (response.Headers.TryGetValues("Retry-After", out var values) &&
                                double.TryParse(values.FirstOrDefault(), out double parsedSec))
                            {
                                retryAfterSeconds = parsedSec;
                            }
                        }

                        // 立即更新此 Bucket 的冷卻狀態，阻擋所有並發請求繼續發射
                        if (_routeToBucketMap.TryGetValue(routeKey, out string? bId))
                        {
                            _bucketLimits[bId] = (0, DateTime.UtcNow.AddSeconds(retryAfterSeconds));
                        }
                        else
                        {
                            // 若尚未取得 bucketId，直接以 routeKey 記錄冷卻，杜絕並發群發衝撞
                            _routeToBucketMap[routeKey] = routeKey;
                            _bucketLimits[routeKey] = (0, DateTime.UtcNow.AddSeconds(retryAfterSeconds));
                        }

                        response.Dispose();
                        // 關鍵優化: 睡眠退避前立即釋放路由信號槽，避免阻塞排隊請求
                        routeSemaphore.Release();

                        int waitMs = Math.Max(50, (int)(retryAfterSeconds * 1000)) + Random.Shared.Next(30, 100);
                        await Task.Delay(waitMs);
                        continue;
                    }

                    routeSemaphore.Release();
                    return response;
                }
                catch (HttpRequestException) when (attempt < maxRetries - 1)
                {
                    routeSemaphore.Release();
                    await Task.Delay(300 * (attempt + 1));
                }
                catch (TaskCanceledException) when (attempt < maxRetries - 1)
                {
                    routeSemaphore.Release();
                    await Task.Delay(300 * (attempt + 1));
                }
                catch
                {
                    routeSemaphore.Release();
                    throw;
                }
            }

            return null;
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

        private static byte[]? _cachedStickerBytes = null;

        public async Task<byte[]?> GetStickerBytesFromUrlAsync(string url)
        {
            if (_cachedStickerBytes != null && _cachedStickerBytes.Length > 0)
            {
                return _cachedStickerBytes;
            }
            try
            {
                using var wc = new HttpClient();
                wc.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                _cachedStickerBytes = await wc.GetByteArrayAsync(url);
                return _cachedStickerBytes;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> CreateStickerAsync(string guildId, string name, byte[] fileBytes, string description = "深淵專屬可愛貼圖", string tags = "cute")
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await _globalLimiter.AcquireAsync();
                var routeSemaphore = GetRouteSemaphore("POST:/guilds/{id}/stickers");
                await routeSemaphore.WaitAsync();
                try
                {
                    using var form = new MultipartFormDataContent();
                    form.Add(new StringContent(name.Length > 30 ? name.Substring(0, 30) : name), "name");
                    form.Add(new StringContent(description.Length > 100 ? description.Substring(0, 100) : description), "description");
                    form.Add(new StringContent(tags.Length > 200 ? tags.Substring(0, 200) : tags), "tags");

                    var byteContent = new ByteArrayContent(fileBytes);
                    byteContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                    form.Add(byteContent, "file", "sticker.png");

                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseApiUrl}/guilds/{guildId}/stickers")
                    {
                        Content = form
                    };

                    using var resp = await _httpClient.SendAsync(request);
                    if (resp.IsSuccessStatusCode) return true;
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        await Task.Delay(1000 + Random.Shared.Next(100, 500));
                        continue;
                    }
                    return false;
                }
                catch
                {
                    if (attempt == 2) return false;
                    await Task.Delay(500);
                }
                finally
                {
                    routeSemaphore.Release();
                }
            }
            return false;
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

        public async Task<bool> SendComplexMessageAsync(string channelId, object payload)
        {
            using var resp = await SafeRequestAsync(HttpMethod.Post, $"/channels/{channelId}/messages", payload);
            return resp != null && resp.IsSuccessStatusCode;
        }

        #endregion

        public void Dispose()
        {
            foreach (var sem in _routeSemaphores.Values)
            {
                sem.Dispose();
            }
            _httpClient.Dispose();
        }
    }
}
