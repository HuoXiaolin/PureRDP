# 每日打包版本回归

运行 `Build/tests/DailyVersion/DailyVersion.ps1`（PowerShell 7）。测试只创建 `outputs/daily-version-tests/` 下的隔离版本文件，并校验真实 `Build/targets/version.props` 没有变化，不构建或发布软件包。

覆盖第三段递增、同日只递增一次、北京时间零点、跨年、跳过未打包的日期、四段版本映射、显式标签、日期/版本损坏保护和跨进程并发。打包失败后当天重试沿用已分配版本，避免跳号。
