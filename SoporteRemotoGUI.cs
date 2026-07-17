using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// =============================================================================
//  SercomDesk - Agente de Soporte Remoto para Windows  v3.0.0
//  Compilar: csc.exe /target:winexe /out:SercomSoporte.exe /reference:System.Net.Http.dll SoporteRemotoGUI.cs
//  DISEÑO: El cliente NUNCA necesita presionar nada. Solo abre el programa y comparte su ID.
// =============================================================================
namespace SercomSoporte
{
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] internal struct INPUT_UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint type; public INPUT_UNION U; }

    internal static class Win32
    {
        public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    }

    // =========================================================================
    //  FORMULARIO PRINCIPAL — Diseño minimalista para el cliente final
    // =========================================================================
    public class SoporteRemotoGUI : Form
    {
        // ── Configuración del servidor ───────────────────────────────────────
        private const string ServerUrl   = "http://129.159.72.206:6001";
        private const string RelayWsUrl  = "ws://129.159.72.206:6002";
        private const string AgentToken  = "SercomAgentToken2026SecureHashKey";
        private const string AppVersion  = "##AGENT_VERSION##"; // inyectado por el servidor al descargar

        // ── Recursos gráficos inyectados en caliente por Express ─────────────
        private static readonly string LogoBase64 = "";
        private static readonly string IconBase64 = "";

        // ── Estado interno ───────────────────────────────────────────────────
        private readonly string _supportId;
        private readonly string _hostname;
        private bool _running = true;
        private bool _streaming = false;

        // ── WebSocket de streaming ───────────────────────────────────────────
        private ClientWebSocket _wsClient;
        private CancellationTokenSource _wsCts;
        private Bitmap _prevScreenBitmap;
        private readonly int _gridCols = 16;
        private readonly int _gridRows = 9;

        // ── Hilo de soporte ──────────────────────────────────────────────────
        private Thread _supportThread;

        // ── Controles UI ─────────────────────────────────────────────────────
        private Label lblStatus;
        private Label lblIdValue;
        private Label _lblVersion;
        private Button btnCopyId;
        private PictureBox picLogo;

        // =====================================================================
        //  Punto de Entrada
        // =====================================================================
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SoporteRemotoGUI());
        }

        // =====================================================================
        //  Constructor
        // =====================================================================
        public SoporteRemotoGUI()
        {
            // Forzar TLS 1.2 para conexiones HTTPS seguras en .NET 4.0
            try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072; } catch { }

            _supportId = GeneratePersistentId();
            _hostname  = Environment.MachineName.ToUpper().Replace(" ", "").Replace(".", "");

            BuildUI();

            // Cargar logo si existe
            if (!string.IsNullOrEmpty(LogoBase64))
            {
                try
                {
                    byte[] logoBytes = Convert.FromBase64String(LogoBase64);
                    using (MemoryStream ms = new MemoryStream(logoBytes))
                        picLogo.Image = Image.FromStream(ms);
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(IconBase64))
            {
                try
                {
                    byte[] iconBytes = Convert.FromBase64String(IconBase64);
                    using (MemoryStream ms = new MemoryStream(iconBytes))
                        this.Icon = new Icon(ms);
                }
                catch { }
            }

            // Arrancar el hilo de soporte en segundo plano
            _supportThread = new Thread(RunSupportLoop) { IsBackground = true };
            _supportThread.Start();
        }

        // =====================================================================
        //  Construcción de la UI minimalista
        // =====================================================================
        private void BuildUI()
        {
            // ── Ventana ───────────────────────────────────────────────────────
            this.Text            = "Sercom Soporte Remoto";
            this.Size            = new Size(420, 330);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox     = false;
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.BackColor       = Color.FromArgb(15, 15, 25);
            this.ForeColor       = Color.White;
            this.FormClosing    += (s, e) => { _running = false; try { if (_wsCts != null) _wsCts.Cancel(); } catch { } };

            // ── Logo ──────────────────────────────────────────────────────────
            picLogo = new PictureBox
            {
                Bounds    = new Rectangle(20, 20, 260, 52),
                SizeMode  = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };
            this.Controls.Add(picLogo);

            // ── Etiqueta "Tu ID de Soporte" ───────────────────────────────────
            var lblTitle = new Label
            {
                Text      = "Tu ID de Soporte:",
                Font      = new Font("Segoe UI", 9f, FontStyle.Regular),
                ForeColor = Color.FromArgb(150, 150, 180),
                Bounds    = new Rectangle(20, 92, 360, 20),
                AutoSize  = false
            };
            this.Controls.Add(lblTitle);

            // ── ID grande ─────────────────────────────────────────────────────
            lblIdValue = new Label
            {
                Text      = _supportId ?? "----",
                Font      = new Font("Segoe UI", 34f, FontStyle.Bold),
                ForeColor = Color.FromArgb(99, 179, 237),
                Bounds    = new Rectangle(18, 114, 260, 60),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
            this.Controls.Add(lblIdValue);

            // ── Botón COPIAR ──────────────────────────────────────────────────
            btnCopyId = new Button
            {
                Text      = "📋 Copiar",
                Font      = new Font("Segoe UI", 9f, FontStyle.Regular),
                Bounds    = new Rectangle(295, 126, 92, 36),
                BackColor = Color.FromArgb(30, 80, 180),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            btnCopyId.FlatAppearance.BorderSize = 0;
            btnCopyId.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(_supportId);
                    btnCopyId.Text = "✅ Copiado";
                    var t = new System.Windows.Forms.Timer { Interval = 2000 };
                    t.Tick += (ts, te) => { btnCopyId.Text = "📋 Copiar"; t.Stop(); t.Dispose(); };
                    t.Start();
                }
                catch { }
            };
            this.Controls.Add(btnCopyId);

            // ── Separador ────────────────────────────────────────────────────
            var sep = new Panel
            {
                Bounds    = new Rectangle(20, 196, 360, 1),
                BackColor = Color.FromArgb(40, 40, 60)
            };
            this.Controls.Add(sep);

            // ── Estado ────────────────────────────────────────────────────────
            lblStatus = new Label
            {
                Text      = "⏳ Conectando...",
                Font      = new Font("Segoe UI", 9f, FontStyle.Regular),
                ForeColor = Color.FromArgb(150, 150, 180),
                Bounds    = new Rectangle(20, 210, 360, 22),
                AutoSize  = false
            };
            this.Controls.Add(lblStatus);

            // ── Instrucción simple ────────────────────────────────────────────
            var lblHint = new Label
            {
                Text      = "Comparte tu ID con el técnico de soporte.\nNo es necesario hacer nada más.",
                Font      = new Font("Segoe UI", 8f, FontStyle.Regular),
                ForeColor = Color.FromArgb(100, 100, 130),
                Bounds    = new Rectangle(20, 238, 360, 36),
                AutoSize  = false
            };
            this.Controls.Add(lblHint);

            // ── Versión (campo de instancia para poder actualizarla) ─────────
            _lblVersion = new Label
            {
                Text      = AppVersion,
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 110),
                Bounds    = new Rectangle(10, 280, 390, 15),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleRight
            };
            this.Controls.Add(_lblVersion);
        }

        // =====================================================================
        //  Actualización de estado (thread-safe)
        // =====================================================================
        private void SetStatus(string text, Color color)
        {
            if (lblStatus.InvokeRequired)
                lblStatus.Invoke(new MethodInvoker(() => { lblStatus.Text = text; lblStatus.ForeColor = color; }));
            else
            { lblStatus.Text = text; lblStatus.ForeColor = color; }
        }

        // =====================================================================
        //  Loop Principal — Registro + Poll de comandos
        // =====================================================================
        private void RunSupportLoop()
        {
            while (_running)
            {
                SetStatus("🔌 Conectando al servidor...", Color.FromArgb(245, 158, 11));
                string errorMsg = "";
                if (RegisterAgent(out errorMsg))
                {
                    SetStatus("✅ En espera del técnico...", Color.FromArgb(16, 185, 129));
                    while (_running)
                    {
                        if (!PollCommands()) break;
                        Thread.Sleep(1000);
                    }
                }
                else
                {
                    SetStatus("⚠️ Falló: " + errorMsg + ". Reintentando...", Color.FromArgb(239, 68, 68));
                    Thread.Sleep(5000);
                }
            }
        }

        // =====================================================================
        //  Registro del agente en el servidor
        // =====================================================================
        private bool RegisterAgent(out string errorMsg)
        {
            errorMsg = "";
            try
            {
                // Omitir llamada pesada a WMI/PowerShell en el registro de inicio rápido
                string healthJson = "null";

                string json = string.Format("{{\"id\":\"{0}\",\"hostname\":\"{1}\",\"health\":{2}}}",
                    _supportId, _hostname, healthJson);
                byte[] data = Encoding.UTF8.GetBytes(json);

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ServerUrl + "/soporte/register");
                req.Method      = "POST";
                req.ContentType = "application/json";
                req.ContentLength = data.Length;
                req.Timeout     = 8000;
                req.UserAgent   = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SercomAgent/" + AppVersion;
                req.Headers.Add("x-sercom-agent-token", AgentToken);

                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                {
                    if (res.StatusCode == HttpStatusCode.OK)
                    {
                        // Hardware en background
                        System.Threading.Tasks.Task.Run(() => SendHardwareHealth());
                        // Verificar actualizaciones en background (solo en el primer registro)
                        System.Threading.Tasks.Task.Run(() => CheckForUpdates());
                        return true;
                    }
                    else
                    {
                        errorMsg = "HTTP " + (int)res.StatusCode;
                        return false;
                    }
                }
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    var httpRes = wex.Response as HttpWebResponse;
                    errorMsg = httpRes != null ? "HTTP " + (int)httpRes.StatusCode : wex.Status.ToString();
                }
                else
                {
                    errorMsg = wex.Message;
                }
                return false;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return false;
            }
        }

        // =====================================================================
        //  Poll de comandos — El servidor encola __RELAY_START__ cuando el técnico abre la sesión
        // =====================================================================
        private bool PollCommands()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ServerUrl + "/soporte/poll?id=" + _supportId);
                req.Method  = "GET";
                req.Timeout = 4000;
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SercomAgent/" + AppVersion;
                req.Headers.Add("x-sercom-agent-token", AgentToken);

                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(res.GetResponseStream()))
                {
                    string text = reader.ReadToEnd();
                    if (text.Contains("\"command\":null") || string.IsNullOrEmpty(text))
                        return true;

                    string cmdId   = ExtractJsonValue(text, "id");
                    string cmdText = ExtractJsonValue(text, "text");

                    if (string.IsNullOrEmpty(cmdId) || string.IsNullOrEmpty(cmdText))
                        return true;

                    // ── Comandos especiales de control remoto ────────────────
                    if (cmdText.StartsWith("__RELAY_START__"))
                    {
                        if (!_streaming)
                        {
                            SetStatus("📡 Técnico conectado — Transmitiendo...", Color.FromArgb(99, 179, 237));
                            Task.Run(() => StartStreamingAsync());
                        }
                        SendResponse(cmdId, "STREAMING_STARTED");
                    }
                    else if (cmdText.StartsWith("__RELAY_STOP__"))
                    {
                        Task.Run(() => StopStreamingAsync());
                        SetStatus("✅ En espera del técnico...", Color.FromArgb(16, 185, 129));
                        SendResponse(cmdId, "STREAMING_STOPPED");
                    }
                    else
                    {
                        // Comandos PowerShell desde la terminal del panel
                        string output = ExecutePowerShell(cmdText);
                        SendResponse(cmdId, output);
                    }
                }
                return true;
            }
            catch { return false; }
        }

        // =====================================================================
        //  Respuesta de comandos al servidor
        // =====================================================================
        private void SendResponse(string cmdId, string output)
        {
            try
            {
                string safe = (output ?? "")
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n");
                string json = string.Format("{{\"id\":\"{0}\",\"cmdId\":\"{1}\",\"output\":\"{2}\"}}",
                    _supportId, cmdId, safe);
                byte[] data = Encoding.UTF8.GetBytes(json);

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ServerUrl + "/soporte/response");
                req.Method      = "POST";
                req.ContentType = "application/json";
                req.ContentLength = data.Length;
                req.Timeout     = 5000;
                req.Headers.Add("x-sercom-agent-token", AgentToken);

                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (req.GetResponse()) { }
            }
            catch { }
        }

        // =====================================================================
        //  Streaming WebSocket — Inicio automático, sin intervención del cliente
        // =====================================================================
        private async Task StartStreamingAsync()
        {
            if (_streaming) return;
            _streaming = true;
            _wsCts     = new CancellationTokenSource();
            _wsClient  = new ClientWebSocket();
            _wsClient.Options.SetRequestHeader("x-sercom-agent-token", AgentToken);

            try
            {
                string wsUri = string.Format("{0}?type=agent&id={1}", RelayWsUrl, _supportId);
                await _wsClient.ConnectAsync(new Uri(wsUri), _wsCts.Token);

                Task send    = SendScreenFramesAsync(_wsCts.Token);
                Task receive = ReceiveInputCommandsAsync(_wsCts.Token);

                await Task.WhenAny(send, receive);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetStatus("❌ Error streaming: " + ex.Message, Color.FromArgb(239, 68, 68));
            }
            finally
            {
                _streaming = false;
                try { if (_wsClient != null) _wsClient.Dispose(); } catch { }
                SetStatus("✅ En espera del técnico...", Color.FromArgb(16, 185, 129));
            }
        }

        private async Task StopStreamingAsync()
        {
            if (_wsCts != null) _wsCts.Cancel();
            if (_wsClient != null && _wsClient.State == WebSocketState.Open)
                try { await _wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            _streaming = false;
        }

        // =====================================================================
        //  Captura de pantalla — Dirty Rectangles (~15 FPS)
        // =====================================================================
        private async Task SendScreenFramesAsync(CancellationToken ct)
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            int cellW  = screen.Width  / _gridCols;
            int cellH  = screen.Height / _gridRows;
            _prevScreenBitmap = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);

            while (!ct.IsCancellationRequested && _wsClient != null && _wsClient.State == WebSocketState.Open)
            {
                try
                {
                    using (Bitmap current = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb))
                    using (Graphics g = Graphics.FromImage(current))
                    {
                        g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);

                        for (int col = 0; col < _gridCols; col++)
                        {
                            for (int row = 0; row < _gridRows; row++)
                            {
                                int x = col * cellW, y = row * cellH;
                                int w = (col == _gridCols - 1) ? screen.Width  - x : cellW;
                                int h = (row == _gridRows - 1) ? screen.Height - y : cellH;

                                if (!IsCellDirty(current, _prevScreenBitmap, x, y, w, h)) continue;

                                using (Bitmap cell = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                                using (Graphics gc = Graphics.FromImage(cell))
                                using (MemoryStream ms = new MemoryStream())
                                {
                                    gc.DrawImage(current, 0, 0, new Rectangle(x, y, w, h), GraphicsUnit.Pixel);

                                    var jpegEncoder  = GetJpegEncoder();
                                    var encoderParams = new EncoderParameters(1);
                                    encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 55L);
                                    cell.Save(ms, jpegEncoder, encoderParams);

                                    string b64 = Convert.ToBase64String(ms.ToArray());
                                    string msg  = string.Format(
                                        "{{\"type\":\"frame\",\"col\":{0},\"row\":{1},\"cellW\":{2},\"cellH\":{3},\"x\":{4},\"y\":{5},\"sw\":{6},\"sh\":{7},\"data\":\"{8}\"}}",
                                        col, row, w, h, x, y, screen.Width, screen.Height, b64);

                                    byte[] msgBytes = Encoding.UTF8.GetBytes(msg);
                                    await _wsClient.SendAsync(new ArraySegment<byte>(msgBytes), WebSocketMessageType.Text, true, ct);
                                }
                            }
                        }

                        using (Graphics gPrev = Graphics.FromImage(_prevScreenBitmap))
                            gPrev.DrawImage(current, 0, 0);
                    }
                    await Task.Delay(66, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { Thread.Sleep(200); }
            }
        }

        private bool IsCellDirty(Bitmap current, Bitmap prev, int x, int y, int w, int h)
        {
            int step = Math.Max(1, (w * h) / 16);
            for (int i = 0; i < w * h; i += step)
            {
                int px = x + (i % w), py = y + (i / w);
                if (px >= current.Width || py >= current.Height) continue;
                Color c1 = current.GetPixel(px, py), c2 = prev.GetPixel(px, py);
                if (Math.Abs(c1.R - c2.R) + Math.Abs(c1.G - c2.G) + Math.Abs(c1.B - c2.B) > 15) return true;
            }
            return false;
        }

        private ImageCodecInfo GetJpegEncoder()
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
                if (codec.MimeType == "image/jpeg") return codec;
            return null;
        }

        // =====================================================================
        //  Receptor de comandos de control (mouse, teclado, portapapeles)
        // =====================================================================
        private async Task ReceiveInputCommandsAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[4096];
            var sb = new StringBuilder();

            while (!ct.IsCancellationRequested && _wsClient != null && _wsClient.State == WebSocketState.Open)
            {
                try
                {
                    sb.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    ProcessRemoteInput(sb.ToString());
                }
                catch (OperationCanceledException) { break; }
                catch { Thread.Sleep(100); }
            }
        }

        private void ProcessRemoteInput(string json)
        {
            try
            {
                string type = ExtractJsonValue(json, "type");
                switch (type)
                {
                    case "mouse_move":   HandleMouseMove(json); break;
                    case "mouse_click":  HandleMouseClick(json); break;
                    case "mouse_scroll": HandleMouseScroll(json); break;
                    case "key":          HandleKeyInput(json); break;
                    case "set_clipboard": HandleSetClipboard(json); break;
                    case "get_clipboard": HandleGetClipboard(); break;
                    case "start_stream":
                        if (!_streaming) { SetStatus("📡 Técnico conectado — Transmitiendo...", Color.FromArgb(99, 179, 237)); Task.Run(() => StartStreamingAsync()); }
                        break;
                    case "stop_stream":
                        Task.Run(() => StopStreamingAsync());
                        SetStatus("✅ En espera del técnico...", Color.FromArgb(16, 185, 129));
                        break;
                }
            }
            catch { }
        }

        private void HandleMouseMove(string json)
        {
            double nx = ParseDouble(ExtractJsonValue(json, "x")), ny = ParseDouble(ExtractJsonValue(json, "y"));
            int absX = (int)(nx * 65535), absY = (int)(ny * 65535);
            var inputs = new INPUT[1];
            inputs[0].type = Win32.INPUT_MOUSE;
            inputs[0].U.mi.dx = absX; inputs[0].U.mi.dy = absY;
            inputs[0].U.mi.dwFlags = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE;
            Win32.SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private void HandleMouseClick(string json)
        {
            double nx = ParseDouble(ExtractJsonValue(json, "x")), ny = ParseDouble(ExtractJsonValue(json, "y"));
            string button = ExtractJsonValue(json, "button"), action = ExtractJsonValue(json, "action");
            int absX = (int)(nx * 65535), absY = (int)(ny * 65535);
            uint downFlag, upFlag;
            switch (button)
            {
                case "right":  downFlag = Win32.MOUSEEVENTF_RIGHTDOWN;  upFlag = Win32.MOUSEEVENTF_RIGHTUP;  break;
                case "middle": downFlag = Win32.MOUSEEVENTF_MIDDLEDOWN; upFlag = Win32.MOUSEEVENTF_MIDDLEUP; break;
                default:       downFlag = Win32.MOUSEEVENTF_LEFTDOWN;   upFlag = Win32.MOUSEEVENTF_LEFTUP;   break;
            }
            var inputs = new INPUT[2];
            inputs[0].type = Win32.INPUT_MOUSE; inputs[0].U.mi.dx = absX; inputs[0].U.mi.dy = absY; inputs[0].U.mi.dwFlags = downFlag | Win32.MOUSEEVENTF_ABSOLUTE;
            inputs[1].type = Win32.INPUT_MOUSE; inputs[1].U.mi.dx = absX; inputs[1].U.mi.dy = absY; inputs[1].U.mi.dwFlags = upFlag   | Win32.MOUSEEVENTF_ABSOLUTE;
            Win32.SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private void HandleMouseScroll(string json)
        {
            int delta = 0; try { delta = int.Parse(ExtractJsonValue(json, "delta") ?? "0"); } catch { }
            var inputs = new INPUT[1];
            inputs[0].type = Win32.INPUT_MOUSE;
            inputs[0].U.mi.dwFlags = Win32.MOUSEEVENTF_WHEEL;
            inputs[0].U.mi.mouseData = (uint)(delta * 120);
            Win32.SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private void HandleKeyInput(string json)
        {
            string vkStr = ExtractJsonValue(json, "keyCode"), action = ExtractJsonValue(json, "action");
            bool ctrl = json.Contains("\"ctrl\":true"), alt = json.Contains("\"alt\":true"), shift = json.Contains("\"shift\":true");
            if (string.IsNullOrEmpty(vkStr)) return;
            ushort vk = (ushort)(int.Parse(vkStr));
            var modList = new System.Collections.Generic.List<INPUT>();
            if (action != "up") { if (ctrl) modList.Add(MakeKeyInput(0x11, false)); if (alt) modList.Add(MakeKeyInput(0x12, false)); if (shift) modList.Add(MakeKeyInput(0x10, false)); }
            modList.Add(MakeKeyInput(vk, action == "up"));
            if (action == "up" || action == "click") { if (shift) modList.Add(MakeKeyInput(0x10, true)); if (alt) modList.Add(MakeKeyInput(0x12, true)); if (ctrl) modList.Add(MakeKeyInput(0x11, true)); }
            Win32.SendInput((uint)modList.Count, modList.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        private INPUT MakeKeyInput(ushort vk, bool keyUp)
        {
            var inp = new INPUT { type = Win32.INPUT_KEYBOARD };
            inp.U.ki.wVk    = vk;
            inp.U.ki.dwFlags = keyUp ? Win32.KEYEVENTF_KEYUP : 0;
            return inp;
        }

        private void HandleSetClipboard(string json)
        {
            string text = ExtractJsonValue(json, "text");
            if (!string.IsNullOrEmpty(text))
                this.Invoke(new MethodInvoker(() => { try { Clipboard.SetText(text); } catch { } }));
        }

        private void HandleGetClipboard()
        {
            string text = "";
            try
            {
                if (this.InvokeRequired)
                    this.Invoke(new MethodInvoker(() => { try { text = Clipboard.GetText(); } catch { } }));
                else
                    text = Clipboard.GetText();
            }
            catch { }

            string msg = string.Format("{{\"type\":\"clipboard\",\"text\":\"{0}\"}}",
                text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n"));
            byte[] data = Encoding.UTF8.GetBytes(msg);
            Task.Run(async () =>
            {
                try
                {
                    if (_wsClient != null && _wsClient.State == WebSocketState.Open)
                        await _wsClient.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch { }
            });
        }

        // =====================================================================
        //  PowerShell silencioso
        // =====================================================================
        private string ExecutePowerShell(string command)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = "powershell.exe",
                    Arguments              = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + command.Replace("\"", "\\\"") + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return (string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\r\n[ERROR]\r\n" + stderr).Trim();
                }
            }
            catch (Exception ex) { return "[ERROR] " + ex.Message; }
        }

        // =====================================================================
        //  Auto-actualización: descarga, compila y relanza si hay nueva versión
        // =====================================================================
        private void CheckForUpdates()
        {
            try
            {
                // 1. Consultar versión actual en el servidor
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ServerUrl + "/soporte/version");
                req.Method  = "GET";
                req.Timeout = 8000;
                req.Headers.Add("x-sercom-agent-token", AgentToken);

                string serverVersion = "";
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(res.GetResponseStream()))
                {
                    string json = sr.ReadToEnd();
                    // Extraer campo "version" del JSON
                    serverVersion = ExtractJsonValue(json, "version");
                }

                if (string.IsNullOrEmpty(serverVersion) || serverVersion == AppVersion)
                    return; // ya estamos actualizados

                // 2. Notificar al usuario
                SetStatus("🔄 Nueva versión " + serverVersion + " — actualizando...", Color.FromArgb(99, 179, 237));
                UpdateVersionLabel("🔄 Actualizando a " + serverVersion + "...");

                // 3. Descargar código fuente nuevo
                string tempDir  = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sercom_update");
                System.IO.Directory.CreateDirectory(tempDir);
                string srcPath  = System.IO.Path.Combine(tempDir, "SoporteRemotoGUI_new.cs");
                string exePath  = System.IO.Path.Combine(tempDir, "SercomSoporte_new.exe");
                string icoPath  = System.IO.Path.Combine(tempDir, "favicon.ico");

                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers.Add("x-sercom-agent-token", AgentToken);
                    wc.DownloadFile(ServerUrl + "/soporte/download/gui-src", srcPath);
                }

                // 4. Descargar ícono
                try
                {
                    using (var wc = new System.Net.WebClient())
                        wc.DownloadFile(ServerUrl + "/soporte/download/favicon", icoPath);
                }
                catch { icoPath = null; }

                // 5. Compilar nueva versión con csc.exe
                string cscPath = System.IO.Path.Combine(
                    System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                    "csc.exe");
                if (!System.IO.File.Exists(cscPath))
                    cscPath = @"C:\Windows\Microsoft.NET\Framework4.0.30319\csc.exe";

                string iconArg = (icoPath != null && System.IO.File.Exists(icoPath)) ? (" /win32icon:\"" + icoPath + "\"") : "";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = cscPath,
                    Arguments              = "/target:winexe /out:\"" + exePath + "\"" + iconArg + " \"" + srcPath + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };

                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    proc.WaitForExit(60000);
                    if (proc.ExitCode != 0) return; // fallo de compilación — abortar
                }

                if (!System.IO.File.Exists(exePath)) return;

                // 6. Crear script batch que: espera que salgamos → copia → lanza nuevo
                string currentExe  = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string updaterBat  = System.IO.Path.Combine(tempDir, "updater.bat");
                System.IO.File.WriteAllText(updaterBat,
                    "@echo off\r\n" +
                    "timeout /t 2 /nobreak >nul\r\n" +
                    "copy /y \"" + exePath + "\" \"" + currentExe + "\" >nul\r\n" +
                    "start \"\" \"" + currentExe + "\"\r\n" +
                    "del \"" + updaterBat + "\"\r\n");

                // 7. Lanzar batch y salir
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "cmd.exe",
                    Arguments       = "/c \"" + updaterBat + "\"",
                    CreateNoWindow  = true,
                    UseShellExecute = false
                });

                _running = false;
                this.Invoke(new MethodInvoker(() => Application.Exit()));
            }
            catch { /* auto-update silencioso — no interrumpir operación */ }
        }

        private void UpdateVersionLabel(string text)
        {
            if (_lblVersion == null) return;
            if (_lblVersion.InvokeRequired)
                _lblVersion.Invoke(new MethodInvoker(() => _lblVersion.Text = text));
            else
                _lblVersion.Text = text;
        }

        // =====================================================================
        //  Envío diferido de datos de hardware al servidor
        // =====================================================================
        private void SendHardwareHealth()
        {
            try
            {
                string script = GetHealthScript();
                string result = ExecutePowerShell(script).Trim();
                if (string.IsNullOrEmpty(result) || !result.StartsWith("{")) return;

                string body = string.Format("{{\"id\":\"{0}\",\"hostname\":\"{1}\",\"health\":{2}}}", _supportId, _hostname, result);
                byte[] data = Encoding.UTF8.GetBytes(body);

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ServerUrl + "/soporte/register");
                req.Method        = "POST";
                req.ContentType   = "application/json";
                req.ContentLength = data.Length;
                req.Timeout       = 15000;
                req.Headers.Add("x-sercom-agent-token", AgentToken);
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (req.GetResponse()) { }
            }
            catch { }
        }

        private string GetHealthScript()
        {
            // Script PowerShell expandido: hardware completo para el panel de soporte
            return @"
try {
  $hw  = Get-WmiObject Win32_ComputerSystem;
  $cpu = Get-WmiObject Win32_Processor | Select-Object -First 1;
  $os  = Get-WmiObject Win32_OperatingSystem;
  $enc = Get-WmiObject Win32_SystemEnclosure;

  # Tipo de dispositivo por ChassisType
  $ct = if ($enc) { [int]($enc.ChassisTypes | Select-Object -First 1) } else { 0 };
  $devType = switch ($ct) {
    {$_ -in @(8,9,10,11,12,14,18,21)} { 'Laptop' }
    {$_ -in @(30,31,32)} { 'Tablet' }
    {$_ -in @(3,4,5,6,7,15,16)} { 'PC' }
    default { 'PC' }
  };

  # RAM: total y módulos
  $ramModules = Get-WmiObject Win32_PhysicalMemory | ForEach-Object {
    $ddrMap = @{20='DDR';21='DDR2';24='DDR3';26='DDR4';34='DDR5';0='Unknown'};
    $ddrType = if ($ddrMap.ContainsKey([int]$_.SMBIOSMemoryType)) { $ddrMap[[int]$_.SMBIOSMemoryType] } else { 'DDR' };
    $capGB = [math]::Round($_.Capacity/1GB,0);
    [PSCustomObject]@{ slot=$_.DeviceLocator; cap=$capGB; type=$ddrType; speed=$_.Speed }
  };
  $ramTotal = [math]::Round($os.TotalVisibleMemorySize/1KB,1);
  $ramFree  = [math]::Round($os.FreePhysicalMemory/1KB,1);
  $ramJson  = ($ramModules | ForEach-Object { '{""slot"":""{0}"",""gb"":{1},""type"":""{2}"",""speed"":{3}}' -f $_.slot,$_.cap,$_.type,$(if($_.speed){$_.speed}else{0}) }) -join ',';

  # Discos
  $disks = Get-WmiObject Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
    $total = [math]::Round($_.Size/1GB,1);
    $free  = [math]::Round($_.FreeSpace/1GB,1);
    $used  = [math]::Round($total - $free,1);
    [PSCustomObject]@{ letter=$_.DeviceID; total=$total; used=$used; free=$free }
  };
  $diskJson = ($disks | ForEach-Object { '{""drive"":""{0}"",""total"":{1},""used"":{2},""free"":{3}}' -f $_.letter,$_.total,$_.used,$_.free }) -join ',';

  $out = '{""deviceType"":""{0}"",""manufacturer"":""{1}"",""model"":""{2}"",""cpu"":""{3}"",""ramGB"":{4},""ramFreeGB"":{5},""ramModules"":[{6}],""disks"":[{7}]}' -f `
    $devType,
    $hw.Manufacturer.Trim(),
    $hw.Model.Trim(),
    $cpu.Name.Trim(),
    $ramTotal,
    $ramFree,
    $ramJson,
    $diskJson;
  $out
} catch { '{""error"":""WMI_FAIL""}' }
";
        }

        // =====================================================================
        //  ID Persistente basado en serial del hardware
        // =====================================================================
        private string GeneratePersistentId()
        {
            try
            {
                string serial = GetSystemSerial();
                if (!string.IsNullOrWhiteSpace(serial) && serial.Length > 4 &&
                    !serial.ToUpper().Contains("FILL") && !serial.ToUpper().Contains("DEFAULT") &&
                    !serial.ToUpper().Contains("NONE") && !serial.ToUpper().Contains("N/A"))
                {
                    int hash = 0;
                    foreach (char c in serial.ToUpper()) hash = hash * 31 + (int)c;
                    hash = Math.Abs(hash);
                    return string.Format("{0}-{1}", 1000 + (hash % 9000), 1000 + ((hash / 9000 + 1) % 9000));
                }
            }
            catch { }

            int fallback = 0;
            foreach (char c in Environment.MachineName.ToUpper()) fallback = fallback * 31 + (int)c;
            fallback = Math.Abs(fallback);
            return string.Format("{0}-{1}", 1000 + (fallback % 9000), 1000 + ((fallback / 9000 + 1) % 9000));
        }

        private string GetSystemSerial()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = "powershell.exe",
                    Arguments              = "-NoProfile -NonInteractive -Command \"(Get-WmiObject Win32_BIOS).SerialNumber\"",
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string result = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(5000);
                    return result;
                }
            }
            catch { return null; }
        }

        // =====================================================================
        //  Utilidades
        // =====================================================================
        private string ExtractJsonValue(string json, string key)
        {
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search);
            if (idx < 0) return null;
            idx += search.Length;
            if (idx >= json.Length) return null;
            if (json[idx] == '"')
            {
                idx++;
                int end = idx;
                while (end < json.Length && !(json[end] == '"' && json[end - 1] != '\\')) end++;
                return json.Substring(idx, end - idx).Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\r", "\r");
            }
            else
            {
                int end = idx;
                while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']') end++;
                return json.Substring(idx, end - idx).Trim();
            }
        }

        private double ParseDouble(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            double val;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val)) return val;
            return 0;
        }
    }
}
