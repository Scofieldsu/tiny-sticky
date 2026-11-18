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
