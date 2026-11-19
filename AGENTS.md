# Tiny Sticky 协作规约

改这个仓库之前先读 [README.md](README.md)。程序是一张给共用 Windows 机看的便利贴，不是服务管理器。

## 名字

项目叫 tiny-sticky。程序、命名空间、互斥量、配置目录都叫 TinySticky。窗口和托盘上写 Tiny。不要再使用改名之前的旧名字。

## 可以改什么

- 界面、扫描和清单解析在 `src\`。语言停在 C# 10，目标是 .NET Framework 4.8、x64、WinForms。不要引入 NuGet，也不要改成 .NET 6 以上，否则这台机器上现成的编译器和运行时对不上。
- 只通过 `build.ps1` 打包。它会把根目录的 `services.json` 写进 `src\EmbeddedJson.cs` 再调用 csc。`EmbeddedJson.cs` 是生成文件，不要手改。
- 界面文案用中文。默认宽约 480，高度包住内容，可以拖边缘改大小。不要加第二块窗口或设置页。
- 默认清单只登记这台机器上长期开着的服务。临时端口让扫描落到「未登记」，不要为了少一行字把它们写进清单。

## 不要改什么

- 便利贴跑在当前登录用户的桌面里。不要把它注册成 Windows 服务，服务会话里画不出这个窗口。
- 扫描用普通用户权限就能完成。不要为了读进程命令行要求管理员。
- 读不到名字的进程（受保护的系统进程）不要显示成「未登记」。清单里的端口即使进程名读不到，也要算在对应服务上。
- 健康检查只打 `services.json` 里写的地址，超时保持在 1 秒左右，失败只改那一行的状态。

## 改完要验证

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
dist\TinySticky.exe --self-test
```

`--self-test` 必须退出码为 0。改了窗口上的字或排版，再跑一次 `dist\TinySticky.exe --snapshot preview.png`，看图，不要只看编译通过。

`dist\TinySticky.exe` 是打包结果，不要提交。`*.exe` 和 `dist/` 已在 `.gitignore` 里。

发布只走 tag：推送 `v*`（例如 `v0.1.0`）后，`.github/workflows/release.yml` 在 Windows 运行器上执行 `build.ps1`，通过自测后把 exe 发到 GitHub Release。不要在流水线里另写一套编译命令。
