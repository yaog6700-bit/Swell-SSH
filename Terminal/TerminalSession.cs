using System;
using System.Threading.Tasks;
using SwellSSH.Models;

namespace SwellSSH.Terminal
{
    /// <summary>
    /// Owns one complete SSH terminal session.
    /// Combines SshTransport + ConPtyBridge, and will later integrate
    /// VtParser + TerminalBuffer (Phase 3) and TerminalView (Phase 4).
    ///
    /// Lifecycle:
    ///   1. Construct with a ConnectionProfile
    ///   2. Call ConnectAsync() → fires StateChanged
    ///   3. TerminalView subscribes to RawDataReceived (Phase 2 debug) or
    ///      TerminalBuffer.Changed (Phase 4)
    ///   4. Call Dispose() when the tab is closed → SSH disconnects cleanly
    /// </summary>
    public sealed class TerminalSession : IDisposable
    {
        // ── Public state ─────────────────────────────────────────────────────

        public ConnectionProfile Profile { get; }
        public SshTransport Transport { get; }
        public ConPtyBridge PtyBridge { get; }
        
        public TerminalBuffer Buffer { get; }
        public VtParser Parser { get; }

        public enum SessionState { Disconnected, Connecting, Connected, Error }

        private SessionState _state = SessionState.Disconnected;
        public SessionState State
        {
            get => _state;
            private set
            {
                _state = value;
                StateChanged?.Invoke(this, value);
            }
        }

        public string? LastError { get; private set; }

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired whenever the session state changes.</summary>
        public event EventHandler<SessionState>? StateChanged;

        /// <summary>
        /// Phase 2 debug: raw UTF-8 output from server, before VT parsing.
        /// In Phase 4 this will be replaced by TerminalBuffer.Changed events.
        /// </summary>
        public event SshDataReceivedHandler? RawDataReceived;

        /// <summary>Fired when the remote sends an OSC window title update.</summary>
        public event Action<string>? TitleChanged;

        // ── Construction ──────────────────────────────────────────────────────

        public TerminalSession(ConnectionProfile profile)
        {
            Profile = profile;
            Transport = new SshTransport();
            PtyBridge = new ConPtyBridge(profile.TerminalCols, profile.TerminalRows);
            
            Buffer = new TerminalBuffer(profile.TerminalCols, profile.TerminalRows);
            Parser = new VtParser(Buffer);

            // Wire raw data through to parser
            Transport.DataReceived += bytes =>
            {
                RawDataReceived?.Invoke(bytes);
                Buffer.BeginUpdate();
                try
                {
                    lock (Buffer.SyncRoot)
                    {
                        Parser.Feed(bytes);
                    }
                }
                finally
                {
                    Buffer.EndUpdate();
                }
            };

            Transport.Disconnected += ex =>
            {
                LastError = ex?.Message;
                State = SessionState.Error;
            };

            Buffer.TitleChanged += title => TitleChanged?.Invoke(title);
        }

        // ── Connect ───────────────────────────────────────────────────────────

        public async Task ConnectAsync()
        {
            if (State == SessionState.Connecting || State == SessionState.Connected) return;

            State = SessionState.Connecting;
            LastError = null;

            try
            {
                await Transport.ConnectAsync(Profile, Profile.TerminalCols, Profile.TerminalRows);
                Profile.LastConnected = DateTime.Now;
                State = SessionState.Connected;
            }
            catch (Renci.SshNet.Common.SshAuthenticationException ex)
            {
                LastError = $"认证失败：{ex.Message}";
                State = SessionState.Error;
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                if (ex.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
                {
                    LastError = $"网络访问被拒绝 (10013)：如果这是纯 IPv6 服务器，请检查您的本地网络是否支持 IPv6；否则请检查防火墙/杀毒软件是否拦截了连接。";
                }
                else if (ex.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound)
                {
                    LastError = $"无法解析主机名：请检查服务器地址是否拼写正确，且没有多余的空格。";
                }
                else
                {
                    LastError = $"无法连接到服务器：{ex.Message}";
                }
                State = SessionState.Error;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                State = SessionState.Error;
            }
        }

        // ── Input helpers (thin wrappers) ─────────────────────────────────────

        public void SendText(string text) => Transport.SendInput(text);
        public void SendRaw(byte[] data)  => Transport.SendRaw(data);

        public Task<string> ExecuteBackgroundCommandAsync(string command, int timeoutSeconds = 30)
            => Transport.ExecuteBackgroundCommandAsync(command, timeoutSeconds);

        public void Resize(double pixelWidth, double pixelHeight,
                           double charWidth, double charHeight)
            => PtyBridge.OnViewResized(pixelWidth, pixelHeight, charWidth, charHeight, Transport);

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            Transport.Disconnect();
            Transport.Dispose();
            PtyBridge.Dispose();
            State = SessionState.Disconnected;
        }
    }
}
