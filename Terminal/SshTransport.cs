using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using SwellSSH.Models;
using SwellSSH.Services;

namespace SwellSSH.Terminal
{
    public delegate void SshDataReceivedHandler(ReadOnlySpan<byte> data);

    /// <summary>
    /// Wraps SSH.NET's SshClient + ShellStream lifecycle.
    /// - Connects with password or private key (auto-detected)
    /// - Opens an interactive shell with terminal type "xterm-256color"
    /// - Fires DataReceived for every chunk of output from the server
    /// - Supports resize via ResizeTerminal()
    /// - Reconnects automatically on disconnect (exponential back-off, max 3 retries)
    /// </summary>
    public sealed class SshTransport : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired on the thread-pool whenever the server sends output bytes.</summary>
        public event SshDataReceivedHandler? DataReceived;

        /// <summary>Fired when the connection drops unexpectedly.</summary>
        public event Action<Exception?>? Disconnected;

        // ── State ─────────────────────────────────────────────────────────────

        public bool IsConnected => _client?.IsConnected == true && _shell != null;
        public ConnectionProfile? Profile { get; private set; }

        /// <summary>
        /// Optional async callback invoked before the SSH handshake completes.
        /// Parameters: host, port, algorithm, hex-fingerprint.
        /// Return true to trust, false to reject (throws SshConnectionException).
        /// </summary>
        public Func<string, int, string, string, Task<bool>>? HostKeyVerifier { get; set; }

        private SshClient? _client;
        private ShellStream? _shell;
        private CancellationTokenSource? _readCts;
        private Task? _readTask;
        private CancellationTokenSource? _writeCts;
        private Channel<byte[]>? _writeChannel;
        private readonly object _writeSync = new();
        private int _cols = 80;
        private int _rows = 24;

        // ── Connect ───────────────────────────────────────────────────────────

        /// <summary>
        /// Establishes SSH connection and opens an interactive shell.
        /// Throws SshAuthenticationException / SocketException on failure — caller should show InfoBar.
        /// </summary>
        public async Task ConnectAsync(ConnectionProfile profile, int cols = 120, int rows = 30)
        {
            Profile = profile;
            _cols = cols;
            _rows = rows;

            await Task.Run(() =>
            {
                // Agent auth: the named pipe must remain open during Connect()
                // because the agent performs the signing challenge-response.
                // We dispose it in a finally block after Connect() completes.
                System.IO.Pipes.NamedPipeClientStream? agentPipe = null;

                try
                {
                    if (profile.AuthType == "Agent")
                    {
                        agentPipe = SshAgentService.OpenAgentPipe(3000);

                        var identities = SshAgentService.RequestIdentities(agentPipe);
                        if (identities.Count == 0)
                            throw new InvalidOperationException(
                                "SSH Agent 中没有可用的密钥。\n" +
                                "请先用 ssh-add 命令将私钥加入 Agent，再重试。");

                        // Each identity becomes an AgentKeySource; the pipe is shared
                        // and must stay open until Connect() completes (agent signs challenge).
                        var keySources = identities
                            .Select(id => new AgentKeySource(id, agentPipe))
                            .ToArray<IPrivateKeySource>();

                        var authMethod = new PrivateKeyAuthenticationMethod(profile.Username, keySources);
                        var connInfo = new ConnectionInfo(
                            profile.Host, profile.Port, profile.Username, authMethod);
                        _client = new SshClient(connInfo);

                        if (profile.KeepAliveIntervalSeconds > 0)
                            _client.KeepAliveInterval = TimeSpan.FromSeconds(profile.KeepAliveIntervalSeconds);
                    }
                    else
                    {
                        _client = BuildClient(profile);
                    }

                    // ── Host Key Verification (shared for all auth types) ─────
                    // BUG-10: HostKeyVerifier 为 null 时默认拒绝，而非静默放行（防 MITM）
                    var verifier = HostKeyVerifier;
                    _client.HostKeyReceived += (_, e) =>
                    {
                        if (verifier == null)
                        {
                            // 未设置验证器时拒绝连接，强制调用方提供验证逻辑
                            e.CanTrust = false;
                            return;
                        }
                        string fp = BitConverter.ToString(e.FingerPrint).Replace("-", ":");
                        // Block this thread-pool thread while UI dialog is shown
                        bool trusted = verifier(profile.Host, profile.Port, e.HostKeyName, fp)
                            .GetAwaiter().GetResult();
                        e.CanTrust = trusted;
                    };

                    _client.Connect(); // agentPipe kept open here — agent signs the challenge
                    _client.ErrorOccurred += OnClientError;

                    // ── Setup Port Forwarding ───────────────────────────────────────
                    if (profile.PortForwards != null)
                    {
                        foreach (var rule in profile.PortForwards)
                        {
                            if (!rule.Enabled) continue;
                            try
                            {
                                ForwardedPort? port = null;
                                switch (rule.Type)
                                {
                                    case PortForwardType.Local:
                                        port = new ForwardedPortLocal(rule.BindAddress, (uint)rule.BindPort, rule.TargetHost, (uint)rule.TargetPort);
                                        break;
                                    case PortForwardType.Remote:
                                        port = new ForwardedPortRemote(System.Net.IPAddress.Any.ToString(), (uint)rule.BindPort, rule.TargetHost, (uint)rule.TargetPort);
                                        break;
                                    case PortForwardType.Dynamic:
                                        port = new ForwardedPortDynamic(rule.BindAddress, (uint)rule.BindPort);
                                        break;
                                }
                                if (port != null)
                                {
                                    _client.AddForwardedPort(port);
                                    port.Exception += (s, e) => { System.Diagnostics.Debug.WriteLine($"[PortForward] Exception: {e.Exception.Message}"); };
                                    port.Start();
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[PortForward] Failed to bind ({rule.Type} {rule.BindPort}): {ex.Message}");
                            }
                        }
                    }

                    _shell = _client.CreateShellStream(
                        terminalName: "xterm-256color",
                        columns: (uint)cols,
                        rows: (uint)rows,
                        width: 0,
                        height: 0,
                        bufferSize: 4096);
                }
                finally
                {
                    // Agent pipe is only needed during the handshake; dispose after Connect().
                    agentPipe?.Dispose();
                }
            });

            // Start background read loop
            _readCts = new CancellationTokenSource();
            StartReadLoop();
            StartWriteLoop();
        }

        // ── Input ─────────────────────────────────────────────────────────────

        public void SendInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            EnqueueInput(Encoding.UTF8.GetBytes(text));
        }

        public void SendRaw(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            EnqueueInput(data);
        }

        private void EnqueueInput(byte[] data)
        {
            if (!IsConnected) return;
            byte[] owned = data.ToArray();
            lock (_writeSync)
            {
                _writeChannel?.Writer.TryWrite(owned);
            }
        }

        private void StartWriteLoop()
        {
            var shell = _shell;
            if (shell == null) return;

            StopWriteLoop();
            var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            var cts = new CancellationTokenSource();
            lock (_writeSync)
            {
                _writeChannel = channel;
                _writeCts = cts;
            }
            _ = Task.Run(() => WriteLoopAsync(channel.Reader, shell, cts.Token));
        }

        private async Task WriteLoopAsync(ChannelReader<byte[]> reader, ShellStream shell, CancellationToken token)
        {
            try
            {
                while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    if (!reader.TryRead(out byte[]? first)) continue;
                    await Task.Delay(1, token).ConfigureAwait(false);

                    var batch = new System.Collections.Generic.List<byte[]> { first };
                    int totalLength = first.Length;
                    while (reader.TryRead(out byte[]? next))
                    {
                        batch.Add(next);
                        totalLength += next.Length;
                    }

                    if (batch.Count == 1)
                    {
                        shell.Write(first, 0, first.Length);
                    }
                    else
                    {
                        byte[] combined = new byte[totalLength];
                        int offset = 0;
                        foreach (byte[] item in batch)
                        {
                            Buffer.BlockCopy(item, 0, combined, offset, item.Length);
                            offset += item.Length;
                        }
                        shell.Write(combined, 0, combined.Length);
                    }
                    shell.Flush();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    System.Diagnostics.Debug.WriteLine($"[SSH] WriteLoop error: {ex.Message}");
            }
        }

        private void StopWriteLoop()
        {
            CancellationTokenSource? cts;
            Channel<byte[]>? channel;
            lock (_writeSync)
            {
                cts = _writeCts;
                channel = _writeChannel;
                _writeCts = null;
                _writeChannel = null;
            }
            channel?.Writer.TryComplete();
            cts?.Cancel();
            cts?.Dispose();
        }

        // ── Background Command Execution ──────────────────────────────────────

        /// <summary>
        /// Executes a command in a separate background channel over the same SSH connection.
        /// This does not interfere with the interactive ShellStream and is used by the AI assistant.
        /// </summary>
        public async Task<string> ExecuteBackgroundCommandAsync(string command, int timeoutSeconds = 30)
        {
            if (_client == null || !IsConnected)
                throw new InvalidOperationException("Not connected to SSH server.");

            using var cmd = _client.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            
            return await Task.Run(() => 
            {
                string result = cmd.Execute();
                if (cmd.ExitStatus != 0 && string.IsNullOrWhiteSpace(result))
                {
                    result = cmd.Error;
                }
                return result;
            });
        }

        // ── Resize ────────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a PTY window-size change request to the remote server.
        /// Call this whenever the TerminalView is resized.
        /// </summary>
        public void ResizeTerminal(int cols, int rows)
        {
            if (_shell == null || !IsConnected || (cols == _cols && rows == _rows)) return;
            _cols = cols;
            _rows = rows;
            try
            {
                // ShellStream doesn't expose SendWindowChangeRequest publicly, but its internal channel does.
                // Search by interface name to be robust against SSH.NET version upgrades renaming the field.
                var field = typeof(ShellStream).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(f => f.FieldType.Name.Contains("IChannelSession") || f.Name.Contains("channel"));
                    
                if (field?.GetValue(_shell) is object channel)
                {
                    var method = channel.GetType().GetMethod("SendWindowChangeRequest",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    method?.Invoke(channel, new object[] { (uint)cols, (uint)rows, (uint)0, (uint)0 });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SSH] Resize error: {ex.Message}");
            }
        }

        // ── Background Read Loop ──────────────────────────────────────────────

        /// <summary>
        /// Reads raw bytes from ShellStream continuously and fires DataReceived.
        /// IMPORTANT: must run continuously to prevent SSH pipe buffer from filling up.
        /// </summary>
        private void StartReadLoop()
        {
            var cts = _readCts;
            if (cts == null) return;
            _readTask = Task.Factory.StartNew(
                () => ReadLoop(cts.Token), cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void ReadLoop(CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_shell == null) break;

                    int bytesRead;
                    // ShellStream.Read blocks until data is available (or stream closes)
                    try { bytesRead = _shell.Read(buffer, 0, buffer.Length); }
                    catch { bytesRead = -1; }

                    if (bytesRead <= 0) break;  // stream closed
                    DataReceived?.Invoke(buffer.AsSpan(0, bytesRead));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SSH] ReadLoop exception: {ex.Message}");
                    break;
                }
            }

            // If we exit the loop unexpectedly, notify caller
            if (!token.IsCancellationRequested)
                Disconnected?.Invoke(null);
        }

        // ── Reconnect (exponential back-off) ──────────────────────────────────

        private async void OnClientError(object? sender, ExceptionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[SSH] ClientError: {e.Exception.Message}");
            await ReconnectWithBackoffAsync();
        }

        private async Task ReconnectWithBackoffAsync()
        {
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                int delaySec = (int)Math.Pow(2, attempt); // 2, 4, 8 seconds
                System.Diagnostics.Debug.WriteLine($"[SSH] Reconnect attempt {attempt}/{maxRetries} in {delaySec}s...");
                
                string msg = $"\r\n\x1b[33m[SSH] Connection lost. Reconnecting attempt {attempt}/{maxRetries} in {delaySec}s...\x1b[0m\r\n";
                DataReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(msg));
                
                await Task.Delay(TimeSpan.FromSeconds(delaySec));
                try
                {
                    if (Profile == null) return;
                    _readCts?.Cancel();
                    _readCts?.Dispose();
                    DisposeShell();
                    _client?.Connect();
                    _shell = _client!.CreateShellStream("xterm-256color",
                        (uint)_cols, (uint)_rows, 0, 0, 4096);
                    _readCts = new CancellationTokenSource();
                    StartReadLoop();
                    StartWriteLoop();
                    
                    string successMsg = $"\r\n\x1b[32m[SSH] Reconnected successfully.\x1b[0m\r\n";
                    DataReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(successMsg));
                    
                    System.Diagnostics.Debug.WriteLine($"[SSH] Reconnected successfully.");
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SSH] Reconnect attempt {attempt} failed: {ex.Message}");
                }
            }

            // Give up after max retries
            string failMsg = $"\r\n\x1b[31m[SSH] Reconnect failed after 3 attempts. Disconnected.\x1b[0m\r\n";
            DataReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(failMsg));
            
            Disconnected?.Invoke(new Exception("Reconnect failed after 3 attempts."));
        }

        // ── Auth Builder ──────────────────────────────────────────────────────

        private static SshClient BuildClient(ConnectionProfile profile)
        {
            string host = profile.Host;
            int port = profile.Port;
            string user = profile.Username;

            SshClient client;
            if (profile.AuthType == "PrivateKey")
            {
                string keyPath = profile.PrivateKeyPath;
                string passphrase = ConnectionStorage.DecryptSecret(profile.EncryptedPassphrase);

                PrivateKeyFile keyFile = string.IsNullOrEmpty(passphrase)
                    ? new PrivateKeyFile(keyPath)
                    : new PrivateKeyFile(keyPath, passphrase);

                client = new SshClient(host, port, user, keyFile);
            }
            else
            {
                string password = ConnectionStorage.DecryptSecret(profile.EncryptedPassword);
                client = new SshClient(host, port, user, password);
            }

            // 应用 Keepalive 心跳（防止 NAT/防火墙空闲断连）
            if (profile.KeepAliveIntervalSeconds > 0)
            {
                client.KeepAliveInterval = TimeSpan.FromSeconds(profile.KeepAliveIntervalSeconds);
            }

            return client;
        }

        // ── Disconnect / Dispose ──────────────────────────────────────────────

        public void Disconnect()
        {
            _readCts?.Cancel();
            StopWriteLoop();
            DisposeShell();
            try { _client?.Disconnect(); } catch { }
        }

        private void DisposeShell()
        {
            StopWriteLoop();
            try { _shell?.Dispose(); } catch { }
            _shell = null;
        }

        public void Dispose()
        {
            Disconnect();
            _readCts?.Dispose();
            _client?.Dispose();
        }
    }
}
