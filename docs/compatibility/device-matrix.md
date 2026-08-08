# 设备支持等级与兼容性矩阵

## 支持等级

| 等级 | 支持内容 |
|---|---|
| L1 | TCP、LLRP 握手、协议版本、身份、标准能力查询 |
| L2 | 标准 Inventory、EPC、RSSI、天线、信道、SeenCount、时间戳 |
| L3 | 标准设置、Gen2 Filter、Tag Access、GPI/GPO |
| L4 | 厂商扩展，例如 Impinj Search Mode、FastID、Phase、Doppler |

## 首版矩阵

| 设备 | 协议 | 目标等级 | 状态 |
|---|---|---|---|
| 已验证标准 Reader | LLRP 1.0.1 | L1～L2，L3 按能力 | 基线，迁移后回归 |
| Impinj R420 | LLRP 1.0.1/SDK 自动策略 | L1～L4 | 基线，迁移后回归 |
| 其他标准 Reader | 以实测协议版本为准 | L1～L2 最低 | 待设备接入 |
| 其他厂商扩展 | 以模块和设备测试为准 | 未验收前不声明 L4 | 待扩展 |

## 记录要求

每个设备至少记录：

- 厂商、型号、固件版本；
- LLRP 协议版本和连接策略；
- 身份、能力、Inventory、Settings、Tag Access、GPI/GPO 结果；
- 扩展模块和扩展字段结果；
- 已知限制、错误码、复现步骤和测试日期。

设备支持等级必须来自实际测试，不能仅由厂商名称、SDK 包或接口实现推导。
