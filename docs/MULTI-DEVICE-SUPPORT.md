# 多型号适配

2026-10-25：在现有 WinUI 架构中加入 14 个型号目录。目录提供身份、G HUB 槽位前缀、按键标签和本机 G HUB 设备图；真正的配置槽数、按键数、G-Shift、DPI、回报率、宏格式和闪存容量均读取设备，目录不能覆盖能力检查。

| 型号 | G HUB 槽位前缀 | RGB 编辑器 | 本项目实机验证 |
| --- | --- | --- | --- |
| G502 X PLUS | g502x-plus | 八区 | 已验证读写、左键连点及键盘取消刀 |
| PRO WIRELESS / GPW | prowirelessmouse | 按实际接口，G HUB 定义两区 | 待验证 |
| PRO X SUPERLIGHT（第一代） | proxwirelessmouse | 隐藏，无可编程 RGB | 待验证 |
| G304 / G305 | g305 | 隐藏，无可编程 RGB | 待验证 |
| G502 HERO | g502hero | 按实际接口 | 待验证 |
| G502 LIGHTSPEED | g502wireless | 按实际接口 | 待验证 |
| G403 有线 / 无线 / HERO | g403-1 / g403 / g403hero | 按实际接口 | 待验证 |
| G703 / G703 HERO | g703 / g703hero | 按实际接口 | 待验证 |
| G900 / G903 / G903 HERO | g900 / g903 / g903hero | 按实际接口 | 待验证 |

SUPERLIGHT 2 等后续型号按独立系列识别，不冒充第一代 SUPERLIGHT。无法唯一识别时显示通用分配图；未知板载格式保留读取和备份，不套用 G502 的扩展布局。未初始化存储或读取不完整时仍停止写入，不能把读失败当作空白闪存。

## 2026-10-29 识别修正

目录增加至 18 个型号系列，并区分国行名称对应的实际产品：

| 型号系列 | 身份依据 | 界面与当前验证范围 |
| --- | --- | --- |
| PRO 2 LIGHTSPEED / GPW4 朱雀 | USB C09A、无线 WPID 40A8 | 八键对称布局；专属图暂缺时显示通用点位，板载执行待实机验证 |
| PRO X SUPERLIGHT 2 / SE（GPW3 / GPW2 金刚） | USB C09B、无线 WPID 40A9；2 与 SE 共用设备 ID | 五键、无 RGB；同系列识别，不按共用 PID 冒认第一代，板载执行待实机验证 |
| PRO X SUPERLIGHT 2 DEX / GPW4 鹰眼 | USB C0A0、无线 WPID 40B8 | 五键、无 RGB；与朱雀分开识别，板载执行待实机验证 |
| G402 Hyperion Fury | USB C07E，HID++ 2.0 | 八键及本机 G HUB 原始设备图；实际布局和宏能力由设备回复决定，待实机验证 |

国行型号对应关系由[罗技官方商城](https://store.logitech.com.cn/Wap/Product/Detail/12400)确认。USB/WPID 目录交叉核对 [OpenLogi 型号目录](https://openlogi.org/hidpp/model-ids)、[OpenMouse 驱动注册与支持列表](https://openmouse.app/supported)和 [libratbag 的 G402 定义](https://github.com/libratbag/libratbag/blob/master/data/devices/logitech-g402.device)。G402 图片来自本机 `core_assets/thumbnails/g402.png`，遵循 [素材来源](ASSET-PROVENANCE.md)。本机新型号的 G HUB 元数据为加密资源，尚未取得可核对的专属图片和宏槽位前缀，因此这些系列暂不直接导入 G HUB 配置；普通宏和 Lua 导入不依赖 G HUB 槽位前缀。

扫描不再因缺少 `0x8100` 板载接口而隐藏已识别、可以回应 HID++ 的鼠标；这类设备显示能力说明，并保持写入受限。未知 HID++ 接收器根设备或键盘不会因此被误当成受支持鼠标。已知有线 USB 鼠标直接探测 FF 地址，避免依次等待六个不存在的接收器槽位。身份和能力探测只读，不修改固件、配对、监听或闪存。

新设备如返回格式 7，不能套用旧的格式 1–5。本轮对照 [OpenMouse 对 G HUB 原生布局及实机数据的研究](https://github.com/OpenMouse-Project/mouse-protocol/blob/main/docs/logitech-onboard-profiles.md) 增加独立的受限编解码：按键表位于 0x30 / 0x70（各 12 条）；DPI 默认/Shift 索引在 0x02/0x03，独立 X/Y 记录在 0x04 起每项五字节；有线/无线回报率枚举位于 0x01/0x00，闲置字段位于 0x2C/0x2E，名称仍在 0xA0。保留每个已有启用档位的 LOD、未知区、保留按键区及灯效字节，拒绝调用旧偏移写入；启用/停用速度时只按 G HUB 的对应三元记录规则更新有效性。

格式 7 仅接受内存模型 1、255 字节扇区、最多五个配置及十二个按键，并检查配置目录、可写/宏扇区边界、CRC 和每个 DPI 三元记录；格式 1 宏由共享的空间分配器写入，其他宏格式继续拒绝。有线回报率限制在已核对的 1000 Hz 以内，无线值仍受实际传感器能力约束。板载 RGB 的原始位置虽然已知，但原生效果编码尚未完成交叉验证，因此格式 7 暂不启用持久化灯效写入，所有灯效区和标志保持原样；实时软件灯效仍依实际 HID++ 能力运行。

验证包含公开的格式 7 出厂扇区（CRC 84DB）逐字节编解码、双轴/回报率/按键/G-Shift/名称/闲置偏移、未知字节与 LOD 保留，以及内存设备中的宏写入、读回和完整恢复。上述为协议与模拟事务验证，尚未在用户的朱雀、金刚或其他格式 7 实体鼠标上执行；也未写入当前 G502 X PLUS。

## 操作与写入

设备设置的“设备诊断与软件配置”中选择鼠标。切换先等待旧设备自动保存完成，再释放旧设备、清理草稿并读取新设备，避免不同鼠标的“配置 1”互相带入按键。

选中板载配置后，有效修改继续合并后即时提交。文本输入保留草稿，完成输入后校验；没有因增加型号而恢复逐字验证。宏使用相同的格式 1 编译器，保留每一个录制的 `durationMs`，不插入统一延迟。不同型号不会继承 G502 的滚轮扩展记录，超出真实按键数量或没有 G-Shift 层时拒绝绑定。单独的按住循环序列等原有编码限制仍然存在。

写入前全量备份，检查快照未过期；宏扇区先写，配置和目录随后，逐扇区读回。失败恢复原始数据。配置文件中的未知字节、其他配置、原宏和录制延迟保持原样。

G HUB 导入工具自动使用目标设备的槽位前缀，并核对 G HUB 记录中的型号与序列号。多只鼠标同时连接时，必须指定目标：

```powershell
dotnet run --project tools/LogiBoardToolBox.Diagnostics -c Release -- --plan-ghub settings.json work/import-preview --device <完整Identity>
dotnet run --project tools/LogiBoardToolBox.Diagnostics -c Release -- --import-ghub settings.json work/import-write --device <完整Identity>
```

先生成计划的命令不写设备。此处 `settings.json` 是导出的 G HUB 设置 JSON，不是直接读取 SQLite 文件的入口。

## 灯效

0x8070 使用它返回的灯区、效果索引和控制状态，软件帧的持久化位为 0，只写 RAM；退出时恢复可读的原效果和控制状态。0x8071 支持按 RGB 簇更新；具有 0x8081 时使用实际 LED ID，G502 X PLUS 单独保留已经验证的 `3,7,2,4,8,6,5,1` 顺序。灯区数量贯穿渲染、屏幕采样区域、时间线和每帧颜色，不再统一要求八区。

屏幕/音频联动和动画的实时生成仍需要主机。可保存的固件预设与软件灯效分别处理；没有可验证的 EEPROM 动画布局时不会伪称板载播放。旧型号不提供 G502 X 的标志性板载效果。

## 依据与验证边界

身份、前缀、可选物理侧键区及 RGB 定义来源于本机 G HUB 2026.7.992920 的 `core/data/devices`。GPW 的 G4/G5 为左侧，G7/G8 为右侧，G6 为底部 DPI。设备图来自同版本 `core_assets/thumbnails`。来源与 SHA256 记录在本地 `work/multi-device/catalog-sources.json`；这些图遵循原有本地素材说明。

协议同时对照 [libratbag 设备目录](https://github.com/libratbag/libratbag/tree/master/data/devices)、[libratbag HID++ 实现](https://github.com/libratbag/libratbag/blob/master/src/hidpp20.c)、[Solaar 板载布局](https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/hidpp20.py) 和 [Solaar LED 控制](https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/settings_templates.py)。C# 编译与事务实现沿用本项目代码，没有复制其页面应用代码。

新增验证覆盖 14 个型号命名空间、共享接收器的型号识别、不同槽位/按键容量下的宏提交和完整恢复、越界绑定、可变灯区及 0x8070/0x8071 簇控制。模拟几何只用于测试边界，不冒充对应型号的硬件备份。

`--catalog-verify <目录>` 使用隔离设置和模拟快照，让真实 WinUI 控件生成后台截图，验证型号切换、宏保存/取消、两区动画及无 RGB 的导航；不枚举 HID、不生成或拦截输入、不写鼠标。截图和运行检查分别留存。其他型号的设备图与编号连线尚未完成对应 G HUB 页面逐项比对，不能声称全界面一致；新型号的物理宏输出仍须对应硬件验证。
