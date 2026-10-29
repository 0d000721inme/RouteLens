# 网络证据观察：实现与后续计划

RouteLens 面向本机网络观察。这里记录各项能力的当前状态和限制；观察异常并不等于感染、攻击或分布式攻击。一个无法获取的证据不应被当作不存在。

## v7.1.0 当前实现

- 可选的持续上传观察使用连续 Clash 活动快照中可比较的字节增量，默认关闭。需要每个当前观察到的连接都可比较；连接新增、计数重置或采样缺口会中断连续计时。阈值是观察条件，不是恶意判定。
- 流量观察明确区分最近出现的目标和有上传增量的目标。它只反映被观察到且可计量的活动连接，不能证明其他目标未被访问。
- 证据页保留本次运行中的采样状态、缺口和提醒；选中的连接只展示当前快照。默认导出状态不包含当前连接，可由用户明确选择包含。事件只在内存中保留，最多 200 条，关闭软件后消失。
- 系统事件页只在用户点击后读取已有的 Sysmon、系统服务与任务日志，最多每来源 100 条；没有权限、日志不存在、超限和无记录都会分别说明。有效 Sysmon `ProcessGuid` 可用于展示同一进程实例的其他已读取事件，不按 PID 或时间猜测；关联不等于因果或恶意。它不安装 Sysmon，不修改策略或记录；尚未使用真实日志验证。
- 启动项页只在用户操作后建立和比较 Run 注册表项的两次快照，仅内存保存，不显示命令内容，不覆盖服务、计划任务或其他启动位置。快照差异不能证明实际执行或恶意。
- 支持手动导入本地 Zeek `conn.log` JSON 行（8 MiB、5,000 条上限），仅内存显示且标注未经独立验证，不自动关联进程；不支持网络路径及其他日志格式。
- `experiments/etw/` 是未集成的研究原型：合成解析测试不等于真实采集验证。没有正式的全机逐进程 ETW 采集功能。

## 后续优先级

1. **真实权限和归因验证。** 在隔离的 Windows 10/11 管理员测试环境，使用自有测试程序覆盖 TCP/UDP、IPv4/IPv6、短连接、进程退出和 PID 重用。以接收端计数和独立抓包对照；记录 ETW 丢事件、内部队列丢弃及解析失败。无可靠分母时不展示覆盖率。
2. **按需采集架构。** 主界面维持普通权限；只有用户主动开启时才运行受限采集组件。优先验证自有 ETW 会话，冲突时停止并报告，不停止其他工具的会话。需要限制本机通信权限、执行范围、资源、持续时间和异常清理；尚未完成的权限隔离不能宣传为安全服务。
3. **按进程实例关联。** 以系统启动标识、PID 和创建时间标识进程；有可靠 `ProcessGuid` 时保留关联。无法查证的实例或重用 PID 显示未知。事件载荷 PID、采集时间和关联依据分别保留。
4. **代理与外层分账。** Clash 内层连接、代理/隧道外层和选定出口观察点分别显示，不相加。只有明确映射才能关联；共享隧道不可仅根据时间和流量推断唯一应用。Fake-IP 不参与公网地理判断；域名来源与证书验证是不同证据。
5. **更多变化来源和调查包。** 扩展服务与计划任务配置快照、日志保留范围、事件来源关联；默认仅保留有限元数据。将命令行、原始事件、抓包载荷分开授权，导出前预览和逐项脱敏。随后再研究定向限时抓包及更多路由器/Zeek 格式的导入；外部记录需要明确来源与覆盖范围，不自动标为可信。

## 参考项目

以下为设计和可行性参考，不代表 RouteLens 已集成其代码；正式复用代码前应固定提交、核对涉及文件和依赖的许可证及行为。

- [Net Analyzer](https://github.com/davidpbj/net-analyzer)：MIT，Windows 网络、进程和 ETW 示例；其实现方式及停止会话行为需要独立审查。
- [System Informer](https://github.com/winsiderss/systeminformer)：当前项目标注 MIT，可参考 [ETW 网络事件处理](https://github.com/winsiderss/systeminformer/blob/master/plugins/ExtendedTools/etwmon.c)；需单独审查 UDP 远端与归因语义。
- [Microsoft KrabsETW](https://github.com/microsoft/krabsetw)：ETW 采集封装参考；不能自动解决归因、丢事件和数据口径。
- [Simple NetMon](https://github.com/simple-netmon/oss)：ETW 和本地监控参考；按目录采用不同许可证，发行程序涉及 AGPL/GPL，不能笼统当作 MIT。
- [WinDivert](https://github.com/basil00/WinDivert)：LGPLv3 或 GPLv2 双许可；需要驱动，默认拦截与纯观察有区别，本项目目前不集成。

系统资料：[Windows TCP ETW](https://learn.microsoft.com/en-us/windows/win32/etw/tcpip)、[UDP ETW](https://learn.microsoft.com/en-us/windows/win32/etw/udpip)、[ETW 会话和权限](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracea)、[Sysmon 事件](https://learn.microsoft.com/en-us/sysinternals/downloads/sysmon)。
