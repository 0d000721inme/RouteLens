# Third-party resources and services

RouteLens 的 MIT 许可证适用于本项目源码；下列外部数据、图像和服务保留各自的来源及适用条款。

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

服务的可用性、额度与使用条件由各提供方决定。详细的数据处理范围见 [使用说明](使用说明.md)。
