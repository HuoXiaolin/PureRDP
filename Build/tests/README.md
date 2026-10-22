# 离线回归入口

从仓库根目录运行（Windows、PowerShell 7）：

```powershell
./Build/scripts/purerdp.test.ps1
# 可选：指定已有 SDK 或只运行一组
./Build/scripts/purerdp.test.ps1 -Dotnet 'D:\Agant\toolchain\dotnet\dotnet.exe' -Suite WindowMinimize
```

统一入口默认创建不切换的独立 Windows 桌面，再启动构建和测试。窗口、最大化、最小化、模拟全屏等检查仍创建真实 HWND，但不会显示在当前使用的桌面。控制台输出和 XML/日志报告保持可用；无法创建隔离桌面时直接报错，不回退到前台执行。

测试子进程使用独立 Job Object，超时或入口退出时清理本轮子进程，不终止用户的 PureRDP 或远程会话。不使用系统桌面切换、全局隐藏窗口或修改用户显示设置。

不要直接运行单项 GUI 测试 EXE 或 `dotnet run` 来规避统一入口，否则无法保证测试窗口不出现在当前桌面。需要单项检查时用 `-Suite` 指定；纯版本脚本测试可单独运行。

报告位于 `outputs/ci-tests/TestResults/`。离线 HWND 和模拟缩放检查不等于真实 RDP 会话、多显示器或物理显示器 DPI 切换验收。

实现依据：[STARTUPINFOW 的 lpDesktop](https://learn.microsoft.com/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow)、[Windows Job Objects](https://learn.microsoft.com/windows/win32/procthread/job-objects)。
