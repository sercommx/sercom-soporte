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
