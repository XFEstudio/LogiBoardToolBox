# 鼠标型号与原图覆盖

2026-11-04：根据本机 G HUB build 880806 可读取且已核对的设备定义，目录从 18 款扩充至 43 款。每个型号都有自带的原图，发布包不依赖目标电脑安装 G HUB。G502 Core、Spectrum、HERO、LIGHTSPEED、X、X LIGHTSPEED、X PLUS 和 Millennium Falcon 分别识别；GPW3 对应 SUPERLIGHT 2，朱雀对应 PRO 2 LIGHTSPEED。

本页的覆盖范围是型号身份、图片和已核对的 G HUB 命名空间。它不代表所有罗技型号或所有板载功能已经实机验收。板载读写仍由实际 HID++ 接口、0x8100 布局及原有检查决定；尚未核对的按键名称不从产品外观猜测，界面使用设备返回的数量。专有/旧接口设备不会因为加入图片目录而获得新的底层驱动。

| 型号 | 鼠标 USB/WPID/蓝牙标识 | 按键名称来源 |
| --- | --- | --- |
| G502 X PLUS | `4099`, `C095` | 已核对的型号目录 |
| PRO WIRELESS | `C088`, `4079` | 已核对的型号目录 |
| PRO X SUPERLIGHT | `C094`, `4093` | 已核对的型号目录 |
| G305 | `4074` | 已核对的型号目录 |
| G502 HERO | `C08B` | 已核对的型号目录 |
| G502 LIGHTSPEED | `C08D`, `407F` | 已核对的型号目录 |
| G703 LIGHTSPEED | `C090`, `4086` | 已核对的型号目录 |
| G703 | `C087`, `4070` | 已核对的型号目录 |
| G403 HERO | `C08F` | 已核对的型号目录 |
| G403 | `C083` | 已核对的型号目录 |
| G403 | `405D`, `C082` | 已核对的型号目录 |
| G903 LIGHTSPEED | `C091`, `4087` | 已核对的型号目录 |
| G903 | `C086`, `4067` | 已核对的型号目录 |
| G900 | `C081`, `4053` | 已核对的型号目录 |
| PRO 2 LIGHTSPEED | `C09A`, `40A8` | 已核对的型号目录 |
| PRO X SUPERLIGHT 2 / SE | `C09B`, `40A9` | 已核对的型号目录 |
| PRO X SUPERLIGHT 2 DEX | `C0A0`, `40B8` | 已核对的型号目录 |
| G402 | `C07E` | 已核对的型号目录 |
| MX518 | `C08E` | 按设备返回数量显示编号 |
| PRO | `C085` | 按设备返回数量显示编号 |
| PRO HERO | `C08C` | 按设备返回数量显示编号 |
| G203 LIGHTSYNC | `C092`, `C09D` | 按设备返回数量显示编号 |
| G102 | `C084` | 按设备返回数量显示编号 |
| G302 | `C07F` | 按设备返回数量显示编号 |
| G303 | `C080` | 按设备返回数量显示编号 |
| G502 Proteus Core | `C07D` | 已核对的型号目录 |
| G502 Proteus Spectrum | `C332` | 已核对的型号目录 |
| G600 | `C24A` | 按设备返回数量显示编号 |
| G602 | `C537` | 按设备返回数量显示编号 |
| G603 | `406C` | 按设备返回数量显示编号 |
| G604 LIGHTSPEED | `4085`, `B024` | 按设备返回数量显示编号 |
| G502 X LIGHTSPEED | `C098`, `409F` | 已核对的型号目录 |
| G502 X | `C099` | 已核对的型号目录 |
| G303 SHROUD EDITION | `409E`, `C097` | 按设备返回数量显示编号 |
| G705 | `C096`, `B02E`, `409D` | 按设备返回数量显示编号 |
| G502 X PLUS MILLENNIUM FALCON™ | `C09E`, `40B2` | 已核对的型号目录 |
| G309 LIGHTSPEED | `40B1`, `B03C` | 按设备返回数量显示编号 |
| PRO X SUPERLIGHT 2c | `40B7`, `C09F` | 按设备返回数量显示编号 |
| G304 X LIGHTSPEED | `40BC`, `C0A7` | 按设备返回数量显示编号 |
| G305 X SUPERLIGHT | `40BB`, `C0A4`, `B046` | 按设备返回数量显示编号 |
| PRO X2 SUPERSTRIKE | `40BD`, `C0A8` | 按设备返回数量显示编号 |
| PRO X3 SUPERSTRIKE | `40BE`, `C0A9` | 按设备返回数量显示编号 |
| PRO X1 SUPERSTRIKE | `40CD`, `C0B7` | 按设备返回数量显示编号 |

G HUB 同时将 Core 和 Spectrum 命名为 G502，因此只收到通用名称时保留待识别状态，使用各自的物理 PID 区分，不按名称猜测具体一代。部分旧固件未完整报告传输标识时保留已核对的物理有线 PID；接收器根不按此方式覆盖鼠标身份。

原图来自已安装 G HUB 的 core_assets/thumbnails。新取用 28 张，经原文件 SHA-256 核对后无损转换 PNG；不包含解密密钥、G HUB 代码或固件。来源及输出哈希保存在本地 work/device-artwork-fix/artwork-sources.json；权利范围参见 ASSET-PROVENANCE.md。

后台验收使用 --catalog-verify <目录> --artwork-only，逐款确认图片已解码、原图 URI、占位图隐藏及型号一致，并验证缺失资源说明与恢复。验收数据为隔离模拟数据，不打开实体 HID、不启动宏引擎或输入拦截。证据位于本地 work/visual/device-artwork-20261104/。
