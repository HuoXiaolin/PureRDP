# 设备星标置顶回归

从仓库根目录先编译 Release 应用，再运行：

```powershell
dotnet run --project Build/tests/Favorites/Favorites.csproj -c Release
```

测试使用离屏 WinForms 控件、示例地址和内存 JSON，覆盖黄色小星星绘制、点击与键盘切换、取消置顶、分组拖动、搜索、视图切换、旧配置兼容、重载保留以及地址变更后的星标迁移。拖动检查经过 DragEnter/DragOver/DragDrop 事件处理，包含三种视图下的双向移动、重复悬停、快速落下和顺序保存。不会建立远程登录或保存用户配置。

这不能替代真实远程预览会话、多显示器及高 DPI 环境的人工验证。
