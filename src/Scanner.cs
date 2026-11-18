using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace TinySticky {
    internal static class PortScanner {
        const int AfInet = 2;
        const int AfInet6 = 23;
        const int TcpTableOwnerPidListener = 3;
        const int ErrorInsufficientBuffer = 122;

        internal static List<Listener> Scan() {
            Dictionary<int, string> names = SnapshotNames();
            var all = new List<Listener>();
            Exception last = null;
            int ok = 0;
            foreach (int family in new[] { AfInet, AfInet6 }) {
                try {
                    all.AddRange(ReadFamily(family, names));
                    ok++;
                } catch (Exception ex) {
                    last = ex;
                }
            }
            if (ok == 0) throw last ?? new InvalidOperationException("无法读取 TCP 监听表");
            return all;
        }

        static List<Listener> ReadFamily(int family, Dictionary<int, string> names) {
            int size = 0;
            uint err = GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TcpTableOwnerPidListener, 0);
            if (err == 0 || size <= 0) return new List<Listener>();
            for (int attempt = 0; attempt < 5; attempt++) {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try {
                    err = GetExtendedTcpTable(buffer, ref size, true, family, TcpTableOwnerPidListener, 0);
                    if (err == ErrorInsufficientBuffer) continue;
                    if (err != 0) throw new Win32Exception((int)err);
                    return family == AfInet ? ParseV4(buffer, names) : ParseV6(buffer, names);
                } finally {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            throw new Win32Exception(ErrorInsufficientBuffer);
        }

        static List<Listener> ParseV4(IntPtr buffer, Dictionary<int, string> names) {
            int count = Marshal.ReadInt32(buffer);
            int stride = Marshal.SizeOf(typeof(TcpRowV4));
            var list = new List<Listener>(count);
            for (int i = 0; i < count; i++) {
                IntPtr rowPtr = IntPtr.Add(buffer, 4 + i * stride);
                var row = (TcpRowV4)Marshal.PtrToStructure(rowPtr, typeof(TcpRowV4));
                int port = HostPort(row.LocalPort);
                if (port <= 0) continue;
                var ip = new IPAddress(BitConverter.GetBytes(row.LocalAddr));
                list.Add(Make(port, (int)row.OwningPid, ip, names));
            }
            return list;
        }

        static List<Listener> ParseV6(IntPtr buffer, Dictionary<int, string> names) {
            int count = Marshal.ReadInt32(buffer);
            int stride = Marshal.SizeOf(typeof(TcpRowV6));
            var list = new List<Listener>(count);
            for (int i = 0; i < count; i++) {
                IntPtr rowPtr = IntPtr.Add(buffer, 4 + i * stride);
                var row = (TcpRowV6)Marshal.PtrToStructure(rowPtr, typeof(TcpRowV6));
                int port = HostPort(row.LocalPort);
                if (port <= 0) continue;
                byte[] addr = row.LocalAddr ?? new byte[16];
                var ip = new IPAddress(addr);
                list.Add(Make(port, (int)row.OwningPid, ip, names));
            }
            return list;
        }

        static Listener Make(int port, int pid, IPAddress ip, Dictionary<int, string> names) {
            return new Listener {
                Port = port,
                Pid = pid,
                ProcessName = ResolveName(pid, names),
                Loopback = IPAddress.IsLoopback(ip)
            };
        }

        static Dictionary<int, string> SnapshotNames() {
            var map = new Dictionary<int, string>();
            int entrySize = Marshal.SizeOf(typeof(ProcessEntry));
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return map;
            try {
                var entry = new ProcessEntry();
                entry.dwSize = (uint)entrySize;
                if (!Process32First(snapshot, ref entry)) return map;
                do {
                    if (entry.th32ProcessID != 0 && !map.ContainsKey((int)entry.th32ProcessID)) {
                        string name = entry.szExeFile ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            name = Path.GetFileNameWithoutExtension(name);
                        if (name.Length > 0) map[(int)entry.th32ProcessID] = name;
                    }
                    entry.dwSize = (uint)entrySize;
                } while (Process32Next(snapshot, ref entry));
            } finally {
                CloseHandle(snapshot);
            }
            FillFromProcessList(map);
            return map;
        }

        static void FillFromProcessList(Dictionary<int, string> map) {
            Process[] processes = null;
            try {
                processes = Process.GetProcesses();
            } catch {
                return;
            }
            foreach (Process process in processes) {
                try {
                    if (process.Id > 4 && !map.ContainsKey(process.Id) && !string.IsNullOrEmpty(process.ProcessName))
                        map[process.Id] = process.ProcessName;
                } catch {
                } finally {
                    process.Dispose();
                }
            }
        }

        static string ResolveName(int pid, Dictionary<int, string> names) {
            if (pid <= 4) return "System";
            string name;
            if (names.TryGetValue(pid, out name) && !string.IsNullOrEmpty(name)) return name;
            name = ImageName(pid);
            if (string.IsNullOrEmpty(name)) name = "pid " + pid;
            names[pid] = name;
            return name;
        }

        // PROCESS_QUERY_LIMITED_INFORMATION works for services without running elevated.
        static string ImageName(int pid) {
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) return null;
            try {
                var buffer = new StringBuilder(520);
                int size = buffer.Capacity;
                if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) return null;
                return Path.GetFileNameWithoutExtension(buffer.ToString());
            } catch {
                return null;
            } finally {
                CloseHandle(handle);
            }
        }

        // dwLocalPort keeps the TCP port in network byte order in the low 16 bits.
        static int HostPort(uint value) {
            return ((int)(value & 0xFF) << 8) | (int)((value >> 8) & 0xFF);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(
            IntPtr table,
            ref int size,
            bool order,
            int family,
            int tableClass,
            uint reserved);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct TcpRowV4 {
            public uint State;
            public uint LocalAddr;
            public uint LocalPort;
            public uint RemoteAddr;
            public uint RemotePort;
            public uint OwningPid;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct TcpRowV6 {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] LocalAddr;
            public uint LocalScopeId;
            public uint LocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] RemoteAddr;
            public uint RemoteScopeId;
            public uint RemotePort;
            public uint State;
            public uint OwningPid;
        }
    }
}
