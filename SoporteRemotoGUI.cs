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
        private const string ServerUrl   = "https://soporte.sercommx.com";
        private const string RelayWsUrl  = "wss://soporte.sercommx.com";
        private const string AgentToken  = "SercomAgentToken2026SecureHashKey";
        private const string AppVersion  = "v4.0.0"; // inyectado por el servidor al descargar

        // ── Recursos gráficos inyectados en caliente por Express ─────────────
        private static readonly string LogoBase64 = "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";
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

            bool isFirstFrame = true;
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

                                if (!isFirstFrame && !IsCellDirty(current, _prevScreenBitmap, x, y, w, h)) continue;

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

                        isFirstFrame = false;
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

            bool isFirstFrame = true;
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
  $manufacturer = ""Desconocido""; $model = ""Desconocido"";
  try { $hw = Get-WmiObject Win32_ComputerSystem; $manufacturer = $hw.Manufacturer.Trim(); $model = $hw.Model.Trim(); } catch {}

  $cpuName = ""Desconocido"";
  try { $cpu = Get-WmiObject Win32_Processor | Select-Object -First 1; $cpuName = $cpu.Name.Trim(); } catch {}

  $devType = ""PC"";
  try {
    $enc = Get-WmiObject Win32_SystemEnclosure;
    if ($enc) {
      $ct = [int]($enc.ChassisTypes | Select-Object -First 1);
      $devType = switch ($ct) {
        {$_ -in @(8,9,10,11,12,14,18,21)} { ""Laptop"" }
        {$_ -in @(30,31,32)} { ""Tablet"" }
        {$_ -in @(3,4,5,6,7,15,16)} { ""PC"" }
        default { ""PC"" }
      };
    }
  } catch {}

  $ramTotal = 0; $ramFree = 0;
  try {
    $os = Get-WmiObject Win32_OperatingSystem;
    $ramTotal = [math]::Round($os.TotalVisibleMemorySize/1024/1024,1);
    $ramFree  = [math]::Round($os.FreePhysicalMemory/1024/1024,1);
  } catch {}

  $ramJson = """";
  try {
    $ramModules = Get-WmiObject Win32_PhysicalMemory | ForEach-Object {
      $ddrMap = @{20='DDR';21='DDR2';24='DDR3';26='DDR4';34='DDR5';0='Unknown'};
      $ddrType = if ($ddrMap.ContainsKey([int]$_.SMBIOSMemoryType)) { $ddrMap[[int]$_.SMBIOSMemoryType] } else { 'DDR' };
      $capGB = [math]::Round($_.Capacity/1GB,0);
      $speed = if ($_.Speed) { $_.Speed } else { 0 };
      '{""slot"":""' + $_.DeviceLocator + '"",""gb"":' + $capGB + ',""type"":""' + $ddrType + '"",""speed"":' + $speed + '}'
    };
    $ramJson = $ramModules -join ',';
  } catch {}

  $diskJson = """";
  try {
    $disks = Get-WmiObject Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
      $total = [math]::Round($_.Size/1GB,1);
      $free  = [math]::Round($_.FreeSpace/1GB,1);
      $used  = [math]::Round($total - $free,1);
      '{""drive"":""' + $_.DeviceID + '"",""total"":' + $total + ',""used"":' + $used + ',""free"":' + $free + '}'
    };
    $diskJson = $disks -join ',';
  } catch {}

  '{""deviceType"":""' + $devType + '"",""manufacturer"":""' + $manufacturer + '"",""model"":""' + $model + '"",""cpu"":""' + $cpuName + '"",""ramGB"":' + $ramTotal + ',""ramFreeGB"":' + $ramFree + ',""ramModules"":[' + $ramJson + '],""disks"":[' + $diskJson + ']}'
} catch { '{""error"":""WMI_EXCEPTION"",""details"":""' + $_.Exception.Message + '""}' }
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
