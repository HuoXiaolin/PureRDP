# 最小化与恢复回归检查

```powershell
. ./Build/scripts/_nuget-cache.ps1
dotnet build PureRDP/PureRDP.csproj -c Release
dotnet run --project Build/tests/WindowMinimize/WindowMinimize.csproj -c Release
```

使用测试 WinForms 连接窗口和真实 HWND，不建立 RDP 连接、不保存用户设置。普通窗口放在屏幕外；最大化和模拟真全屏检查会短暂显示测试窗口。检查未配对的绘制恢复不能显示隐藏窗口/画布、双窗口反复最小化、普通与最大化混合状态恢复、任务栏最小化恢复、模拟真全屏状态保留、快速连续切换，以及远程画布绘制锁不能移除顶层窗口可见性。

此测试能验证宿主窗口状态和绘制锁的 Win32 行为，不能替代真实 RDP 会话的闪烁和画面验收。

另覆盖：组最小化后由任务栏恢复全部/部分窗口，再次执行快捷键必须首次最小化；模拟真全屏外部恢复后走异步快捷键执行路径，也应保持原状态。
