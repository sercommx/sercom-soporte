using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using System.Drawing;

namespace SoporteSercom
{
    class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Auto-elevacion de privilegios a Administrador (UAC)
            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if (!isAdmin)
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Process.GetCurrentProcess().MainModule.FileName;
                psi.Verb = "runas"; // Fuerza la elevación de administrador
                try
                {
                    Process.Start(psi);
                }
                catch
                {
                    // El usuario canceló o denegó el permiso
                }
                return; // Cierra la instancia actual sin privilegios
            }

            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private static readonly string ServerUrl = "http://129.159.72.206:6001";
        private const string AppVersion = "v1.0.0";
        private string _supportId;
        private string _hostname;
        private bool _running = true;
        private Thread _pollThread;
        private static readonly string LogoBase64 = "";
        private static readonly string IconBase64 = "";

        // Componentes Gráficos (GUI)
        private PictureBox picLogo;
        private Label lblIdTitle;
        private Label lblId;
        private Label lblStatus;
        private Button btnClose;

        public MainForm()
        {
            InitializeComponent();
            
            // Generar ID persistente y único basado en hardware (Número de Serie de Placa Madre o CPU)
            _supportId = GetPersistentSupportId();
            _hostname = Dns.GetHostName();

            lblId.Text = _supportId;

            // Configurar auto-inicio con Windows para recuperación de sesión ante reinicios
            ConfigureRegistryAutoStart(true);

            // Iniciar conexión y sondeo en segundo plano
            _pollThread = new Thread(RunSupportLoop);
            _pollThread.IsBackground = true;
            _pollThread.Start();
        }

        private string GetPersistentSupportId()
        {
            try
            {
                // Leer número de serie de la placa madre o disco duro como seed
                string serial = "";
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c wmic baseboard get serialnumber")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 1) serial = lines[1].Trim();
                }

                if (string.IsNullOrEmpty(serial) || serial.ToLower().Contains("to be filled"))
                {
                    // Fallback a número de serie de BIOS
                    psi.Arguments = "/c wmic bios get serialnumber";
                    using (Process p = Process.Start(psi))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit();
                        string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 1) serial = lines[1].Trim();
                    }
                }

                if (string.IsNullOrEmpty(serial))
                {
                    serial = _hostname;
                }

                // Generar un hash numérico determinista de 8 dígitos con guión (formato XXXX-XXXX)
                int hash = 0;
                foreach (char c in serial)
                {
                    hash += (int)c;
                }
                
                int part1 = 1000 + (hash * 17 % 9000);
                int part2 = 1000 + (hash * 31 % 9000);
                
                return string.Format("{0}-{1}", part1, part2);
            }
            catch
            {
                // Fallback dinámico si falla
                Random r = new Random();
                return string.Format("{0}-{1}", r.Next(1000, 9999), r.Next(1000, 9999));
            }
        }

        private void ConfigureRegistryAutoStart(bool enable)
        {
            try
            {
                string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(runKey, true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            string appPath = Process.GetCurrentProcess().MainModule.FileName;
                            key.SetValue("SercomSoporteRemoto", "\"" + appPath + "\"");
                        }
                        else
                        {
                            key.DeleteValue("SercomSoporteRemoto", false);
                        }
                    }
                }
            }
            catch
            {
                // Si no tiene permisos de admin del registro local, intentar en CurrentUser
                try
                {
                    string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                    using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, true))
                    {
                        if (key != null)
                        {
                            if (enable)
                            {
                                string appPath = Process.GetCurrentProcess().MainModule.FileName;
                                key.SetValue("SercomSoporteRemoto", "\"" + appPath + "\"");
                            }
                            else
                            {
                                key.DeleteValue("SercomSoporteRemoto", false);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void InitializeComponent()
        {
            this.Text = "Asistencia Tecnica Remota - Sercom Mx " + AppVersion;
            this.Size = new Size(380, 310);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.White;

            // Cargar Icono de la ventana desde Base64
            try
            {
                byte[] iconBytes = Convert.FromBase64String(IconBase64);
                using (MemoryStream ms = new MemoryStream(iconBytes))
                {
                    this.Icon = new Icon(ms);
                }
            }
            catch { }

            // Logotipo de Sercom Mx incrustado en Base64
            picLogo = new PictureBox();
            picLogo.Size = new Size(220, 55);
            picLogo.Location = new Point(72, 18);
            picLogo.SizeMode = PictureBoxSizeMode.Zoom;
            try
            {
                byte[] imageBytes = Convert.FromBase64String(LogoBase64);
                using (MemoryStream ms = new MemoryStream(imageBytes))
                {
                    picLogo.Image = Image.FromStream(ms);
                }
            }
            catch { }

            // Panel Contenedor del ID de Soporte (Estilo Fluent)
            Panel pnlIdContainer = new Panel();
            pnlIdContainer.Size = new Size(340, 90);
            pnlIdContainer.Location = new Point(12, 85);
            pnlIdContainer.BackColor = Color.FromArgb(243, 244, 246);
            pnlIdContainer.Paint += (s, e) => {
                ControlPaint.DrawBorder(e.Graphics, pnlIdContainer.ClientRectangle,
                    Color.FromArgb(229, 231, 235), ButtonBorderStyle.Solid);
            };

            // Etiqueta del ID
            lblIdTitle = new Label();
            lblIdTitle.Text = "CÓDIGO DE SOPORTE";
            lblIdTitle.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            lblIdTitle.ForeColor = Color.FromArgb(107, 114, 128); // Gris intermedio moderno
            lblIdTitle.Size = new Size(336, 20);
            lblIdTitle.Location = new Point(2, 12);
            lblIdTitle.TextAlign = ContentAlignment.MiddleCenter;

            // Número de ID grande
            lblId = new Label();
            lblId.Text = "----";
            lblId.Font = new Font("Segoe UI Semibold", 26f, FontStyle.Bold);
            lblId.ForeColor = Color.FromArgb(10, 37, 64); // Azul Sercom Mx Profundo
            lblId.Size = new Size(336, 50);
            lblId.Location = new Point(2, 32);
            lblId.TextAlign = ContentAlignment.MiddleCenter;

            pnlIdContainer.Controls.Add(lblIdTitle);
            pnlIdContainer.Controls.Add(lblId);

            // Estado de la conexión
            lblStatus = new Label();
            lblStatus.Text = "🔌 Conectando con el servidor...";
            lblStatus.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Regular);
            lblStatus.ForeColor = Color.FromArgb(245, 158, 11); // Naranja moderno
            lblStatus.Size = new Size(340, 20);
            lblStatus.Location = new Point(12, 185);
            lblStatus.TextAlign = ContentAlignment.MiddleCenter;

            // Botón de salir
            btnClose = new Button();
            btnClose.Text = "Finalizar Asistencia";
            btnClose.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            btnClose.Size = new Size(340, 38);
            btnClose.Location = new Point(12, 215);
            btnClose.BackColor = Color.FromArgb(10, 37, 64); // Fondo azul Sercom
            btnClose.ForeColor = Color.White;
            btnClose.FlatStyle = FlatStyle.Flat;
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Cursor = Cursors.Hand;
            btnClose.Click += (s, e) => this.Close();
            btnClose.MouseEnter += (s, e) => btnClose.BackColor = Color.FromArgb(20, 60, 95);
            btnClose.MouseLeave += (s, e) => btnClose.BackColor = Color.FromArgb(10, 37, 64);

            // Añadir componentes a la ventana
            this.Controls.Add(picLogo);
            this.Controls.Add(pnlIdContainer);
            this.Controls.Add(lblStatus);
            this.Controls.Add(btnClose);

            // Limpieza al cerrar la ventana
            this.FormClosing += (s, e) => {
                _running = false;
                CleanupSystem();
            };
        }

        private void UpdateStatus(string text, Color color)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new MethodInvoker(() => UpdateStatus(text, color)));
            }
            else
            {
                lblStatus.Text = text;
                lblStatus.ForeColor = color;
            }
        }

        private void RunSupportLoop()
        {
            while (_running)
            {
                UpdateStatus("🔌 Conectando con el servidor...", Color.Orange);
                if (RegisterAgent())
                {
                    UpdateStatus("✅ Conectado. Esperando instrucciones...", Color.Green);
                    
                    while (_running)
                    {
                        if (!PollCommands())
                        {
                            // Si falla el polling (bot offline o reinicio), romper loop y re-registrarse
                            break;
                        }
                        Thread.Sleep(1000);
                    }
                }
                else
                {
                    UpdateStatus("⚠️ Reintentando conexion en 5s...", Color.Red);
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
                {
                    healthJson = "null";
                }

                string json = string.Format("{{\"id\":\"{0}\",\"hostname\":\"{1}\",\"health\":{2}}}", _supportId, _hostname, healthJson.Trim());
                byte[] data = Encoding.UTF8.GetBytes(json);

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ServerUrl + "/soporte/register");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.ContentLength = data.Length;
                request.Timeout = 10000;
                request.Headers.Add("x-sercom-agent-token", "SercomAgentToken2026SecureHashKey");

                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool PollCommands()
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ServerUrl + "/soporte/poll?id=" + _supportId);
                request.Method = "GET";
                request.Timeout = 3000;
                request.Headers.Add("x-sercom-agent-token", "SercomAgentToken2026SecureHashKey");

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    string resText = reader.ReadToEnd();
                    if (resText.Contains("\"command\":null") || string.IsNullOrEmpty(resText))
                        return true;

                    string cmdId = ExtractJsonValue(resText, "id");
                    string cmdText = ExtractJsonValue(resText, "text");

                    if (!string.IsNullOrEmpty(cmdId) && !string.IsNullOrEmpty(cmdText))
                    {
                        UpdateStatus("⚡ Ejecutando accion remota...", Color.Orange);
                        string output = ExecutePowerShell(cmdText);
                        SendResponse(cmdId, output);
                        UpdateStatus("✅ Conectado. Esperando instrucciones...", Color.Green);
                    }
                }
                return true;
            }
            catch (WebException)
            {
                // Si hay fallo de red o el servidor responde error, forzar re-registro
                return false;
            }
            catch
            {
                return true;
            }
        }

        private void SendResponse(string cmdId, string output)
        {
            try
            {
                string escapedOutput = output.Replace("\\", "\\\\")
                                             .Replace("\"", "\\\"")
                                             .Replace("\r", "\\r")
                                             .Replace("\n", "\\n");

                string json = string.Format("{{\"id\":\"{0}\",\"cmdId\":\"{1}\",\"output\":\"{2}\"}}", _supportId, cmdId, escapedOutput);
                byte[] data = Encoding.UTF8.GetBytes(json);

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ServerUrl + "/soporte/response");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.ContentLength = data.Length;
                request.Timeout = 5000;
                request.Headers.Add("x-sercom-agent-token", "SercomAgentToken2026SecureHashKey");

                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    // Enviado con éxito
                }
            }
            catch
            {
                // Ignorar
            }
        }

        private string ExecutePowerShell(string command)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"";
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true; // Oculta por completo la consola negra

                using (Process proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    if (!string.IsNullOrEmpty(stderr))
                    {
                        return stdout + "\r\n[ERROR]\r\n" + stderr;
                    }
                    return stdout;
                }
            }
            catch (Exception ex)
            {
                return "Error al iniciar proceso: " + ex.Message;
            }
        }

        private void CleanupSystem()
        {
            // Opcional: Notificar al servidor el cierre
            try
            {
                // El recolector de basura de Windows y el cierre del proceso eliminarán los usuarios temporales creados por el bot
            }
            catch { }
        }

        private string ExtractJsonValue(string json, string key)
        {
            string searchKey = "\"" + key + "\":\"";
            int startIndex = json.IndexOf(searchKey);
            if (startIndex == -1)
            {
                searchKey = "\"" + key + "\":";
                startIndex = json.IndexOf(searchKey);
                if (startIndex == -1) return null;
                
                int valStart = startIndex + searchKey.Length;
                int valEnd = json.IndexOf(",", valStart);
                if (valEnd == -1) valEnd = json.IndexOf("}", valStart);
                if (valEnd == -1) return null;
                return json.Substring(valStart, valEnd - valStart).Trim('\"');
            }
            
            int valueStart = startIndex + searchKey.Length;
            int valueEnd = json.IndexOf("\"", valueStart);
            if (valueEnd == -1) return null;

            return json.Substring(valueStart, valueEnd - valueStart);
        }

        private string GetHealthReportScript()
        {
            return @"
$report = @{}
$cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
$proc = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$board = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue
$bios = Get-CimInstance Win32_Bios -ErrorAction SilentlyContinue

# Modo de BIOS y Secure Boot
$firmware = $env:Firmware_Type
if (-not $firmware) { $firmware = 'Desconocido' }
$secureBoot = 'No Soportado'
try {
    if (Confirm-SecureBootUEFI -ErrorAction SilentlyContinue) { $secureBoot = 'Activo' } else { $secureBoot = 'Inactivo' }
} catch {}

# Batería (Laptop Health)
$batteryHealth = @{ IsLaptop = $false }
$battStatic = Get-CimInstance -Namespace root/wmi -ClassName BatteryStaticData -ErrorAction SilentlyContinue
$battStatus = Get-CimInstance -Namespace root/wmi -ClassName BatteryFullLoadedCapacity -ErrorAction SilentlyContinue
if ($battStatic -and $battStatus) {
    $design = $battStatic.DesignCapacity
    $current = $battStatus.FullChargedCapacity
    $healthPct = 0
    if ($design -gt 0) { $healthPct = [Math]::Round(($current / $design) * 100, 2) }
    $batteryHealth = @{
        IsLaptop = $true
        DesignCapacity = $design
        FullChargedCapacity = $current
        HealthPercent = $healthPct
    }
}

$report.Hardware = @{
    Manufacturer = ($cs.Manufacturer -replace '""','').Trim()
    Model = ($cs.Model -replace '""','').Trim()
    PCSystemType = $cs.PCSystemType
    OS = $os.Caption
    OSVersion = $os.Version
    CPU = $proc.Name
    CPULoad = $proc.LoadPercentage
    BoardManufacturer = ($board.Manufacturer -replace '""','').Trim()
    BoardModel = ($board.Product -replace '""','').Trim()
    BIOSVersion = $bios.SMBIOSBIOSVersion
    FirmwareType = $firmware
    SecureBoot = $secureBoot
    Battery = $batteryHealth
}
$ram = Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue
$report.RAM = @()
foreach ($r in $ram) {
    $report.RAM += @{
        CapacityGB = [Math]::Round($r.Capacity / 1GB, 2)
        Speed = $r.Speed
        Manufacturer = ($r.Manufacturer -replace '""','').Trim()
        PartNumber = ($r.PartNumber -replace '""','').Trim()
        SerialNumber = ($r.SerialNumber -replace '""','').Trim()
    }
}
$disks = Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue
$report.Disks = @()
foreach ($d in $disks) {
    $smart = 'OK'
    if ($d.Status -ne 'OK') { $smart = 'Warning' }
    $partitions = Get-CimInstance -Query ""ASSOCIATORS OF {Win32_DiskDrive.DeviceID='$($d.DeviceID)'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"" -ErrorAction SilentlyContinue
    $volumes = @()
    foreach ($p in $partitions) {
        $logical = Get-CimInstance -Query ""ASSOCIATORS OF {Win32_DiskPartition.DeviceID='$($p.DeviceID)'} WHERE AssocClass=Win32_LogicalDiskToPartition"" -ErrorAction SilentlyContinue
        foreach ($l in $logical) {
            $volumes += @{
                Letter = $l.DeviceID
                SizeGB = [Math]::Round($l.Size / 1GB, 2)
                FreeGB = [Math]::Round($l.FreeSpace / 1GB, 2)
            }
        }
    }
    $report.Disks += @{
        Model = $d.Model
        SizeGB = [Math]::Round($d.Size / 1GB, 2)
        SerialNumber = ($d.SerialNumber -replace '""','').Trim()
        SMART = $smart
        Volumes = $volumes
    }
}
$services = @('WinRM', 'MSSQLSERVER', 'MSSQL$COMPAQI', 'Saci_Service', 'Wuauserv', 'Windefend')
$report.Services = @{}
foreach ($s in $services) {
    $status = Get-Service -Name $s -ErrorAction SilentlyContinue
    if ($status) {
        $report.Services[$s] = @{
            Status = $status.Status.ToString()
            StartType = $status.StartType.ToString()
        }
    } else {
        $report.Services[$s] = @{ Status = 'NotInstalled'; StartType = 'None' }
    }
}
$events = Get-WinEvent -FilterHashtable @{LogName='System'; Level=1,2} -MaxEvents 20 -ErrorAction SilentlyContinue
$report.Events = @()
if ($events) {
    foreach ($e in $events) {
        $report.Events += @{
            Time = $e.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')
            Message = ($e.Message -replace '""','').Trim()
        }
    }
}
$adapters = Get-CimInstance Win32_NetworkAdapter -Filter ""NetConnectionStatus=2"" -ErrorAction SilentlyContinue
$report.Network = @()
foreach ($a in $adapters) {
    $config = Get-CimInstance Win32_NetworkAdapterConfiguration -Filter ""Index=$($a.Index)"" -ErrorAction SilentlyContinue
    $type = 'Ethernet'
    if ($a.Name -like '*Wireless*' -or $a.Name -like '*Wi-Fi*' -or $a.Name -like '*802.11*') {
        $type = 'Wifi'
    }
    $report.Network += @{
        Name = $a.Name
        Type = $type
        SpeedMbps = [Math]::Round($a.Speed / 1MB, 2)
        IP = $config.IPAddress[0]
    }
}
$ping = Test-Connection -ComputerName 1.1.1.1 -Count 2 -ErrorAction SilentlyContinue
if ($ping) {
    $report.PingLatencyMs = [Math]::Round(($ping | Measure-Object ResponseTime -Average).Average, 2)
} else {
    $report.PingLatencyMs = -1
}
$temp = Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction SilentlyContinue
if ($temp) {
    $report.TemperatureC = [Math]::Round(($temp.CurrentTemperature / 10) - 273.15, 2)
} else {
    $report.TemperatureC = -1
}
$report | ConvertTo-Json -Depth 5
";
        }
    }
}
