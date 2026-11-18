using System;
using System.Collections.Generic;
using System.Linq;

namespace TinySticky {
    internal static class Classifier {
        static readonly HashSet<string> SystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "System", "Idle", "svchost", "lsass", "wininit", "services", "spoolsv",
            "csrss", "smss", "fontdrvhost", "dwm", "sihost", "ctfmon", "RuntimeBroker",
            "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
            "WidgetService", "SecurityHealthService", "SecurityHealthSystray", "MsMpEng",
            "NisSrv", "WmiPrvSE", "dllhost", "conhost", "taskhostw", "winlogon", "LsaIso",
            "Registry", "audiodg", "SearchIndexer", "SearchProtocolHost", "DasHost",
            "smartscreen", "CompPkgSrv", "AggregatorHost", "ChsIME", "ApplicationFrameHost",
            "LockApp", "PhoneExperienceHost", "SystemSettings", "UserOOBEBroker",
            "MoUsoCoreWorker", "TiWorker", "TrustedInstaller", "msdtc", "sqlwriter",
            "backgroundTaskHost", "WidgetBoard", "ShellHost", "Explorer"
        };

        internal static bool IsSystemProcess(string name) {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return SystemProcesses.Contains(name.Trim());
        }

        // Protected system processes often refuse to give out their name.
        // A named program (python, minio, node) still shows up.
        static bool IsUnnamed(string name) {
            return string.IsNullOrWhiteSpace(name) || name.TrimStart().StartsWith("pid ");
        }

        internal static Board Classify(IList<ServiceDefinition> services, IList<Listener> listeners) {
            var byPort = new Dictionary<int, List<Listener>>();
            if (listeners != null) {
                foreach (Listener listener in listeners) {
                    if (listener == null || listener.Port < 1 || listener.Port > 65535) continue;
                    List<Listener> group;
                    if (!byPort.TryGetValue(listener.Port, out group)) {
                        group = new List<Listener>();
                        byPort[listener.Port] = group;
                    }
                    group.Add(listener);
                }
            }

            var board = new Board();
            var claimed = new HashSet<int>();
            if (services != null) {
                foreach (ServiceDefinition svc in services) {
                    if (svc == null || svc.ports == null) continue;
                    var view = new ServiceView {
                        Name = svc.name,
                        HealthUrl = svc.health
                    };
                    int listening = 0;
                    foreach (PortDefinition port in svc.ports) {
                        claimed.Add(port.port);
                        List<Listener> group;
                        bool up = byPort.TryGetValue(port.port, out group) && group.Count > 0;
                        if (up) listening++;
                        view.Ports.Add(new PortView {
                            Port = port.port,
                            Label = port.label ?? "",
                            Listening = up,
                            LoopbackOnly = up && group.All(item => item.Loopback)
                        });
                    }
                    if (listening == 0) view.State = RunState.Down;
                    else if (listening == view.Ports.Count) view.State = RunState.Up;
                    else view.State = RunState.Partial;
                    view.LocalOnly = view.Ports.Any(port => port.Listening)
                        && view.Ports.Where(port => port.Listening).All(port => port.LoopbackOnly);
                    board.Services.Add(view);
                }
            }

            foreach (int port in byPort.Keys.OrderBy(value => value)) {
                if (claimed.Contains(port)) continue;
                List<Listener> group = byPort[port];
                List<Listener> users = group.Where(item => !IsSystemProcess(item.ProcessName) && !IsUnnamed(item.ProcessName)).ToList();
                if (users.Count == 0) continue;
                Listener pick = users[0];
                board.Unknown.Add(new UnknownView {
                    Port = port,
                    Pid = pick.Pid,
                    ProcessName = pick.ProcessName,
                    LoopbackOnly = group.All(item => item.Loopback)
                });
            }
            return board;
        }
    }
}
