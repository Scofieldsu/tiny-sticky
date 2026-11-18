using System;
using System.Collections.Generic;

namespace TinySticky {
    internal enum RunState {
        Up,
        Down,
        Partial,
        Unhealthy,
        Unknown
    }

    internal sealed class PortDefinition {
        public int port { get; set; }
        public string label { get; set; }
    }

    internal sealed class ServiceDefinition {
        public string name { get; set; }
        public string health { get; set; }
        public List<PortDefinition> ports { get; set; }
    }

    internal sealed class Listener {
        public int Port;
        public int Pid;
        public string ProcessName;
        public bool Loopback;
    }

    internal sealed class PortView {
        public int Port;
        public string Label;
        public bool Listening;
        public bool LoopbackOnly;
    }

    internal sealed class ServiceView {
        public string Name;
        public string HealthUrl;
        public RunState State;
        public bool LocalOnly;
        public readonly List<PortView> Ports = new List<PortView>();
    }

    internal sealed class UnknownView {
        public int Port;
        public int Pid;
        public string ProcessName;
        public bool LoopbackOnly;
    }

    internal sealed class Board {
        public readonly List<ServiceView> Services = new List<ServiceView>();
        public readonly List<UnknownView> Unknown = new List<UnknownView>();
        public string CatalogPath;
        public string CatalogError;
        public bool ScanFailed;
        public DateTime ScannedAt;
    }

    internal sealed class CatalogLoad {
        public List<ServiceDefinition> Services = new List<ServiceDefinition>();
        public string Path;
        public string Error;
    }

    internal sealed class WindowSettings {
        public int x { get; set; }
        public int y { get; set; }
        public bool hasPosition { get; set; }
        public double opacity { get; set; }
        public bool topMost { get; set; }
        public bool clickThrough { get; set; }
        public bool collapsed { get; set; }

        public static WindowSettings CreateDefault() {
            return new WindowSettings {
                opacity = 0.88,
                topMost = true,
                collapsed = false
            };
        }
    }
}
