using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AbyssCore
{
    /// <summary>
    /// Discord Gateway (WebSocket v10) 輕量監聽器
    /// 核心功能:
    /// 1. 維持獨立心跳循環 (Heartbeat Loop)，防止斷線 (Session Invalidation)
    /// 2. 監聽 MESSAGE_CREATE 事件，攔截 .mss>[指令] 聊天室文字指令
    /// 3. 非同步分派至指令處理器，完全不阻塞 Gateway 接收線程
    /// </summary>
    public sealed class DiscordGatewayClient : IDisposable
    {
        private const string GatewayUrl = "wss://gateway.discord.gg/?v=10&encoding=json";
        private readonly string _botToken;
        private readonly Func<string, string, string, string, Task> _messageHandler;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cts;
        private Task? _listenTask;
        private Task? _heartbeatTask;
        private int? _lastSequenceNumber;
        private string? _sessionId;
        private string? _botUserId;

        public bool IsConnected => _webSocket != null && _webSocket.State == WebSocketState.Open;

        public DiscordGatewayClient(string botToken, Func<string, string, string, string, Task> messageHandler)
        {
            _botToken = botToken;
            _messageHandler = messageHandler;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _webSocket = new ClientWebSocket();

            try
            {
                await _webSocket.ConnectAsync(new Uri(GatewayUrl), _cts.Token);
                _listenTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Gateway 連線失敗: {ex.Message}");
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            var ms = new MemoryStream();

            while (!ct.IsCancellationRequested && _webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                try
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    string jsonStr = Encoding.UTF8.GetString(ms.ToArray());
                    using var doc = JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;

                    int op = root.GetProperty("op").GetInt32();
                    if (root.TryGetProperty("s", out var sProp) && sProp.ValueKind == JsonValueKind.Number)
                    {
                        _lastSequenceNumber = sProp.GetInt32();
                    }

                    switch (op)
                    {
                        case 10: // Hello
                            int interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(interval, ct));
                            await SendIdentifyAsync();
                            break;

                        case 11: // Heartbeat ACK
                            break;

                        case 0: // Dispatch Event
                            string? eventType = root.GetProperty("t").GetString();
                            if (eventType == "READY")
                            {
                                var d = root.GetProperty("d");
                                _sessionId = d.GetProperty("session_id").GetString();
                                _botUserId = d.GetProperty("user").GetProperty("id").GetString();
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"✔ [Gateway] 聊天室指令通道已就緒！監聽前綴: .mss> (Bot ID: {_botUserId})");
                                Console.ResetColor();
                            }
                            else if (eventType == "MESSAGE_CREATE")
                            {
                                HandleMessageCreate(root.GetProperty("d"));
                            }
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Console.WriteLine($"▲ [Gateway] 封包解析異常: {ex.Message}");
                    }
                }
            }
        }

        private async Task HeartbeatLoopAsync(int intervalMs, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                try
                {
                    await Task.Delay(intervalMs, ct);
                    var heartbeatMsg = new
                    {
                        op = 1,
                        d = _lastSequenceNumber
                    };
                    string json = JsonSerializer.Serialize(heartbeatMsg);
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                    {
                        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task SendIdentifyAsync()
        {
            // 請求 GUILDS(1), GUILD_MEMBERS(2), GUILD_MESSAGES(512), DIRECT_MESSAGES(4096), MESSAGE_CONTENT(32768) = 37379
            var identifyPayload = new
            {
                op = 2,
                d = new
                {
                    token = _botToken,
                    intents = 37379,
                    properties = new
                    {
                        os = "windows",
                        browser = "AbyssCore_CSharp",
                        device = "AbyssCore_CSharp"
                    }
                }
            };

            string json = JsonSerializer.Serialize(identifyPayload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        private void HandleMessageCreate(JsonElement d)
        {
            try
            {
                if (d.TryGetProperty("author", out var authorProp))
                {
                    if (authorProp.TryGetProperty("bot", out var bProp) && bProp.GetBoolean())
                    {
                        return; // 忽略所有機器人發言
                    }
                }

                string content = d.GetProperty("content").GetString() ?? "";
                string channelId = d.GetProperty("channel_id").GetString() ?? "";
                string guildId = d.TryGetProperty("guild_id", out var gProp) ? gProp.GetString() ?? "" : "";
                string authorName = authorProp.GetProperty("username").GetString() ?? "User";

                if (string.IsNullOrWhiteSpace(content)) return;

                // 辨識 .mss> 指令前綴 (支援大小寫與空白容錯)
                string trimmed = content.Trim();
                const string prefix = ".mss>";

                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string commandWithArgs = trimmed.Substring(prefix.Length).Trim();
                    // 非同步分派執行，絕對不阻塞 Gateway 封包接收
                    _ = Task.Run(() => _messageHandler(guildId, channelId, authorName, commandWithArgs));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] 訊息分派錯誤: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _webSocket?.Dispose();
            _cts?.Dispose();
        }
    }
}
