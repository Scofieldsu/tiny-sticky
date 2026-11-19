# Tiny Sticky

共用 Windows 机器上的一张便利贴。它每隔几秒看一次本机正在监听的 TCP 端口，对照服务清单，标出哪些服务在跑、哪些端口不在清单里。

窗口置顶、半透明，跟着当前登录用户走。清单里登记的后台进程不受它开关的影响。

## 运行

从 [Releases](https://github.com/Scofieldsu/tiny-sticky/releases) 下载 `TinySticky.exe`。Windows 10 / 11 自带 .NET Framework 4.8，不用另外安装运行时。程序是 64 位的。

exe 不放进 git 仓库。推送 `v` 开头的 tag（例如 `v0.1.0`）后，GitHub Actions 会编译、跑自测，并把 exe 发到这个 tag 的 Release。

窗口上分三块：

| 状态 | 含义 |
| --- | --- |
| 正常 | 清单里的端口都在监听，健康检查也通过 |
| 未启动 / 部分异常 / 无响应 | 清单里有这项服务，但端口没人听，或只听了一部分，或健康检查失败 |
| 未登记 | 有程序在听端口，但清单里没有。系统进程不显示 |

拖标题栏可以移动，拖窗口边缘可以改大小，字号会跟着窗口变。双击「Tiny」收成一行。标题栏第二行是给这台机器的提醒：双击那一行写下要给后来的人看的话，回车保存，清空后回车就删掉。右下角时间只有一处。窗口最底下的色条写着移动、改大小和收起。右键可以换浅色、改透明度、置顶、穿透点击、编辑清单，以及设成登录时启动。穿透打开后鼠标会穿过窗口，从托盘图标右键可以关掉。

未登记的一行可以点开改名字，回车后写进当前清单。超过 6 个未登记进程时，点「还有 N 个进程」会展开全部。

位置、大小、颜色和透明度记在 `%APPDATA%\TinySticky\window.json`，每人一份。

## 服务清单

按这个顺序读取，读到就停：

1. `C:\srv\services.json` — 共用机上的一份
2. exe 旁边的 `services.json`
3. 打进程序里的默认清单

默认清单是仓库根目录的 [`services.json`](services.json)。改的是名字、端口和健康检查地址，例如：

```json
{
  "name": "MinIO",
  "health": "http://127.0.0.1:9000/minio/health/live",
  "ports": [
    { "port": 9000, "label": "API" },
    { "port": 9001, "label": "控制台" }
  ]
}
```

保存后最多等 5 秒就会刷新。不要把 DeepSeek Harness 写进去，它的页面端口每次启动都会变。

## 编译

在项目目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物在 `dist\TinySticky.exe`（这个目录被 git 忽略），旁边会带上一份 `services.json`。本机和 GitHub Actions 用的是同一份 `build.ps1`。编译需要 Visual Studio 2022（含 MSBuild），不从网上拉包。

检查逻辑是否还对：

```powershell
dist\TinySticky.exe --self-test
dist\TinySticky.exe --scan
```

`--scan` 把当前机器的识别结果打到控制台，不打开窗口。
