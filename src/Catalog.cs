using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TinySticky {
    internal static class Catalog {
        const string SharedPath = @"C:\srv\services.json";

        internal static CatalogLoad Load() {
            string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "services.json");
            if (File.Exists(SharedPath)) return ReadFile(SharedPath);
            if (File.Exists(beside)) return ReadFile(beside);
            return Parse(EmbeddedJson.Text, null, null);
        }

        internal static string EmbeddedText() {
            return EmbeddedJson.Text;
        }

        static CatalogLoad ReadFile(string path) {
            try {
                string json = File.ReadAllText(path);
                CatalogLoad load = Parse(json, path, null);
                load.Path = path;
                return load;
            } catch (Exception ex) {
                CatalogLoad fallback = Parse(EmbeddedJson.Text, null, null);
                fallback.Error = "清单无法读取，已改用内置清单：" + ex.Message;
                return fallback;
            }
        }

        internal static CatalogLoad Parse(string json, string path, string error) {
            var load = new CatalogLoad();
            load.Path = path;
            load.Error = error;
            if (string.IsNullOrWhiteSpace(json)) {
                load.Error = "清单是空的";
                return load;
            }
            if (json[0] == '\uFEFF') json = json.Substring(1);
            try {
                var serializer = new JavaScriptSerializer();
                List<ServiceDefinition> raw = serializer.Deserialize<List<ServiceDefinition>>(json);
                if (raw == null) {
                    load.Error = "清单格式不对，需要一个服务数组";
                    return load;
                }
                foreach (ServiceDefinition svc in raw) {
                    ServiceDefinition clean = Normalize(svc);
                    if (clean != null) load.Services.Add(clean);
                }
                if (load.Services.Count == 0)
                    load.Error = "清单里没有可用的服务";
            } catch (Exception ex) {
                load.Services.Clear();
                load.Error = "清单无法解析：" + ex.Message;
            }
            return load;
        }

        internal static void Upsert(string path, string name, IList<int> ports) {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("没有可写的清单");
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new InvalidOperationException("名字是空的");
            if (ports == null || ports.Count == 0) throw new InvalidOperationException("没有端口");
            List<ServiceDefinition> services;
            if (File.Exists(path)) {
                CatalogLoad load = Parse(File.ReadAllText(path), path, null);
                if (!string.IsNullOrEmpty(load.Error)) throw new InvalidOperationException(load.Error);
                services = load.Services;
            } else {
                services = Parse(EmbeddedJson.Text, null, null).Services;
            }
            var wanted = new HashSet<int>();
            foreach (int port in ports) {
                if (port >= 1 && port <= 65535) wanted.Add(port);
            }
            if (wanted.Count == 0) throw new InvalidOperationException("没有端口");
            ServiceDefinition found = null;
            foreach (ServiceDefinition svc in services) {
                if (SamePorts(svc, wanted)) {
                    found = svc;
                    break;
                }
            }
            if (found != null) {
                found.name = name;
            } else {
                var created = new ServiceDefinition();
                created.name = name;
                created.ports = new List<PortDefinition>();
                foreach (int port in wanted) created.ports.Add(new PortDefinition { port = port, label = "" });
                services.Add(created);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string text = Format(services);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(true));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        static bool SamePorts(ServiceDefinition svc, HashSet<int> ports) {
            if (svc.ports == null || svc.ports.Count != ports.Count) return false;
            foreach (PortDefinition port in svc.ports) {
                if (!ports.Contains(port.port)) return false;
            }
            return true;
        }

        static string Format(List<ServiceDefinition> services) {
            var sb = new StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < services.Count; i++) {
                ServiceDefinition svc = services[i];
                sb.Append("  {\n");
                sb.Append("    \"name\": \"").Append(Esc(svc.name)).Append("\"");
                if (!string.IsNullOrWhiteSpace(svc.health))
                    sb.Append(",\n    \"health\": \"").Append(Esc(svc.health.Trim())).Append("\"");
                sb.Append(",\n    \"ports\": [\n");
                for (int p = 0; p < svc.ports.Count; p++) {
                    PortDefinition port = svc.ports[p];
                    sb.Append("      { \"port\": ").Append(port.port);
                    if (!string.IsNullOrWhiteSpace(port.label))
                        sb.Append(", \"label\": \"").Append(Esc(port.label.Trim())).Append("\"");
                    sb.Append(" }");
                    if (p + 1 < svc.ports.Count) sb.Append(",");
                    sb.Append("\n");
                }
                sb.Append("    ]\n  }");
                if (i + 1 < services.Count) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("]\n");
            return sb.ToString();
        }

        static string Esc(string value) {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "");
        }

        static ServiceDefinition Normalize(ServiceDefinition svc) {
            if (svc == null || string.IsNullOrWhiteSpace(svc.name) || svc.ports == null) return null;
            var ports = new List<PortDefinition>();
            var seen = new HashSet<int>();
            foreach (PortDefinition port in svc.ports) {
                if (port == null || port.port < 1 || port.port > 65535) continue;
                if (!seen.Add(port.port)) continue;
                ports.Add(new PortDefinition {
                    port = port.port,
                    label = string.IsNullOrWhiteSpace(port.label) ? "" : port.label.Trim()
                });
            }
            if (ports.Count == 0) return null;
            return new ServiceDefinition {
                name = svc.name.Trim(),
                health = string.IsNullOrWhiteSpace(svc.health) ? null : svc.health.Trim(),
                ports = ports
            };
        }
    }
}
