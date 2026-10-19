# 功能截图说明

这里保存 README 引用的六张页面截图：设备控制台卡片与列表、设备连接设置、窗口偏好、快捷键、设备状态扫描。

控制台截图中，前两台演示设备的左上角黄色小星星表示置顶；其他设备显示空心星星。

图片由当前程序的实际 WinForms 控件渲染，不是设计稿。全部设备和账号为虚构；使用 `192.0.2.0/24`、`198.51.100.0/24`、`203.0.113.0/24` 和 `.example` 演示地址。状态、连接时间及网络数值也是示例，不能作为实际连接或性能测试结果。密码输入框只显示演示掩码。

截图中的账号统一为 `example-user`。若可见控件包含用户目录、临时目录、程序目录或工程目录，生成工具会替换成 `C:\Users\ExampleUser\…`、`C:\Apps\PureRDP\…`、`C:\Projects\PureRDP\…` 等通用示例，并检查没有残留其他 Windows 用户目录；不修改应用真实运行路径。

## 重新生成

在 Windows、.NET 10 SDK 环境下，从仓库根目录运行：

```powershell
. ./Build/scripts/_nuget-cache.ps1
dotnet build PureRDP/PureRDP.csproj -c Release
dotnet run --project Build/tools/Screenshots/Screenshots.csproj -c Release
```

生成结果位于 `outputs/documentation-screenshots/`。检查页面完整性和隐私后，再将 PNG 复制到本目录。不要用 `outputs/ui-review/` 中的日常回归图替代，它们可能显示本机目录或实际日志。

截图工具只显示页面和注入内存演示数据，不发起 RDP、预览连接、网络探测或更新请求，也不会点击保存和修改 Windows 凭据。页面初始化可能只读加载本机的下拉历史或启动项；导出的画面在填入测试数据之后生成，不包含这些内容。
