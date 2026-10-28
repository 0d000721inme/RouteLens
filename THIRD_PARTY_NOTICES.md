# Third-party resources and services

RouteLens 的 MIT 许可证适用于本项目源码；下列外部数据、图像和服务保留各自的来源及适用条款。

## 开源项目参考和移植

- [Mihomo Traffic Ledger](https://github.com/severin-ye/mihomo-traffic-ledger)：桌面界面的分组导航及统计布局参考并改写其 MIT 许可的界面设计。该项目由 Clash Traffic Monitor 衍生，保留两项上游版权；完整许可见 [mihomo-traffic-ledger-MIT.txt](third_party/licenses/mihomo-traffic-ledger-MIT.txt)。
- [DNS Leak Test](https://github.com/macvk/dnsleaktest)：主动 DNS 解析器测试参考其 MIT 许可的测试流程；完整许可见 [dnsleaktest-MIT.txt](third_party/licenses/dnsleaktest-MIT.txt)。

## DB-IP 离线国家数据库

IP Geolocation by [DB-IP](https://db-ip.com). 本项目附带并内嵌 [DB-IP Country Lite 2026-10](https://db-ip.com/db/lite.php)，数据库按 [Creative Commons Attribution 4.0 International (CC BY 4.0)](https://creativecommons.org/licenses/by/4.0/) 发布，与 RouteLens 源码的 MIT 许可证分别适用。附带数据库未做修改；文件校验值及来源详见 [DB-IP-NOTICE.md](resources/DB-IP-NOTICE.md)。

## Natural Earth 地图

Made with Natural Earth. 底图和国家代表点由 Natural Earth 矢量数据整理而来。Natural Earth 将其栅格和矢量地图数据置于公共领域，允许使用、修改和再分发。

- [Natural Earth 使用条款](https://www.naturalearthdata.com/about/terms-of-use/)
- [110m 国家面数据](https://github.com/nvkelso/natural-earth-vector/blob/master/geojson/ne_110m_admin_0_countries.geojson)
- [50m 国家面数据](https://github.com/nvkelso/natural-earth-vector/blob/master/geojson/ne_50m_admin_0_countries.geojson)

原始文件的 SHA-256、字段选择、坐标处理和绘制范围见 [src/maps/provenance.md](src/maps/provenance.md)。该来源说明原样保留。

## 旗帜图像

内嵌的旗帜 PNG 与按需下载的旗帜来自 [Flagpedia / FlagCDN](https://flagpedia.net/download/api)，运行时下载地址形式为 `https://flagcdn.com/w40/{code}.png`。本说明记录其来源，不将这些图像重新许可为项目源码的 MIT 许可证；有关图像使用说明请参阅来源网站。

## 可选联网服务

这些服务不随仓库分发；启用相应功能时，软件会向它们发起请求：

- [ipwhois.io](https://ipwhois.io/documentation)：公网 IP 地理信息查询。
- [ipify](https://www.ipify.org/)：本软件请求的公网出口探测。
- [FlagCDN](https://flagpedia.net/download/api)：缺少内嵌图像时的旗帜下载。
- [DB-IP Lite](https://db-ip.com/db/lite.php)：用户主动选择下载时获取国家数据库更新。
- [bash.ws](https://bash.ws/)：用户主动运行 DNS 测试时，服务可观察该测试请求的出口 IP 和参与解析随机测试域名的解析服务器。

服务的可用性、额度与使用条件由各提供方决定。详细的数据处理范围见 [使用说明](使用说明.md)。
