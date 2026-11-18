using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TinySticky {
    internal static class BoardBuilder {
        internal static Board Build() {
            CatalogLoad catalog = Catalog.Load();
            string scanError = null;
            List<Listener> listeners = null;
            try {
                listeners = PortScanner.Scan();
            } catch (Exception ex) {
                scanError = ex.Message;
            }

            Board board = Classifier.Classify(catalog.Services, listeners ?? new List<Listener>());
            board.CatalogPath = catalog.Path;
            board.CatalogError = catalog.Error;
            board.ScannedAt = DateTime.Now;
            if (scanError != null) {
                board.ScanFailed = true;
                board.CatalogError = "端口扫描失败：" + scanError;
                board.Unknown.Clear();
                foreach (ServiceView service in board.Services)
                    service.State = RunState.Unknown;
                return board;
            }

            var pending = new List<Task>();
            foreach (ServiceView service in board.Services) {
                if (service.State != RunState.Up || string.IsNullOrWhiteSpace(service.HealthUrl)) continue;
                ServiceView target = service;
                pending.Add(Task.Run(delegate {
                    if (!Health.Probe(target.HealthUrl, 1000))
                        target.State = RunState.Unhealthy;
                }));
            }
            if (pending.Count > 0) Task.WaitAll(pending.ToArray());
            return board;
        }
    }
}
