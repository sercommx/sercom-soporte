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
//  SercomDesk - Agente de Soporte Remoto para Windows
//  Sercom Mx © 2026 | Versión v2.0.0
//  Compilar: csc.exe /out:SoporteRemotoGUI.exe /target:winexe /win32icon:favicon.ico SoporteRemotoGUI.cs
// =============================================================================
namespace SercomSoporte
{
    // =========================================================================
    //  P/Invoke Win32 para inyección de entradas (mouse y teclado)
    // =========================================================================
    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUT_UNION U;
    }

    internal static class Win32
    {
        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }

    // =========================================================================
    //  Formulario Principal (WinForms)
    // =========================================================================
    public class SoporteRemotoGUI : Form
    {
        // --- Versión del agente ---
        private const string AppVersion = "v2.0.0";

        // --- Servidor de API Express (SV1) ---
        private const string ServerUrl = "http://129.159.72.206:6001";

        // --- Servidor de Relay WebSocket (SV1) ---
        private const string RelayWsUrl = "ws://129.159.72.206:6002";

        // --- Token de autenticación del agente (compilado) ---
        private const string AgentToken = "SercomAgentToken2026SecureHashKey";

        // --- Recursos gráficos inyectados en caliente por Express ---
        private static readonly string LogoBase64 = "";
        private static readonly string IconBase64 = "";

        // --- Estado del agente ---
        private readonly string _supportId;
        private readonly string _hostname;
        private bool _running = true;
        private bool _streaming = false;
        private bool _elevated = false;

        // --- WebSocket de streaming ---
        private ClientWebSocket _wsClient;
        private CancellationTokenSource _wsCts;

        // --- Captura de pantalla (Dirty Rectangles) ---
        private Bitmap _prevScreenBitmap;
        private readonly int _gridCols = 16;
        private readonly int _gridRows = 9;

        // --- Controles de interfaz ---
        private PictureBox picLogo;
        private Label lblIdTitle, lblId, lblStatus, lblStreamStatus;
        private Button btnClose, btnRemoteControl;
        private Panel pnlIdContainer;

        // --- Hilo de soporte ---
        private Thread _supportThread;

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
            _supportId = GeneratePersistentId();
            _hostname = Environment.MachineName.Replace(" ", "").Replace(".", "");

            InitializeComponent();

            lblId.Text = _supportId;

            _supportThread = new Thread(RunSupportLoop) { IsBackground = true };
            _supportThread.Start();
        }

        // =====================================================================
        //  Generación del ID persistente de 8 dígitos (XXXX-XXXX)
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
                    foreach (char c in serial.ToUpper())
                        hash = hash * 31 + (int)c;
                    hash = Math.Abs(hash);
                    int part1 = 1000 + (hash % 9000);
                    int part2 = 1000 + ((hash / 9000 + 1) % 9000);
                    return string.Format("{0}-{1}", part1, part2);
                }
            }
            catch { }

            int fallback = 0;
            foreach (char c in Environment.MachineName.ToUpper())
                fallback = fallback * 31 + (int)c;
            fallback = Math.Abs(fallback);
            return string.Format("{0}-{1}", 1000 + (fallback % 9000), 1000 + ((fallback / 9000 + 1) % 9000));
        }

        private string GetSystemSerial()
        {
            try
            {
                string board = RunSilent("wmic baseboard get serialnumber /value").Replace("SerialNumber=", "").Trim();
                if (!string.IsNullOrWhiteSpace(board) && board.Length > 4) return board;
            }
            catch { }
            try
            {
                string bios = RunSilent("wmic bios get serialnumber /value").Replace("SerialNumber=", "").Trim();
                if (!string.IsNullOrWhiteSpace(bios) && bios.Length > 4) return bios;
            }
            catch { }
            return Environment.MachineName;
        }

        private string RunSilent(string cmd)
        {
            var p = new System.Diagnostics.Process();
            p.StartInfo.FileName = "cmd.exe";
            p.StartInfo.Arguments = "/c " + cmd;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output.Trim();
        }

        // =====================================================================
        //  Script PowerShell de Diagnóstico de Salud del Sistema
        // =====================================================================
        private string GetHealthReportScript()
        {
            return @"
$ErrorActionPreference = 'SilentlyContinue'
$r = @{}
try {
    $cpu = Get-WmiObject Win32_Processor | Select-Object Name, LoadPercentage
    $r['cpu'] = @{ name = ($cpu.Name -replace '\s+',' ').Trim(); load = [int]$cpu.LoadPercentage }
} catch {}
try {
    $ramModules = Get-WmiObject Win32_PhysicalMemory | ForEach-Object {
        @{ capacity = [math]::Round($_.Capacity/1GB,1); speed = $_.Speed; slot = $_.DeviceLocator }
    }
    $totalRam = [math]::Round(($ramModules | Measure-Object -Property capacity -Sum).Sum, 1)
    $r['ram'] = @{ total = $totalRam; modules = $ramModules }
} catch {}
try {
    $disks = Get-WmiObject Win32_DiskDrive | ForEach-Object {
        $size = [math]::Round($_.Size/1GB)
        @{ model = $_.Model; serial = $_.SerialNumber; size = $size; status = $_.Status }
    }
    $r['disks'] = $disks
} catch {}
try {
    $os = Get-WmiObject Win32_OperatingSystem
    $freeGB = [math]::Round($os.FreePhysicalMemory/1MB, 1)
    $r['os'] = @{ name = $os.Caption; version = $os.Version; freeRam = $freeGB }
} catch {}
try {
    $bat = Get-WmiObject Win32_Battery
    if ($bat) {
        $r['battery'] = @{
            status = $bat.BatteryStatus
            charge = $bat.EstimatedChargeRemaining
            life = $bat.EstimatedRunTime
        }
    }
} catch {}
try {
    $events = Get-EventLog -LogName System -EntryType Error -Newest 5 |
              Select-Object -ExpandProperty Message |
              ForEach-Object { ($_ -split '\n')[0].Trim() }
    $r['recentErrors'] = $events
} catch {}
$r | ConvertTo-Json -Depth 3 -Compress";
        }

        // =====================================================================
        //  Inicialización de la Interfaz Gráfica (WinForms)
        // =====================================================================
        private void InitializeComponent()
        {
            this.Text = "Asistencia Tecnica Remota - Sercom Mx " + AppVersion;
            this.Size = new Size(400, 360);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.White;

            // Icono de ventana desde Base64
            try
            {
                byte[] iconBytes = Convert.FromBase64String(IconBase64);
                using (MemoryStream ms = new MemoryStream(iconBytes))
                    this.Icon = new Icon(ms);
            }
            catch { }

            // Logotipo de Sercom Mx
            picLogo = new PictureBox();
            picLogo.Size = new Size(220, 55);
            picLogo.Location = new Point(82, 16);
            picLogo.SizeMode = PictureBoxSizeMode.Zoom;
            try
            {
                byte[] imgBytes = Convert.FromBase64String(LogoBase64);
                using (MemoryStream ms = new MemoryStream(imgBytes))
                    picLogo.Image = Image.FromStream(ms);
            }
            catch { }

            // Separador superior
            Panel topDivider = new Panel();
            topDivider.BackColor = Color.FromArgb(229, 231, 235);
            topDivider.Size = new Size(376, 1);
            topDivider.Location = new Point(12, 80);

            // Panel contenedor del ID
            pnlIdContainer = new Panel();
            pnlIdContainer.Size = new Size(376, 90);
            pnlIdContainer.Location = new Point(12, 90);
            pnlIdContainer.BackColor = Color.FromArgb(248, 249, 250);
            pnlIdContainer.Paint += (s, e) => {
                ControlPaint.DrawBorder(e.Graphics, pnlIdContainer.ClientRectangle,
                    Color.FromArgb(229, 231, 235), ButtonBorderStyle.Solid);
            };

            lblIdTitle = new Label();
            lblIdTitle.Text = "CÓDIGO DE SOPORTE";
            lblIdTitle.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            lblIdTitle.ForeColor = Color.FromArgb(107, 114, 128);
            lblIdTitle.Size = new Size(372, 22);
            lblIdTitle.Location = new Point(2, 12);
            lblIdTitle.TextAlign = ContentAlignment.MiddleCenter;

            lblId = new Label();
            lblId.Text = "----";
            lblId.Font = new Font("Segoe UI Semibold", 26f, FontStyle.Bold);
            lblId.ForeColor = Color.FromArgb(10, 37, 64);
            lblId.Size = new Size(372, 50);
            lblId.Location = new Point(2, 34);
            lblId.TextAlign = ContentAlignment.MiddleCenter;

            pnlIdContainer.Controls.Add(lblIdTitle);
            pnlIdContainer.Controls.Add(lblId);

            // Etiqueta de estado de conexión
            lblStatus = new Label();
            lblStatus.Text = "🔌 Conectando con el servidor...";
            lblStatus.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Regular);
            lblStatus.ForeColor = Color.FromArgb(245, 158, 11);
            lblStatus.Size = new Size(376, 20);
            lblStatus.Location = new Point(12, 192);
            lblStatus.TextAlign = ContentAlignment.MiddleCenter;

            // Etiqueta de estado del streaming
            lblStreamStatus = new Label();
            lblStreamStatus.Text = "";
            lblStreamStatus.Font = new Font("Segoe UI", 8.5f, FontStyle.Italic);
            lblStreamStatus.ForeColor = Color.FromArgb(107, 114, 128);
            lblStreamStatus.Size = new Size(376, 18);
            lblStreamStatus.Location = new Point(12, 215);
            lblStreamStatus.TextAlign = ContentAlignment.MiddleCenter;

            // Separador inferior
            Panel bottomDivider = new Panel();
            bottomDivider.BackColor = Color.FromArgb(229, 231, 235);
            bottomDivider.Size = new Size(376, 1);
            bottomDivider.Location = new Point(12, 242);

            // Botón de control remoto
            btnRemoteControl = new Button();
            btnRemoteControl.Text = "🖥  Compartir Escritorio";
            btnRemoteControl.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            btnRemoteControl.Size = new Size(376, 38);
            btnRemoteControl.Location = new Point(12, 252);
            btnRemoteControl.BackColor = Color.FromArgb(16, 185, 129); // Verde conexión
            btnRemoteControl.ForeColor = Color.White;
            btnRemoteControl.FlatStyle = FlatStyle.Flat;
            btnRemoteControl.FlatAppearance.BorderSize = 0;
            btnRemoteControl.Cursor = Cursors.Hand;
            btnRemoteControl.Click += OnRemoteControlClick;
            btnRemoteControl.MouseEnter += (s, e) => {
                if (!_streaming)
                    btnRemoteControl.BackColor = Color.FromArgb(5, 150, 105);
            };
            btnRemoteControl.MouseLeave += (s, e) => {
                if (!_streaming)
                    btnRemoteControl.BackColor = Color.FromArgb(16, 185, 129);
            };

            // Botón finalizar
            btnClose = new Button();
            btnClose.Text = "Finalizar Asistencia";
            btnClose.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            btnClose.Size = new Size(376, 38);
            btnClose.Location = new Point(12, 298);
            btnClose.BackColor = Color.FromArgb(10, 37, 64);
            btnClose.ForeColor = Color.White;
            btnClose.FlatStyle = FlatStyle.Flat;
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Cursor = Cursors.Hand;
            btnClose.Click += (s, e) => this.Close();
            btnClose.MouseEnter += (s, e) => btnClose.BackColor = Color.FromArgb(20, 60, 95);
            btnClose.MouseLeave += (s, e) => btnClose.BackColor = Color.FromArgb(10, 37, 64);

            // Añadir controles
            this.Controls.Add(picLogo);
            this.Controls.Add(topDivider);
            this.Controls.Add(pnlIdContainer);
            this.Controls.Add(lblStatus);
            this.Controls.Add(lblStreamStatus);
            this.Controls.Add(bottomDivider);
            this.Controls.Add(btnRemoteControl);
            this.Controls.Add(btnClose);

            // Limpieza al cerrar
            this.FormClosing += (s, e) => {
                _running = false;
                StopStreamingAsync().Wait();
                CleanupSystem();
            };
        }

        // =====================================================================
        //  Eventos de Interfaz
        // =====================================================================
        private void OnRemoteControlClick(object sender, EventArgs e)
        {
            if (!_streaming)
            {
                btnRemoteControl.Text = "⏹  Detener Transmisión";
                btnRemoteControl.BackColor = Color.FromArgb(220, 38, 38);
                btnRemoteControl.MouseEnter -= null;
                btnRemoteControl.MouseLeave -= null;
                UpdateStreamStatus("📡 Iniciando transmisión segura...", Color.FromArgb(16, 185, 129));
                Task.Run(() => StartStreamingAsync());
            }
            else
            {
                Task.Run(() => StopStreamingAsync());
                btnRemoteControl.Text = "🖥  Compartir Escritorio";
                btnRemoteControl.BackColor = Color.FromArgb(16, 185, 129);
                UpdateStreamStatus("", Color.Gray);
            }
        }

        private void UpdateStatus(string text, Color color)
        {
            if (this.InvokeRequired)
                this.Invoke(new MethodInvoker(() => UpdateStatus(text, color)));
            else
            {
                lblStatus.Text = text;
                lblStatus.ForeColor = color;
            }
        }

        private void UpdateStreamStatus(string text, Color color)
        {
            if (this.InvokeRequired)
                this.Invoke(new MethodInvoker(() => UpdateStreamStatus(text, color)));
            else
            {
                lblStreamStatus.Text = text;
                lblStreamStatus.ForeColor = color;
            }
        }

        // =====================================================================
        //  Loop Principal de Soporte (Sondeo HTTP de comandos)
        // =====================================================================
        private void RunSupportLoop()
        {
            while (_running)
            {
                UpdateStatus("🔌 Conectando con el servidor...", Color.FromArgb(245, 158, 11));
                if (RegisterAgent())
                {
                    UpdateStatus("✅ Conectado — Esperando instrucciones...", Color.FromArgb(16, 185, 129));
                    while (_running)
                    {
                        if (!PollCommands()) break;
                        Thread.Sleep(1000);
                    }
                }
                else
                {
                    UpdateStatus("⚠️ Reintentando en 5s...", Color.FromArgb(239, 68, 68));
                    Thread.Sleep(5000);
                }
            }
        }

        private bool RegisterAgent()
        {
            try
            {
                string healthJson = ExecutePowerShell(GetHealthReportScript());
                if (string.IsNullOrEmpty(healthJson) || healthJson.Contains("[ERROR]"))
                    healthJson = "null";

                string json = string.Format("{{\"id\":\"{0}\",\"hostname\":\"{1}\",\"health\":{2}}}",
                    _supportId, _hostname, healthJson.Trim());
                byte[] data = Encoding.UTF8.GetBytes(json);

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ServerUrl + "/soporte/register");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.ContentLength = data.Length;
                req.Timeout = 10000;
                req.Headers.Add("x-sercom-agent-token", AgentToken);

                using (Stream s = req.GetRequestStream())
                    s.Write(data, 0, data.Length);

                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                    return res.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        private bool PollCommands()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ServerUrl + "/soporte/poll?id=" + _supportId);
                req.Method = "GET";
                req.Timeout = 4000;
                req.Headers.Add("x-sercom-agent-token", AgentToken);

                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(res.GetResponseStream()))
                {
                    string text = reader.ReadToEnd();
                    if (text.Contains("\"command\":null") || string.IsNullOrEmpty(text))
                        return true;

                    string cmdId = ExtractJsonValue(text, "id");
                    string cmdText = ExtractJsonValue(text, "text");

                    if (!string.IsNullOrEmpty(cmdId) && !string.IsNullOrEmpty(cmdText))
                    {
                        // Comandos especiales del sistema de control remoto
                        if (cmdText.StartsWith("__RELAY_START__"))
                        {
                            Task.Run(() => StartStreamingAsync());
                            SendResponse(cmdId, "STREAMING_STARTED");
                        }
                        else if (cmdText.StartsWith("__RELAY_STOP__"))
                        {
                            Task.Run(() => StopStreamingAsync());
                            SendResponse(cmdId, "STREAMING_STOPPED");
                        }
                        else
                        {
                            UpdateStatus("⚡ Ejecutando acción remota...", Color.FromArgb(245, 158, 11));
                            string output = ExecutePowerShell(cmdText);
                            SendResponse(cmdId, output);
                            UpdateStatus("✅ Conectado — Esperando instrucciones...", Color.FromArgb(16, 185, 129));
                        }
                    }
                }
                return true;
            }
            catch (WebException) { return false; }
            catch { return true; }
        }

        private void SendResponse(string cmdId, string output)
        {
            try
            {
                string escaped = output
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n");

                string json = string.Format("{{\"id\":\"{0}\",\"cmdId\":\"{1}\",\"output\":\"{2}\"}}",
                    _supportId, cmdId, escaped);
                byte[] data = Encoding.UTF8.GetBytes(json);

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ServerUrl + "/soporte/response");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.ContentLength = data.Length;
                req.Timeout = 5000;
                req.Headers.Add("x-sercom-agent-token", AgentToken);

                using (Stream s = req.GetRequestStream())
                    s.Write(data, 0, data.Length);

                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse()) { }
            }
            catch { }
        }

        // =====================================================================
        //  Streaming WebSocket (Fase 3+4: Pantalla, Mouse, Teclado, Portapapeles)
        // =====================================================================
        private async Task StartStreamingAsync()
        {
            _streaming = true;
            _wsCts = new CancellationTokenSource();
            _wsClient = new ClientWebSocket();
            _wsClient.Options.SetRequestHeader("x-sercom-agent-token", AgentToken);

            try
            {
                string wsUri = string.Format("{0}?type=agent&id={1}", RelayWsUrl, _supportId);
                await _wsClient.ConnectAsync(new Uri(wsUri), _wsCts.Token);

                UpdateStreamStatus("📡 Transmitiendo escritorio en tiempo real", Color.FromArgb(16, 185, 129));

                // Lanzar tareas paralelas
                Task sendFrames = SendScreenFramesAsync(_wsCts.Token);
                Task receiveInputs = ReceiveInputCommandsAsync(_wsCts.Token);

                await Task.WhenAny(sendFrames, receiveInputs);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                UpdateStreamStatus("❌ Error de streaming: " + ex.Message, Color.FromArgb(239, 68, 68));
            }
            finally
            {
                _streaming = false;
                try { _wsClient?.Dispose(); } catch { }
                UpdateStreamStatus("⏹ Transmisión detenida", Color.FromArgb(107, 114, 128));
            }
        }

        private async Task StopStreamingAsync()
        {
            _wsCts?.Cancel();
            if (_wsClient?.State == WebSocketState.Open)
            {
                try { await _wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch { }
            }
            _streaming = false;
            this.Invoke(new MethodInvoker(() => {
                btnRemoteControl.Text = "🖥  Compartir Escritorio";
                btnRemoteControl.BackColor = Color.FromArgb(16, 185, 129);
            }));
        }

        // =====================================================================
        //  Captura de Pantalla con Dirty Rectangles (optimización de red)
        // =====================================================================
        private async Task SendScreenFramesAsync(CancellationToken ct)
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            int cellW = screen.Width / _gridCols;
            int cellH = screen.Height / _gridRows;

            _prevScreenBitmap = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);

            while (!ct.IsCancellationRequested && _wsClient?.State == WebSocketState.Open)
            {
                try
                {
                    using (Bitmap current = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb))
                    using (Graphics g = Graphics.FromImage(current))
                    {
                        g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);

                        // Detectar celdas modificadas (Dirty Rectangles)
                        for (int col = 0; col < _gridCols; col++)
                        {
                            for (int row = 0; row < _gridRows; row++)
                            {
                                int x = col * cellW;
                                int y = row * cellH;
                                int w = Math.Min(cellW, screen.Width - x);
                                int h = Math.Min(cellH, screen.Height - y);

                                if (IsCellDirty(current, _prevScreenBitmap, x, y, w, h))
                                {
                                    using (Bitmap cell = new Bitmap(w, h))
                                    using (Graphics gc = Graphics.FromImage(cell))
                                    using (MemoryStream ms = new MemoryStream())
                                    {
                                        gc.DrawImage(current, 0, 0, new Rectangle(x, y, w, h), GraphicsUnit.Pixel);

                                        var jpegEncoder = GetJpegEncoder();
                                        var encoderParams = new EncoderParameters(1);
                                        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 55L);
                                        cell.Save(ms, jpegEncoder, encoderParams);

                                        string b64 = Convert.ToBase64String(ms.ToArray());
                                        string msg = string.Format(
                                            "{{\"type\":\"frame\",\"col\":{0},\"row\":{1},\"cellW\":{2},\"cellH\":{3},\"x\":{4},\"y\":{5},\"sw\":{6},\"sh\":{7},\"data\":\"{8}\"}}",
                                            col, row, w, h, x, y, screen.Width, screen.Height, b64);

                                        byte[] msgBytes = Encoding.UTF8.GetBytes(msg);
                                        await _wsClient.SendAsync(
                                            new ArraySegment<byte>(msgBytes),
                                            WebSocketMessageType.Text, true, ct);
                                    }
                                }
                            }
                        }

                        // Copiar frame actual al previo
                        using (Graphics gPrev = Graphics.FromImage(_prevScreenBitmap))
                            gPrev.DrawImage(current, 0, 0);
                    }

                    // ~15 FPS
                    await Task.Delay(66, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(200, ct); }
            }
        }

        private bool IsCellDirty(Bitmap current, Bitmap prev, int x, int y, int w, int h)
        {
            // Muestra de 16 píxeles distribuidos para comparar la celda
            int step = Math.Max(1, (w * h) / 16);
            for (int i = 0; i < w * h; i += step)
            {
                int px = x + (i % w);
                int py = y + (i / w);
                if (px >= current.Width || py >= current.Height) continue;

                Color c1 = current.GetPixel(px, py);
                Color c2 = prev.GetPixel(px, py);

                int diff = Math.Abs(c1.R - c2.R) + Math.Abs(c1.G - c2.G) + Math.Abs(c1.B - c2.B);
                if (diff > 15) return true;
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
        //  Receptor de Comandos de Control Remoto (Mouse, Teclado, Portapapeles)
        // =====================================================================
        private async Task ReceiveInputCommandsAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[4096];
            var sb = new StringBuilder();

            while (!ct.IsCancellationRequested && _wsClient?.State == WebSocketState.Open)
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
                    }
                    while (!result.EndOfMessage);

                    string json = sb.ToString();
                    ProcessRemoteInput(json);
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(100, ct); }
            }
        }

        private void ProcessRemoteInput(string json)
        {
            try
            {
                string type = ExtractJsonValue(json, "type");

                switch (type)
                {
                    case "mouse_move":
                        HandleMouseMove(json);
                        break;
                    case "mouse_click":
                        HandleMouseClick(json);
                        break;
                    case "mouse_scroll":
                        HandleMouseScroll(json);
                        break;
                    case "key":
                        HandleKeyInput(json);
                        break;
                    case "set_clipboard":
                        HandleSetClipboard(json);
                        break;
                    case "get_clipboard":
                        HandleGetClipboard();
                        break;
                    case "panel_disconnected":
                        Task.Run(() => StopStreamingAsync());
                        break;
                }
            }
            catch { }
        }

        // =====================================================================
        //  Inyección de Entradas vía Win32 SendInput
        // =====================================================================
        private void HandleMouseMove(string json)
        {
            double nx = ParseDouble(ExtractJsonValue(json, "x"));
            double ny = ParseDouble(ExtractJsonValue(json, "y"));

            int screenW = Win32.GetSystemMetrics(0);
            int screenH = Win32.GetSystemMetrics(1);

            // Coordenadas absolutas (0-65535 para MOUSEEVENTF_ABSOLUTE)
            int absX = (int)(nx * 65535);
            int absY = (int)(ny * 65535);

            var inputs = new INPUT[1];
            inputs[0].type = Win32.INPUT_MOUSE;
            inputs[0].U.mi.dx = absX;
            inputs[0].U.mi.dy = absY;
            inputs[0].U.mi.dwFlags = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE;
            Win32.SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private void HandleMouseClick(string json)
        {
            double nx = ParseDouble(ExtractJsonValue(json, "x"));
            double ny = ParseDouble(ExtractJsonValue(json, "y"));
            string button = ExtractJsonValue(json, "button");
            string action = ExtractJsonValue(json, "action");

            int absX = (int)(nx * 65535);
            int absY = (int)(ny * 65535);

            uint downFlag, upFlag;
            switch (button)
            {
                case "right":
                    downFlag = Win32.MOUSEEVENTF_RIGHTDOWN;
                    upFlag = Win32.MOUSEEVENTF_RIGHTUP;
                    break;
                case "middle":
                    downFlag = Win32.MOUSEEVENTF_MIDDLEDOWN;
                    upFlag = Win32.MOUSEEVENTF_MIDDLEUP;
                    break;
                default: // left
                    downFlag = Win32.MOUSEEVENTF_LEFTDOWN;
                    upFlag = Win32.MOUSEEVENTF_LEFTUP;
                    break;
            }

            uint flags = Win32.MOUSEEVENTF_ABSOLUTE;
            if (action == "down") flags |= downFlag;
            else if (action == "up") flags |= upFlag;
            else { flags |= downFlag; }

            var inputs = new INPUT[action == "click" ? 2 : 1];
            inputs[0].type = Win32.INPUT_MOUSE;
            inputs[0].U.mi.dx = absX;
            inputs[0].U.mi.dy = absY;
            inputs[0].U.mi.dwFlags = flags;

            if (action == "click")
            {
                inputs[1].type = Win32.INPUT_MOUSE;
                inputs[1].U.mi.dx = absX;
                inputs[1].U.mi.dy = absY;
                inputs[1].U.mi.dwFlags = Win32.MOUSEEVENTF_ABSOLUTE | upFlag;
            }

            Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private void HandleMouseScroll(string json)
        {
            int delta = int.Parse(ExtractJsonValue(json, "delta") ?? "0");
            var inputs = new INPUT[1];
            inputs[0].type = Win32.INPUT_MOUSE;
            inputs[0].U.mi.dwFlags = Win32.MOUSEEVENTF_WHEEL;
            inputs[0].U.mi.mouseData = (uint)(delta * 120);
            Win32.SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private void HandleKeyInput(string json)
        {
            string vkStr = ExtractJsonValue(json, "keyCode");
            string action = ExtractJsonValue(json, "action");
            bool ctrl = json.Contains("\"ctrl\":true");
            bool alt = json.Contains("\"alt\":true");
            bool shift = json.Contains("\"shift\":true");

            if (string.IsNullOrEmpty(vkStr)) return;
            ushort vk = (ushort)(int.Parse(vkStr));

            // Modificadores primero si se presiona
            var modList = new System.Collections.Generic.List<INPUT>();
            if (action != "up")
            {
                if (ctrl) modList.Add(MakeKeyInput(0x11, false));   // VK_CONTROL
                if (alt) modList.Add(MakeKeyInput(0x12, false));    // VK_MENU
                if (shift) modList.Add(MakeKeyInput(0x10, false));  // VK_SHIFT
            }

            modList.Add(MakeKeyInput(vk, action == "up"));

            if (action == "up" || action == "click")
            {
                if (shift) modList.Add(MakeKeyInput(0x10, true));
                if (alt) modList.Add(MakeKeyInput(0x12, true));
                if (ctrl) modList.Add(MakeKeyInput(0x11, true));
            }

            Win32.SendInput((uint)modList.Count, modList.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        private INPUT MakeKeyInput(ushort vk, bool isUp)
        {
            INPUT inp = new INPUT();
            inp.type = Win32.INPUT_KEYBOARD;
            inp.U.ki.wVk = vk;
            inp.U.ki.dwFlags = isUp ? Win32.KEYEVENTF_KEYUP : 0;
            return inp;
        }

        // =====================================================================
        //  Portapapeles (Fase 2: Clipboard Sync)
        // =====================================================================
        private void HandleSetClipboard(string json)
        {
            string text = ExtractJsonValue(json, "text");
            if (!string.IsNullOrEmpty(text))
            {
                this.Invoke(new MethodInvoker(() => {
                    try { Clipboard.SetText(text); }
                    catch { }
                }));
            }
        }

        private void HandleGetClipboard()
        {
            this.Invoke(new MethodInvoker(async () => {
                try
                {
                    string text = Clipboard.GetText();
                    string msg = string.Format("{{\"type\":\"clipboard\",\"text\":\"{0}\"}}",
                        text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n"));
                    byte[] data = Encoding.UTF8.GetBytes(msg);
                    if (_wsClient?.State == WebSocketState.Open)
                        await _wsClient.SendAsync(new ArraySegment<byte>(data),
                            WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch { }
            }));
        }

        // =====================================================================
        //  Herramientas de Ejecución PowerShell Silenciosa
        // =====================================================================
        private string ExecutePowerShell(string command)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = string.Format("-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{0}\"",
                    command.Replace("\"", "\\\""));
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                var process = System.Diagnostics.Process.Start(psi);
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit(30000);

                if (!string.IsNullOrWhiteSpace(error) && string.IsNullOrWhiteSpace(output))
                    return "[ERROR] " + error.Trim();
                return string.IsNullOrWhiteSpace(output) ? "(sin salida)" : output.Trim();
            }
            catch (Exception ex)
            {
                return "[ERROR] " + ex.Message;
            }
        }

        private string ExtractJsonValue(string json, string key)
        {
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search);
            if (idx < 0) return null;
            idx += search.Length;

            if (json[idx] == '"')
            {
                idx++;
                int end = idx;
                while (end < json.Length && !(json[end] == '"' && json[end - 1] != '\\'))
                    end++;
                return json.Substring(idx, end - idx)
                    .Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\r", "\r");
            }
            else
            {
                int end = idx;
                while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']')
                    end++;
                return json.Substring(idx, end - idx).Trim();
            }
        }

        private double ParseDouble(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            double result;
            if (double.TryParse(s.Replace(",", "."),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result))
                return result;
            return 0;
        }

        // =====================================================================
        //  Limpieza del sistema (Autorun + Persistencia)
        // =====================================================================
        private void CleanupSystem()
        {
            // Cancelar WebSocket activo
            try { _wsCts?.Cancel(); } catch { }
            try { _wsClient?.Dispose(); } catch { }
            try { _prevScreenBitmap?.Dispose(); } catch { }
        }
    }
}
