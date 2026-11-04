# 会话接管与远端关机断开回归检查

```powershell
./Build/scripts/purerdp.test.ps1 -Suite ShutdownDisconnect
```

注入 RDP 断开原因码，检查只有 ServerShutdown / 25 被识别为关机、重启与网络故障不误判、立即停止已有重试并释放加载页、异步关闭本连接、重复事件安全处理，以及其他连接不受影响。另检查重连页在连接窗口内、没有新增顶层窗口、2–1024 秒共十次递增等待、取消关闭当前窗口及十次后关闭。测试不建立 RDP 会话、不保存用户设置。真实服务器是否返回该原因码需要实际关机验证，测试不会关机或重启任何计算机。

会话接管检查使用微软定义的 ReplacedByOtherConnection / 5：覆盖普通设备、外部请求、预览窗口、正在等待及执行中的重试；确认关闭旧窗口、取消原生 MSTSC 回退、忽略迟到重试和重复事件，同时保留其他连接。通知在旧窗口关闭后写入控制台并准备托盘提示；测试托盘图标保持隐藏，不发送桌面通知。网络超时和服务器重启仍执行正常重试。

这是原生窗口配合原因码注入的离线回归，不等同于两台电脑实际争用同一远端会话的现场验证。原因码依据：[Microsoft ExtendedDisconnectReasonCode](https://learn.microsoft.com/en-us/windows/win32/termserv/extendeddisconnectreasoncode)。
