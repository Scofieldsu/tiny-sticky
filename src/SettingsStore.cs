using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TinySticky {
    internal static class SettingsStore {
        internal static string Dir {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TinySticky"); }
        }

        static string PathName {
            get { return Path.Combine(Dir, "window.json"); }
        }

        internal static WindowSettings Load() {
            try {
                if (!File.Exists(PathName)) return WindowSettings.CreateDefault();
                var serializer = new JavaScriptSerializer();
                WindowSettings settings = serializer.Deserialize<WindowSettings>(File.ReadAllText(PathName));
                if (settings == null) return WindowSettings.CreateDefault();
                if (settings.opacity < 0.55 || settings.opacity > 1) settings.opacity = 0.88;
                return settings;
            } catch {
                return WindowSettings.CreateDefault();
            }
        }

        internal static void Save(WindowSettings settings) {
            try {
                Directory.CreateDirectory(Dir);
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(PathName, serializer.Serialize(settings), new UTF8Encoding(false));
            } catch (Exception ex) {
                Program.Log(ex);
            }
        }
    }

    internal static class StartupShortcut {
        static string LinkPath {
            get {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    "TinySticky.lnk");
            }
        }

        internal static bool Exists() {
            return File.Exists(LinkPath);
        }

        internal static void Enable(bool on) {
            if (!on) {
                if (File.Exists(LinkPath)) File.Delete(LinkPath);
                return;
            }
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("无法创建快捷方式");
            object shell = Activator.CreateInstance(shellType);
            object link = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { LinkPath });
            Type linkType = link.GetType();
            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath });
            linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { AppDomain.CurrentDomain.BaseDirectory });
            linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Tiny 服务便利贴" });
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
    }
}
