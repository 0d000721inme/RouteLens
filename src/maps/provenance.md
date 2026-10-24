# 世界地图数据来源

Made with Natural Earth. 底图为 Natural Earth 的公共领域矢量数据；软件内置绘制，不调用在线地图服务。

- [官方使用条款](https://www.naturalearthdata.com/about/terms-of-use/)：Natural Earth 栅格与矢量数据属于公共领域，允许修改与再分发。
- [110m 国家面原始文件](https://github.com/nvkelso/natural-earth-vector/blob/master/geojson/ne_110m_admin_0_countries.geojson)：来自项目维护者仓库，用于海岸线与低分辨率国家轮廓。
- [50m 国家面原始文件](https://github.com/nvkelso/natural-earth-vector/blob/master/geojson/ne_50m_admin_0_countries.geojson)：只提取 ISO_A2_EH、NAME、LABEL_X、LABEL_Y，补充小国家和地区的代表点。

读取 ISO_A2_EH 作为匹配代码。50m 数据提供的代表点优先；110m 面按代码归并。每个 polygon 的外环和内洞均保留，坐标四舍五入到 0.001 度并去除相邻重复点。外环统一方向、内洞反向，配合 GraphicsPath 的 Winding 填充保留孔洞。未提供 ISO 代码的原始图形合并到 `--` 底图元素，该元素的经度为 181，不可用于连接定位。XK 沿用数据源中的标识。

代表点是国家或地区的制图标签位置，不是 IP 精确位置。部分小岛在该比例尺下没有轮廓，但只要有代表点就可显示连接标记。不猜测缺少的代表点：未知代码或纬度超出当前绘图范围时计入“未定位”。为保持宽屏概览可读性，绘图显示南纬 60 度至北纬 85 度；南极不在此视图内。

资源仅包含公共地理数据，没有用户连接、浏览域名、VPN 节点、密钥或 IP 查询缓存。下载于 2026-10-22。

## 原始文件 SHA-256

- `ne_110m_admin_0_countries.geojson`：`6866c877d39cba9c357620878839b336d569f8c662d3cfab4cb1dbe2d39c977f`
- `ne_50m_admin_0_countries.geojson`：`3e458fc036ad0a66411f2c1e6cac49c5d7bfb81cb1123bc513b22511a2b7fdeb`
