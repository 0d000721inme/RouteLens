# RouteLens

Windows 网络连接观察工具：从 Clash / Mihomo 读取原始连接，把程序、域名、目标 IP 国家和实际代理链放在一起，提供实时流量排行、世界地图与 DNS 明文外发风险提醒。

RouteLens is a Windows network monitor for Clash / Mihomo, with per-program traffic rankings, country maps, and DNS exposure alerts.

## 下载与运行

需要 **Windows 10/11 和 .NET Framework 4.8**。下载 [RouteLens-Windows-v6.zip](release/RouteLens-Windows-v6.zip)，解压后双击 `RouteLens.exe`；也可下载 [单文件程序](release/RouteLens.exe)。无需 Python、Node.js 或抓包驱动。

Clash / Mihomo 保持运行即可自动发现本机控制接口。默认普通权限运行；DNS 被动捕获需要管理员权限，普通权限下会明确显示检测受限。

## 功能

- **原始连接**：查看程序、域名、目标 IP、国家/地区、代理链与命中规则。可切换 Windows 系统连接视图，检查未经过 Clash 的连接。
- **实时流量排行**：按程序汇总下载、上传和总速度，沿用主列表的搜索与国家筛选。
- **世界地图**：显示目标 IP 的国家分布；点击“放大地图”，查看该国的程序、域名与连接数排行。
- **DNS 风险提醒**：在物理 IPv4 接口上被动观察明文 DNS 外发，结合请求发生时的代理状态提示风险。

界面截图均使用虚构程序、域名与节点：

![RouteLens 连接概览](docs/images/main.png)

![按程序查看实时流量排行](docs/images/traffic.png)

![放大世界地图与国家详情](docs/images/map.png)

## 数据与能力范围

实时速度来自 Clash **每条活动连接的累计字节差值**，只覆盖当前内核提供的连接。Windows 连接表不提供逐连接流量。累计值是仍存活连接自建立以来的字节之和，连接结束后移出；它不是整机流量、运行总量或计费流量。首次采样、计数重置或暂停恢复时需要重新计速。

地图使用国家级代表点，表示目标 IP 的地理归属，不能当作服务器精确位置或 VPN 节点实测位置。域名来自内核元数据，不能显示 HTTPS 完整页面地址或浏览器标签页。

DNS 检查仅覆盖物理 IPv4 接口上的明文 DNS，不覆盖 IPv6、DoH、DoT 或监测开始前的流量。权限不足与未观察到外发均不等于没有泄露；直连规则、引导解析也可能有意产生外发，需要结合代理策略判断。

程序、域名和代理链通过本机接口读取，不上传到国家查询服务。启用联网国家查询时，仅向 IP 查询服务发送待识别的公网 IP，并在本地 `data/` 缓存国家结果；出口探测和旗帜下载会访问各自的服务。发布内容不含个人缓存或 Clash 配置。详细行为和第三方服务说明见 [使用说明](使用说明.md)。

## 从源码构建

项目使用 C#、WinForms 与 .NET Framework，不需要 NuGet 包。在 Windows PowerShell 中从仓库根目录运行：

```powershell
.\build.ps1
```

脚本调用 Windows 的 .NET Framework 编译器，将旗帜与地图内嵌到根目录生成的 `RouteLens.exe`。构建输出和 `data/` 已被 Git 忽略；`release/` 保留本版下载文件。

运行流量计算的 48 项检查：

```powershell
.\tests\RunTrafficTrackerChecks.ps1
```

检查使用虚构连接，覆盖采样间隔、计数重置、新连接、暂停、过期、程序归组与溢出等情况，不连接 Clash、不发送网络请求。

## 许可证

RouteLens 源码采用 [MIT License](LICENSE)，允许使用、修改和分发；分发时请保留许可证与版权声明。地图和旗帜等第三方资源的来源及适用说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
