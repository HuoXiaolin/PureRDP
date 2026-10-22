# 远端关机断开回归检查

```powershell
./Build/scripts/purerdp.test.ps1 -Suite ShutdownDisconnect
```

注入 RDP 断开原因码，检查只有 ServerShutdown / 25 被识别为关机、重启与网络故障不误判、立即停止已有重试和关闭弹窗、异步关闭本连接、重复事件安全处理，以及其他连接不受影响。测试不建立 RDP 会话、不保存用户设置。真实服务器是否返回该原因码需要实际关机验证，测试不会关机或重启任何计算机。
