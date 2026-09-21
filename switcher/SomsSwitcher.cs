using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SomsLauncher;

[assembly: AssemblyTitle("SOMS! switcher")]
[assembly: AssemblyDescription("Safe private-server launcher for the official osu!lazer client")]
[assembly: AssemblyCompany("SOMS!")]
[assembly: AssemblyProduct("SOMS! switcher")]
[assembly: AssemblyVersion("1.1.3.0")]
[assembly: AssemblyFileVersion("1.1.3.0")]

namespace SomsSwitcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private const string ManifestUrl = "https://soms.angelfish-leaffish.ts.net/client/manifest.json";
        // The client module includes menu artwork and now exceeds 16 MiB.
        private const int MaxModuleBytes = 64 * 1024 * 1024;

        private readonly Color purple = Color.FromArgb(137, 63, 186);
        private readonly Color pink = Color.FromArgb(238, 92, 158);
        private readonly Color background = Color.FromArgb(27, 23, 31);
        private readonly Color panel = Color.FromArgb(53, 45, 59);
        private readonly Color muted = Color.FromArgb(191, 178, 199);

        private readonly Label osuValue;
        private readonly Label storageValue;
        private readonly Label versionValue;
        private readonly Label serverValue;
        private readonly Label statusValue;
        private readonly Button privateButton;
        private readonly Button refreshButton;
        private readonly Button browseButton;
        private readonly Button siteButton;

        private string osuPath;
        private string storagePath;
        private string osuVersion;
        private Dictionary<string, object> manifest;
        private bool busy;

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public MainForm()
        {
            Text = "SOMS! switcher";
            ClientSize = new Size(720, 458);
            MinimumSize = new Size(736, 497);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = background;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 92;
            header.BackColor = purple;
            Controls.Add(header);

            PictureBox logoImage = new PictureBox();
            using (Stream logoStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SomsSwitcher.Logo.png"))
            using (Image original = Image.FromStream(logoStream))
                logoImage.Image = new Bitmap(original);
            logoImage.Location = new Point(23, 13);
            logoImage.Size = new Size(66, 66);
            logoImage.SizeMode = PictureBoxSizeMode.Zoom;
            logoImage.Disposed += delegate { logoImage.Image.Dispose(); };
            header.Controls.Add(logoImage);
            using (Bitmap iconBitmap = new Bitmap(logoImage.Image, new Size(32, 32)))
            {
                IntPtr iconHandle = iconBitmap.GetHicon();
                try
                {
                    using (Icon sourceIcon = Icon.FromHandle(iconHandle))
                        Icon = (Icon)sourceIcon.Clone();
                }
                finally
                {
                    DestroyIcon(iconHandle);
                }
            }
            Disposed += delegate { Icon.Dispose(); };

            Label logo = new Label();
            logo.Text = "SOMS!";
            logo.Font = new Font("Segoe UI", 25F, FontStyle.Bold, GraphicsUnit.Point);
            logo.AutoSize = true;
            logo.Location = new Point(101, 13);
            header.Controls.Add(logo);

            Label subtitle = new Label();
            subtitle.Text = "свитчер для osu!lazer";
            subtitle.Font = new Font("Segoe UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(106, 60);
            header.Controls.Add(subtitle);

            Label safe = new Label();
            safe.Text = "Карты, скины и настройки остаются общими с обычным osu!";
            safe.ForeColor = Color.FromArgb(241, 227, 248);
            safe.Size = new Size(386, 48);
            safe.Location = new Point(306, 27);
            header.Controls.Add(safe);

            Panel info = new Panel();
            info.Location = new Point(22, 111);
            info.Size = new Size(676, 196);
            info.BackColor = panel;
            Controls.Add(info);

            int left = 22;
            int valueLeft = 142;
            AddInfoRow(info, "osu!lazer", left, valueLeft, 18, out osuValue);
            AddInfoRow(info, "Версия", left, valueLeft, 57, out versionValue);
            AddInfoRow(info, "Файлы игры", left, valueLeft, 96, out storageValue);
            AddInfoRow(info, "Сервер", left, valueLeft, 135, out serverValue);

            browseButton = MakeButton("Выбрать osu!.exe", Color.FromArgb(75, 64, 82), 536, 15, 124, 31);
            browseButton.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            browseButton.Click += delegate { BrowseForOsu(); };
            info.Controls.Add(browseButton);

            statusValue = new Label();
            statusValue.Location = new Point(24, 324);
            statusValue.Size = new Size(672, 48);
            statusValue.ForeColor = muted;
            statusValue.Text = "Проверяю установленный lazer и соединение с сервером…";
            Controls.Add(statusValue);

            privateButton = MakeButton("Играть на SOMS!", pink, 22, 382, 477, 54);
            privateButton.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            privateButton.Click += async delegate { await RunBusy(StartPrivate); };
            Controls.Add(privateButton);

            refreshButton = MakeButton("Обновить", Color.FromArgb(63, 55, 69), 511, 382, 90, 54);
            refreshButton.Click += async delegate { await RunBusy(RefreshState); };
            Controls.Add(refreshButton);

            siteButton = MakeButton("Сайт", Color.FromArgb(63, 55, 69), 610, 382, 88, 54);
            siteButton.Click += delegate { OpenWebsite(); };
            Controls.Add(siteButton);

            Shown += async delegate { await RunBusy(RefreshState); };
        }

        private void AddInfoRow(Panel parent, string name, int left, int valueLeft, int top, out Label value)
        {
            Label key = new Label();
            key.Text = name;
            key.ForeColor = muted;
            key.AutoSize = true;
            key.Location = new Point(left, top + 2);
            parent.Controls.Add(key);

            value = new Label();
            value.Text = "—";
            value.AutoEllipsis = true;
            value.Location = new Point(valueLeft, top);
            value.Size = new Size(382, 28);
            parent.Controls.Add(value);
        }

        private Button MakeButton(string text, Color color, int x, int y, int width, int height)
        {
            Button button = new Button();
            button.Text = text;
            button.Location = new Point(x, y);
            button.Size = new Size(width, height);
            button.BackColor = color;
            button.ForeColor = Color.White;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.Cursor = Cursors.Hand;
            return button;
        }

        private async Task RunBusy(Action action)
        {
            if (busy)
                return;

            SetBusy(true);
            try
            {
                await Task.Run(action);
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка: " + FriendlyMessage(ex), Color.FromArgb(255, 130, 150));
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            RunOnUi(delegate
            {
                privateButton.Enabled = !value;
                refreshButton.Enabled = !value;
                browseButton.Enabled = !value;
                siteButton.Enabled = !value;
                UseWaitCursor = value;
            });
        }

        private void RefreshState()
        {
            osuPath = FindOsuExecutable();
            osuVersion = File.Exists(osuPath) ? ReadOsuVersion(osuPath) : null;
            storagePath = ResolveStoragePath();

            RunOnUi(delegate
            {
                osuValue.Text = File.Exists(osuPath) ? osuPath : "не найден";
                osuValue.ForeColor = File.Exists(osuPath) ? Color.White : Color.FromArgb(255, 130, 150);
                versionValue.Text = string.IsNullOrEmpty(osuVersion) ? "—" : osuVersion;
                storageValue.Text = storagePath;
            });

            try
            {
                manifest = DownloadManifest();
                Dictionary<string, object> compatibility = Dict(manifest, "compatibility");
                bool supported = !string.IsNullOrEmpty(osuVersion) && compatibility.ContainsKey(osuVersion);
                string product = OptionalString(manifest, "product_name", "SOMS!");
                RunOnUi(delegate { serverValue.Text = product + " — доступен"; serverValue.ForeColor = Color.FromArgb(139, 235, 171); });

                if (!File.Exists(osuPath))
                    SetStatus("osu!lazer не найден. Нажми «Выбрать osu!.exe».", Color.FromArgb(255, 188, 112));
                else if (!supported)
                    SetStatus("Версия " + osuVersion + " пока не поддерживается. Подожди обновление модуля SOMS!.", Color.FromArgb(255, 188, 112));
                else
                    SetStatus("Готово. Приватный запуск использует твои существующие карты, скины и настройки.", Color.FromArgb(139, 235, 171));
            }
            catch
            {
                manifest = null;
                RunOnUi(delegate { serverValue.Text = "SOMS! — недоступен"; serverValue.ForeColor = Color.FromArgb(255, 130, 150); });
                if (File.Exists(osuPath))
                    SetStatus("Сервер сейчас не отвечает. Попробуй нажать «Обновить» чуть позже.", Color.FromArgb(255, 188, 112));
                else
                    SetStatus("Не найден osu!lazer, а сервер сейчас не отвечает.", Color.FromArgb(255, 130, 150));
            }
        }

        private void StartPrivate()
        {
            AssertNoLazerRunning();
            osuPath = FindOsuExecutable();
            if (!File.Exists(osuPath))
                throw new InvalidOperationException("osu!lazer не найден. Выбери osu!.exe вручную.");

            VerifyOfficialExecutable(osuPath);
            osuVersion = ReadOsuVersion(osuPath);
            manifest = DownloadManifest();
            Dictionary<string, object> compatibility = Dict(manifest, "compatibility");
            if (!compatibility.ContainsKey(osuVersion))
                throw new InvalidOperationException("Версия lazer " + osuVersion + " пока не поддерживается. Подожди обновление модуля на сервере.");

            Dictionary<string, object> versionEntry = AsDict(compatibility[osuVersion], "compatibility." + osuVersion);
            string enhancedAuthPath = DownloadModule(Dict(versionEntry, "enhanced_auth"));
            string startupHookPath = DownloadModule(Dict(versionEntry, "startup_hook"));

            string healthUrl = RequiredString(manifest, "health_url");
            RequireTrustedUrl(new Uri(ManifestUrl), new Uri(healthUrl, UriKind.Absolute));
            using (WebClient healthClient = NewWebClient())
                healthClient.DownloadString(healthUrl);

            Dictionary<string, object> endpoints = Dict(manifest, "endpoints");
            Dictionary<string, object> client = Dict(manifest, "client");
            string api = TrustedEndpoint(endpoints, "api");
            string website = TrustedEndpoint(endpoints, "website");
            string spectator = TrustedEndpoint(endpoints, "spectator");
            string multiplayer = TrustedEndpoint(endpoints, "multiplayer");
            string metadata = TrustedEndpoint(endpoints, "metadata");
            string bss = TrustedEndpoint(endpoints, "beatmap_submission");
            string clientId = RequiredString(client, "id");
            string clientSecret = RequiredString(client, "secret");
            string credentialTarget = RequiredString(manifest, "credential_target");

            if (!Regex.IsMatch(clientId, "^[1-9][0-9]*$") || !Regex.IsMatch(clientSecret, "^[A-Za-z0-9._~-]{16,200}$"))
                throw new InvalidDataException("Сервер прислал неправильные OAuth-настройки.");
            if (!Regex.IsMatch(credentialTarget, "^[A-Za-z0-9._/-]{3,200}$"))
                throw new InvalidDataException("Сервер прислал неправильное имя хранилища входа.");

            List<string> arguments = new List<string>();
            arguments.Add("--api-url=" + api);
            arguments.Add("--website-url=" + website);
            arguments.Add("--client-id=" + clientId);
            arguments.Add("--client-secret=" + clientSecret);
            arguments.Add("--spectator-url=" + spectator);
            arguments.Add("--multiplayer-url=" + multiplayer);
            arguments.Add("--metadata-url=" + metadata);
            arguments.Add("--bss-url=" + bss);
            arguments.Add("--disable-sentry-logger");

            ProcessStartInfo start = NewOsuStartInfo(osuPath);
            start.Arguments = string.Join(" ", arguments.ToArray());
            start.EnvironmentVariables["DOTNET_STARTUP_HOOKS"] = startupHookPath;
            start.EnvironmentVariables["PRIVATE_OSU_ENHANCED_AUTH_PATH"] = enhancedAuthPath;
            start.EnvironmentVariables["PRIVATE_OSU_CREDENTIAL_TARGET"] = credentialTarget;
            Process.Start(start);
            SetStatus("osu! запущен на SOMS!. Вводи данные аккаунта SOMS!.", Color.FromArgb(139, 235, 171));
        }

        private ProcessStartInfo NewOsuStartInfo(string path)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = path;
            start.WorkingDirectory = Path.GetDirectoryName(path);
            start.UseShellExecute = false;
            return start;
        }

        private void AssertNoLazerRunning()
        {
            if (LazerProcessGuard.FindRunning().Length > 0)
                throw new InvalidOperationException("osu!lazer уже запущен. Сначала закрой lazer. osu!stable можно оставить открытым.");
        }

        private Dictionary<string, object> DownloadManifest()
        {
            Uri manifestUri = new Uri(ManifestUrl, UriKind.Absolute);
            RequireTrustedUrl(manifestUri, manifestUri);
            string json;
            using (WebClient client = NewWebClient())
                json = client.DownloadString(manifestUri);
            if (Encoding.UTF8.GetByteCount(json) > 1024 * 1024)
                throw new InvalidDataException("Манифест сервера слишком большой.");

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 1024 * 1024;
            Dictionary<string, object> result = serializer.Deserialize<Dictionary<string, object>>(json);
            if (result == null || Convert.ToInt32(result["schema_version"]) != 1)
                throw new InvalidDataException("Версия манифеста сервера не поддерживается.");
            return result;
        }

        private string DownloadModule(Dictionary<string, object> module)
        {
            return DownloadModule(module, Path.Combine(SettingsDirectory(), "modules"));
        }

        private string DownloadModule(Dictionary<string, object> module, string root)
        {
            string relativeUrl = RequiredString(module, "url");
            string expectedHash = RequiredString(module, "sha256").ToLowerInvariant();
            string filename = RequiredString(module, "filename");
            int expectedSize = Convert.ToInt32(module["size"]);
            if (!Regex.IsMatch(expectedHash, "^[a-f0-9]{64}$"))
                throw new InvalidDataException("Сервер прислал неправильный SHA-256 модуля.");
            if (Path.GetFileName(filename) != filename || !filename.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Сервер прислал неправильное имя модуля.");
            if (expectedSize <= 0)
                throw new InvalidDataException("Неправильный размер модуля.");
            if (expectedSize > MaxModuleBytes)
                throw new InvalidDataException("Модуль превышает лимит свитчера (64 МиБ). Скачай новую версию SOMS-switcher.exe с сайта.");

            Directory.CreateDirectory(root);
            string destination = Path.Combine(root, expectedHash.Substring(0, 16) + "-" + filename);
            if (File.Exists(destination) && new FileInfo(destination).Length == expectedSize && HashFile(destination) == expectedHash)
                return destination;

            Uri manifestUri = new Uri(ManifestUrl, UriKind.Absolute);
            Uri moduleUri = new Uri(manifestUri, relativeUrl);
            RequireTrustedUrl(manifestUri, moduleUri);
            byte[] bytes;
            using (WebClient client = NewWebClient())
                bytes = client.DownloadData(moduleUri);
            if (bytes.Length != expectedSize || bytes.Length > MaxModuleBytes)
                throw new InvalidDataException("Размер скачанного модуля не совпал с манифестом.");
            string actualHash = HashBytes(bytes);
            if (actualHash != expectedHash)
                throw new InvalidDataException("SHA-256 скачанного модуля не совпал. Запуск отменён.");

            string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(destination))
                    File.Replace(temporary, destination, null);
                else
                    File.Move(temporary, destination);
            }
            catch (IOException ex)
            {
                throw new IOException("Не удалось обновить модуль osu!. Закрой игру и повтори запуск свитчера. Запуск отменён.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException("Нет доступа для обновления модуля osu!. Запуск отменён.", ex);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            if (HashFile(destination) != expectedHash)
                throw new InvalidDataException("SHA-256 сохранённого модуля не совпал. Запуск отменён.");
            return destination;
        }

        private WebClient NewWebClient()
        {
            WebClient client = new WebClient();
            client.Encoding = Encoding.UTF8;
            client.Headers[HttpRequestHeader.UserAgent] = "SOMS-switcher/1.1.3 Windows";
            return client;
        }

        private string TrustedEndpoint(Dictionary<string, object> endpoints, string name)
        {
            string value = RequiredString(endpoints, name).TrimEnd('/');
            RequireTrustedUrl(new Uri(ManifestUrl), new Uri(value, UriKind.Absolute));
            return value;
        }

        private static void RequireTrustedUrl(Uri manifestUri, Uri target)
        {
            if (!string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(target.Host, manifestUri.Host, StringComparison.OrdinalIgnoreCase) ||
                !target.IsDefaultPort)
                throw new InvalidDataException("Сервер попытался перенаправить свитчер на недоверенный адрес.");
        }

        private string FindOsuExecutable()
        {
            string saved = ReadSavedOsuPath();
            if (!string.IsNullOrEmpty(saved) && File.Exists(saved))
                return saved;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osulazer", "current", "osu!.exe");
        }

        private string ResolveStoragePath()
        {
            string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");
            string storageIni = Path.Combine(defaultPath, "storage.ini");
            if (!File.Exists(storageIni))
                return defaultPath;

            foreach (string line in File.ReadAllLines(storageIni, Encoding.UTF8))
            {
                Match match = Regex.Match(line, "^\\s*FullPath\\s*=\\s*(.*?)\\s*$", RegexOptions.IgnoreCase);
                if (!match.Success)
                    continue;
                string value = match.Groups[1].Value.Trim().Trim('"', '\'');
                if (value.Length > 0)
                    return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
            }
            return defaultPath;
        }

        private string ReadOsuVersion(string path)
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            string[] values = new string[] { info.ProductVersion, info.FileVersion };
            foreach (string value in values)
            {
                if (string.IsNullOrEmpty(value))
                    continue;
                Match match = Regex.Match(value, "(?<![0-9])([0-9]{4}\\.[0-9]+\\.[0-9]+)(?![0-9])");
                if (match.Success)
                    return match.Groups[1].Value;
            }
            throw new InvalidDataException("Не получилось определить версию osu!lazer.");
        }

        private void VerifyOfficialExecutable(string path)
        {
            if (!LazerProcessGuard.IsLazerExecutable(path))
                throw new InvalidDataException("Выбери osu!.exe из папки osu!lazer, а не osu!stable.");
            try
            {
                X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
                X509Certificate2 certificate2 = new X509Certificate2(certificate);
                if (certificate2.Subject.IndexOf("ppy Pty Ltd", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidDataException("Выбранный osu!.exe подписан не ppy Pty Ltd.");
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Не удалось проверить цифровую подпись официального osu!.exe.", ex);
            }
        }

        private void BrowseForOsu()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Выбери osu!.exe из папки osu!lazer";
                dialog.Filter = "osu!lazer (osu!.exe)|osu!.exe|Программы (*.exe)|*.exe";
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    VerifyOfficialExecutable(dialog.FileName);
                    SaveOsuPath(dialog.FileName);
                    osuPath = dialog.FileName;
                    BeginRefresh();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, FriendlyMessage(ex), "SOMS! switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private string SettingsDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SOMS-switcher");
        }

        private async void BeginRefresh()
        {
            await RunBusy(RefreshState);
        }

        private string ReadSavedOsuPath()
        {
            string path = Path.Combine(SettingsDirectory(), "osu-path.txt");
            // Read the old preference without moving or deleting an existing installation.
            if (!File.Exists(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pulse-switcher", "osu-path.txt");
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : null;
        }

        private void SaveOsuPath(string value)
        {
            Directory.CreateDirectory(SettingsDirectory());
            File.WriteAllText(Path.Combine(SettingsDirectory(), "osu-path.txt"), value, new UTF8Encoding(false));
        }

        private void OpenWebsite()
        {
            try
            {
                string url = manifest == null ? "https://soms.angelfish-leaffish.ts.net/site/" : RequiredString(manifest, "website_url");
                RequireTrustedUrl(new Uri(ManifestUrl), new Uri(url));
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, FriendlyMessage(ex), "SOMS! switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SetStatus(string text, Color color)
        {
            RunOnUi(delegate { statusValue.Text = text; statusValue.ForeColor = color; });
        }

        private void RunOnUi(Action action)
        {
            if (IsDisposed)
                return;
            if (InvokeRequired)
                Invoke(action);
            else
                action();
        }

        private static Dictionary<string, object> Dict(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key))
                throw new InvalidDataException("В манифесте нет раздела " + key + ".");
            return AsDict(source[key], key);
        }

        private static Dictionary<string, object> AsDict(object value, string name)
        {
            Dictionary<string, object> dictionary = value as Dictionary<string, object>;
            if (dictionary == null)
                throw new InvalidDataException("Раздел " + name + " имеет неправильный формат.");
            return dictionary;
        }

        private static string RequiredString(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key) || source[key] == null)
                throw new InvalidDataException("В манифесте нет поля " + key + ".");
            string value = Convert.ToString(source[key]);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("Поле " + key + " в манифесте пустое.");
            return value;
        }

        private static string OptionalString(Dictionary<string, object> source, string key, string fallback)
        {
            return source != null && source.ContainsKey(key) && source[key] != null ? Convert.ToString(source[key]) : fallback;
        }

        private static string HashFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return Hex(sha.ComputeHash(stream));
        }

        private static string HashBytes(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return Hex(sha.ComputeHash(bytes));
        }

        private static string Hex(byte[] bytes)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
                result.Append(value.ToString("x2"));
            return result.ToString();
        }

        private static string FriendlyMessage(Exception ex)
        {
            WebException web = ex as WebException;
            if (web != null)
                return "Не удалось связаться с сервером. Проверь Tailscale и интернет.";
            return ex.Message;
        }
    }
}
