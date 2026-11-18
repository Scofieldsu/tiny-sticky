using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TinySticky {
    internal static class Program {
        [STAThread]
        static int Main(string[] args) {
            string command = args != null && args.Length > 0 ? args[0] : "";
            try {
                if (command == "--self-test") return SelfTest(args.Length > 1 ? args[1] : null);
                if (command == "--scan") return Scan(args.Length > 1 ? args[1] : null);
                if (command == "--snapshot") return Snapshot(args.Length > 1 ? args[1] : "preview.png");
                return Run();
            } catch (Exception ex) {
                Log(ex);
                EnsureConsole();
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        static int Run() {
            bool created;
            using (var mutex = new Mutex(true, @"Local\TinySticky", out created)) {
                if (!created) {
                    ActivateOther();
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { Log(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
                    Log(e.ExceptionObject as Exception);
                };
                Application.Run(new StickyForm(SettingsStore.Load(), true));
                return 0;
            }
        }

        static int Scan(string reportPath) {
            EnsureConsole();
            Board board = BoardBuilder.Build();
            var buffer = new StringWriter();
            TextWriter previous = Console.Out;
            Console.SetOut(buffer);
            PrintBoard(board);
            Console.SetOut(previous);
            string report = buffer.ToString();
            Console.WriteLine(report);
            if (!string.IsNullOrEmpty(reportPath))
                File.WriteAllText(reportPath, report, new UTF8Encoding(false));
            return board.ScanFailed ? 1 : 0;
        }

        static int Snapshot(string path) {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Board board = BoardBuilder.Build();
            using (var form = new StickyForm(WindowSettings.CreateDefault(), false)) {
                IntPtr handle = form.Handle;
                if (handle == IntPtr.Zero) return 1;
                form.SetBoard(board);
                using (var bitmap = new Bitmap(Math.Max(1, form.ClientSize.Width), Math.Max(1, form.ClientSize.Height))) {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                        form.Render(graphics);
                    string full = Path.GetFullPath(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    bitmap.Save(full, ImageFormat.Png);
                }
            }
            EnsureConsole();
            Console.WriteLine(Path.GetFullPath(path));
            return 0;
        }

        static int SelfTest(string reportPath) {
            EnsureConsole();
            var buffer = new StringWriter();
            TextWriter previous = Console.Out;
            Console.SetOut(buffer);
            int failed = 0;
            Action<bool, string> check = delegate(bool ok, string name) {
                Console.WriteLine((ok ? "OK  " : "FAIL ") + name);
                if (!ok) failed++;
            };

            CatalogLoad embedded = Catalog.Parse(Catalog.EmbeddedText(), null, null);
            check(embedded.Services.Count == 2, "embedded catalog has two services");
            check(embedded.Services[0].name == "MinIO", "first service is MinIO");
            check(embedded.Services[0].ports[1].label == "控制台", "chinese label survives");
            check(embedded.Services[1].ports.Count == 4, "rpa exposes four ports");

            CatalogLoad trimmed = Catalog.Parse(
                "[{\"name\":\"Demo\",\"ports\":[{\"port\":0,\"label\":\"bad\"},{\"port\":18021,\"label\":\"ok\"}]}]",
                "mem",
                null);
            check(trimmed.Services.Count == 1 && trimmed.Services[0].ports.Count == 1, "invalid port is dropped");
            check(trimmed.Services[0].ports[0].label == "ok", "valid port label is kept");
            check(Catalog.Parse("{}", null, null).Error != null, "object catalog is rejected");

            var partialListeners = new List<Listener> {
                new Listener { Port = 9000, Pid = 10, ProcessName = "minio", Loopback = false },
                new Listener { Port = 135, Pid = 800, ProcessName = "svchost", Loopback = false },
                new Listener { Port = 18021, Pid = 99, ProcessName = "python", Loopback = true }
            };
            Board partial = Classifier.Classify(embedded.Services, partialListeners);
            check(partial.Services[0].State == RunState.Partial, "minio is partial when console is down");
            check(partial.Services[1].State == RunState.Down, "rpa is down when nothing listens");
            check(partial.Unknown.Count == 1 && partial.Unknown[0].Port == 18021, "python is unregistered");
            check(partial.Unknown[0].LoopbackOnly, "loopback-only flag");
            var hidden = new List<Listener> {
                new Listener { Port = 49664, Pid = 1608, ProcessName = "pid 1608", Loopback = false },
                new Listener { Port = 18022, Pid = 50, ProcessName = "node", Loopback = false }
            };
            Board unnamed = Classifier.Classify(new List<ServiceDefinition>(), hidden);
            check(unnamed.Unknown.Count == 1 && unnamed.Unknown[0].Port == 18022, "unnamed system pid is hidden");

            var ownedBySystem = new List<Listener> {
                new Listener { Port = 9000, Pid = 4, ProcessName = "svchost", Loopback = true },
                new Listener { Port = 9001, Pid = 4, ProcessName = "svchost", Loopback = true }
            };
            var minioOnly = new List<ServiceDefinition> { embedded.Services[0] };
            Board systemOwned = Classifier.Classify(minioOnly, ownedBySystem);
            check(systemOwned.Services[0].State == RunState.Up, "catalog port counts even if the process is svchost");
            check(systemOwned.Unknown.Count == 0, "claimed system port is not listed as unregistered");
            check(systemOwned.Services[0].LocalOnly, "both listeners are loopback-only");

            var loopbackAndAny = new List<Listener> {
                new Listener { Port = 5000, Pid = 3, ProcessName = "python", Loopback = true },
                new Listener { Port = 5000, Pid = 3, ProcessName = "python", Loopback = false }
            };
            Board mixed = Classifier.Classify(new List<ServiceDefinition> { embedded.Services[1] }, loopbackAndAny);
            check(mixed.Services[0].State == RunState.Partial, "one of four rpa ports is up");
            check(!mixed.Services[0].Ports[0].LoopbackOnly, "a public socket clears loopback-only");

            check(!Health.Probe("http://127.0.0.1:1/", 800), "closed port is unhealthy");

            List<Listener> live = PortScanner.Scan();
            bool sane = live.Count > 0;
            foreach (Listener listener in live) {
                if (listener.Port < 1 || listener.Port > 65535 || string.IsNullOrEmpty(listener.ProcessName))
                    sane = false;
            }
            check(sane, "live scan returned " + live.Count + " sockets");
            Console.SetOut(previous);
            string report = buffer.ToString();
            Console.WriteLine(report);
            if (!string.IsNullOrEmpty(reportPath))
                File.WriteAllText(reportPath, report, new UTF8Encoding(false));
            return failed == 0 ? 0 : 1;
        }

        internal static void PrintBoard(Board board) {
            Console.WriteLine(board.ScanFailed ? "scan failed" : "scan ok");
            if (!string.IsNullOrEmpty(board.CatalogError)) Console.WriteLine("catalog: " + board.CatalogError);
            Console.WriteLine("catalog file: " + (board.CatalogPath ?? "(embedded)"));
            foreach (ServiceView service in board.Services) {
                Console.WriteLine(service.Name + "  " + service.State + (service.LocalOnly ? "  local" : ""));
                foreach (PortView port in service.Ports)
                    Console.WriteLine("  " + port.Port + " " + port.Label + "  " + (port.Listening ? "listen" : "down"));
            }
            Console.WriteLine("unknown " + board.Unknown.Count);
            foreach (UnknownView item in board.Unknown)
                Console.WriteLine("  " + item.Port + "  " + item.ProcessName + "  pid " + item.Pid + (item.LoopbackOnly ? "  local" : ""));
        }

        internal static void Log(Exception ex) {
            if (ex == null) return;
            try {
                Directory.CreateDirectory(SettingsStore.Dir);
                File.AppendAllText(
                    Path.Combine(SettingsStore.Dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + ex + Environment.NewLine);
            } catch {
            }
        }

        static void EnsureConsole() {
            if (!AttachConsole(-1)) AllocConsole();
            try {
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetOut(stdout);
                Console.SetError(stderr);
                Console.OutputEncoding = Encoding.UTF8;
            } catch {
            }
        }

        static void ActivateOther() {
            int me = Process.GetCurrentProcess().Id;
            foreach (Process process in Process.GetProcessesByName("TinySticky")) {
                if (process.Id == me || process.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(process.MainWindowHandle, 9);
                SetForegroundWindow(process.MainWindowHandle);
                return;
            }
        }

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        static extern bool AllocConsole();

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int command);
    }
}
