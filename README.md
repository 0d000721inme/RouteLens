# RouteLens

RouteLens 是一款开源 Windows 网络观察工具。从 Clash / Mihomo 读取活动连接，在一个界面中查看程序、域名、目标地区、实际代理链和流量变化，并提供地图、DNS 检测与可选的本地历史记录。

RouteLens is an open-source Windows network monitor for Clash / Mihomo, with connection and traffic views, a world map, DNS checks, and optional local history.

## 下载与运行

需要 **Windows 10/11 和 .NET Framework 4.8**。前往 [最新版本下载页](https://github.com/0d000721inme/RouteLens/releases/latest)，下载 `RouteLens-Windows-v7.1.0.zip`，解压后双击 `RouteLens.exe`；也可以下载 [单文件程序](https://github.com/0d000721inme/RouteLens/releases/download/v7.1.0/RouteLens.exe)。无需 Python、Node.js 或抓包驱动。

保持 Clash / Mihomo 运行，RouteLens 会尝试发现本机控制接口。普通权限可以使用连接、地图和流量视图；DNS 被动捕获需要管理员权限，权限不足时会标注检测受限。

## 功能

- **原始连接**：查看内核提供的程序、域名、目标 IP、国家或地区、代理链和命中规则；也可切换到 Windows 系统连接视图。
- **实时流量**：查看 Clash 活动连接的程序排行与速度趋势；另可查看各网卡独立的上下行速度。
- **历史流量**：可主动开启本机记录，按程序、域名、代理链或最终节点汇总，选择保留 7、30 或 90 天并清除记录。默认不记录历史。
- **世界地图**：查看目标 IP 的国家分布，在地图详情窗口缩放、拖动，并按国家查看程序和域名排行。
- **离线国家识别**：内置 DB-IP Country Lite 数据库；支持主动下载更新或导入国家版 CSV / CSV.GZ。无离线结果时可选择是否联网查询。
- **DNS 检测**：被动观察物理 IPv4 网卡上的明文 DNS 外发；也可以手动启动主动测试，查看测试服务观察到的解析服务器与请求出口。
- **联网提醒**：可选提醒新程序、新目标地区或 Clash 流量达到设定阈值；默认关闭，仅本次运行有效。
- **持续上传与目标观察**：可选持续上传提醒，帮助发现达到自设条件的活动；区分最近出现过的目标和观察到上传增量的目标，仅覆盖 Clash 已观察连接。
- **证据与系统变更**：查看本次采样状态和缺口、手动读取已有系统日志，按有效 Sysmon ProcessGuid 查看同一进程的其他已读取事件；可比较 Run 启动项快照，或手动导入本地 Zeek JSON 连接记录。

界面截图使用虚构程序、域名与节点：

![RouteLens 连接概览](docs/images/main.png)

![按程序查看实时流量排行](docs/images/traffic.png)

![世界地图与国家详情](docs/images/map.png)

![证据与采集状态](docs/images/evidence.png)

## 数据与能力范围

程序、域名和代理链来自 Clash 的本机接口，能否识别取决于内核提供的元数据；HTTPS 完整网址、页面内容或浏览器标签页无法从连接表直接获得。未经过该内核的连接可切换到 Windows 视图查看，但系统连接表无法从代理隧道还原原始网站，也不提供逐连接流量。

实时速度和历史流量来自 Clash 活动连接累计字节的连续采样差值；短连接可能遗漏，不代表整机或计费流量。网卡窗口显示每张接口的独立速度，不将虚拟、隧道与物理网卡相加以免重复计算，也不能归因到程序。地图采用国家级代表点，不能视为服务器精确位置或 VPN 出口实测位置。

持续上传是观察条件，不是感染或攻击判定；状态页所示计速连接和缺口不代表全机覆盖率。外部导入目前仅支持本地 Zeek JSON 连接日志，来源未经独立验证；尚未实现正式的 ETW 全机逐进程采集或服务及计划任务配置快照。系统日志读取依赖已有记录及权限；读取不到或没有记录不代表事件未发生。`experiments/etw/` 是独立研究原型，没有集成到程序中。

被动 DNS 检查只覆盖物理 IPv4 接口上的明文 DNS，不覆盖 IPv6、DoH、DoT 等路径；主动测试只反映测试请求自身的路径。未观察到异常不等于不存在泄露，解析服务器与出口的地区不同也不足以单独证明泄露。详细说明见 [使用说明](使用说明.md)。

离线查询在本机完成。启用联网国家查询时，未能离线识别的公网 IP 可能发送给查询服务；出口探测和缺少的旗帜也可能访问相应服务。主动 DNS 测试只在用户点击开始后联系第三方服务，它能看到该测试的请求出口 IP 和解析服务器；不会发送浏览历史。历史记录包含程序、域名与代理链，仅在用户启用后保存在本机 `data/`。系统事件、启动项及外部日志只在手动操作时读取、保存在窗口内存中；状态导出由用户选择位置，默认不含所选连接，可另行勾选包含。

## 意见与反馈

欢迎在 [GitHub Issues](https://github.com/0d000721inme/RouteLens/issues) 分享使用体验、报告错误或提出功能建议。反馈时请说明 Windows 与 RouteLens 版本、复现步骤和预期结果；截图、日志或导出内容请先检查并删除 IP、域名、用户名、密钥及其他不希望公开的信息。

## 从源码构建

项目使用 C#、WinForms 与 .NET Framework，不需要 NuGet 包。在 Windows PowerShell 中从仓库根目录运行：

```powershell
.\build.ps1
```

脚本调用 Windows 的 .NET Framework 编译器，将地图、旗帜与 `resources/dbip-country-lite.csv.gz` 内嵌到根目录的 `RouteLens.exe`。本地历史功能使用 Windows 自带的 SQLite 接口；发行包包含程序，个人 `data/` 不应包含在公开发布内容中。

相关检查使用虚构数据，不连接 Clash 或主动 DNS 服务：

```powershell
.\tests\RunTrafficTrackerChecks.ps1
.\tests\RunHistoryChecks.ps1
.\tests\RunOfflineChecks.ps1
.\tests\RunPrivacyChecks.ps1
.\tests\RunActivityAlertsChecks.ps1
.\tests\RunSystemTrafficChecks.ps1
.\tests\RunEvidenceChecks.ps1
.\tests\RunSystemTimelineChecks.ps1
.\tests\RunExternalEvidenceChecks.ps1
```

系统事件、启动项及外部记录检查使用虚构数据；这些检查不代表已经在真实日志、注册表数据或实际外部日志上验证采集。ETW 原型也仅通过合成检查，未在管理员环境中验证真实事件与归因。

## 许可证

RouteLens 源码采用 [MIT License](LICENSE)，允许使用、修改和分发；分发时请保留许可证与版权声明。界面参考、离线数据库、地图与旗帜等第三方资源保留各自的许可，来源和归属见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
