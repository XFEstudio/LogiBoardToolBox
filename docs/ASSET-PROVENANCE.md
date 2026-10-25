# 本地界面还原素材来源

2026-10-25 多型号扩展另提取了本机 G HUB 2026.7.992920 的 13 张 `core_assets/thumbnails` 设备图，原样复制到 `Assets/Devices/Catalog`。没有生成替代设备外形。原路径、型号元数据及逐文件 SHA256 记录在本地 `work/multi-device/catalog-sources.json`；它们与下述素材具有相同的本地参考用途和权利边界。

根据用户要求，应用从本机已安装 G HUB 取得以下本地参考素材。没有复制其专有页面 JS/CSS 到应用实现。

- 设备图原文件：`C:\ProgramData\LGHUB\depots\876190\8807090d-3e9c-4dfe-a6a6-a863d7b688d4\images\device\front_black.webp` 和 `side_black.webp`，无损转换 PNG。
- 字体原文件：`C:\Program Files\LGHUB\resources\app.asar` 中 `assets/288e4f97861b5f5f.otf`（Regular）和 `assets/185a760fa7bac8a8.otf`（Bold），提取后核对 asar 内 SHA256。
- 按键、LED 点位与遮罩：同一设备资源的 metadata.json、light_map.json，重新用 WinUI 图形实现。
- 三款鼠标动画数据：本机 `C:\ProgramData\LGHUB\depots\876190\core\LGHUB\data\lighting\effects\mouse-{verticool,redwhiteblue,oceanwave}.json`，按原文件复制为 `Assets/Lighting/*.json`；分别包含 3、3、22 帧。来源哈希清单位于 `work/visual/animation-asset-sources.json`。同样属于本地 G HUB 资源，不适用本项目代码的开源许可。

这些素材属于其各自权利人，不是本项目自行创作或可默认以开源许可再分发的资产。当前提取用于用户要求的本地界面复现；对外发布、商用或重新分发前，应独立核实授权或替换有适当许可的素材。

| 应用文件 | SHA256 |
| --- | --- |
| LogiBoardToolBox/LogiBoardToolBox/Assets/Devices/G502XPlus/front_black.png | `d7286b0a786be88ddebb17d27f94ebeabbec62160afa167e9c50df54f6f0db38` |
| LogiBoardToolBox/LogiBoardToolBox/Assets/Devices/G502XPlus/side_black.png | `4a97ab97818867e0f36e2cb7d0dd617e47695445ec895d1fb695d19aa186b7a3` |
| LogiBoardToolBox/LogiBoardToolBox/Assets/Fonts/BrownLogitechPan-Regular.otf | `c02c5591422f2a682819696669807a19f7e5355744aeff20c1fa866890c08a92` |
| LogiBoardToolBox/LogiBoardToolBox/Assets/Fonts/BrownLogitechPan-Bold.otf | `0895125f8a487ae1669a65eac0092bb11084553800dbceecb8c366ef8ecb868c` |
| LogiBoardToolBox/LogiBoardToolBox/Assets/Lighting/verticool.json | `e253aea0bb61a05938727884ff343ad082fb7157055440fd329ab719d38073d3` |
| LogiBoardToolBox/LogiBoardToolBox/Assets/Lighting/redwhiteblue.json | `0bfde5f32da146717bdb055061f9171e797e136e79093fec838d8232af98e1b4` |
| LogiBoardToolBox/LogiBoardToolBox/Assets/Lighting/oceanwave.json | `4f50f354c11d3bf07cf7f12205b36fb088fc282d27d64a54cf98c8386914ec36` |
