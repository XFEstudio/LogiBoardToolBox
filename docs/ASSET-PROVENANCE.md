# 本地界面还原素材来源

2026-11-04：按本机 G HUB build 880806 的设备定义，另补齐 28 张设备图，覆盖目录共 43 款鼠标。原图来自该构建的 `core_assets/thumbnails`；核对来源内容 SHA-256 后无损转为 PNG，没有生成替代外形。来源路径、型号、原文件/解码内容/输出哈希和尺寸记录于本地 `work/device-artwork-fix/artwork-sources.json`。发布目录内逐款实际解码，相关来源哈希表见本文末尾。这些资源沿用本文的素材权利边界，不包含 G HUB 程序代码、解密密钥或固件。

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


## 2026-11-04 补充设备图哈希

| 型号 / 应用文件 | 原文件 SHA-256 | 输出 SHA-256 |
| --- | --- | --- |
| MX518 / `MX518.png` | `d9e403abf774b674aba59751380ba3dc166789ee1e11141dff3c1ff69cfca34a` | `64ad2a5857aa7e7670b01434454d4b3d5bd3b5da506a02d3cfd474c73ad68048` |
| ProWired / `ProWired.png` | `36f50cb5c8fce5129cf90bbcb22bb53af9b49e8a6e3bd3157df21987cdafd4fb` | `778d5e9f11029bafe0fa17edbe614d3413023dc7b2594ba800c2624d22b9f955` |
| ProHero / `ProHero.png` | `36f50cb5c8fce5129cf90bbcb22bb53af9b49e8a6e3bd3157df21987cdafd4fb` | `778d5e9f11029bafe0fa17edbe614d3413023dc7b2594ba800c2624d22b9f955` |
| G203Lightsync / `G203Lightsync.png` | `547dc67824bf2449dca3945ee373c917d0ef28cbaa1aea9bd5110a11ca0d90bf` | `8bf5cb53a0d0e5b92ee77405b0adebae58e7695f7b67c48e4d8b86d674a3e3d5` |
| G203 / `G203.png` | `dd7bfea13623969c034e6e531053cc834952dbd30ef6b890ba85b8125b7675f2` | `af10bdbccebcd225e84ab852f777e77baaf095a7aab04635782a14b2cd9e4f13` |
| G302 / `G302.png` | `8437fa1a8590167fa015c8fe0518575a9808331d41fb04b4bd6490e882d4c74a` | `d70a39230b88c969b1c3962ef2fb7af78c5cf576f0d87df9942d2bfeafdf8f1f` |
| G303 / `G303.png` | `8437fa1a8590167fa015c8fe0518575a9808331d41fb04b4bd6490e882d4c74a` | `d70a39230b88c969b1c3962ef2fb7af78c5cf576f0d87df9942d2bfeafdf8f1f` |
| G502Core / `G502Core.png` | `ac2bf43b3de68468103092af31ad251f1f98d73cb49cdf65a152177dd12a0e61` | `4f2b049f6e5a2dcb0c913f56a95190b072ad93348cf04ab577fcdc8c2c2d8f09` |
| G502Spectrum / `G502Spectrum.png` | `756058161dc1ca77e16037794daa1580f6e8cab1378854afc09db7cd170f1d0e` | `459abc5e0ee7933392b599cbe0ee566a270d90dd797302ea6912e1a016329056` |
| G600 / `G600.png` | `29e6078ca1c18703ba48a368aede991ee9df4afe4c5272ca9cd43f806e85bb34` | `8268f3ffb14dd88a1b8e44cc788b7d6b703c9cd44940a62817b9f1fd63300276` |
| G602 / `G602.png` | `edf41267dffbc5ba4efa79f4e0c2fc971eb3193eaf924dbf87a684d0455122d9` | `9ca205b60afa63eee4aee3c017eeb43a7c13ef7180e761d83bf4129b6ea5b103` |
| G603 / `G603.png` | `d39f5c2f908364c975462f7f81a8bc098fbf3dfae1c80a31cdc59d80fd091d3a` | `297ee293021b601cdf2bcb6ffe62f3494f47bba653e28f7a46076165631f6976` |
| G604 / `G604.png` | `3b82e3e994c0ccc284ba19aaf96205f0274e22ff5aab90a1d956b788061b3f33` | `c43d245ca75051eb17a0b86a39afaf42a2f46bf83e67b0fbb4e2652280dfeaba` |
| G502XLightspeed / `G502XLightspeed.png` | `458e21d191790f2fb986b79eeb840a5db28478f3890c8070257756f72997c8ea` | `42570414643acfd510123055904078e6e1028d6ffa2dffabd88a7ad3364cc19f` |
| G502X / `G502X.png` | `458e21d191790f2fb986b79eeb840a5db28478f3890c8070257756f72997c8ea` | `42570414643acfd510123055904078e6e1028d6ffa2dffabd88a7ad3364cc19f` |
| G303Shroud / `G303Shroud.png` | `fe692cf02143179b529ef6072fd739602639efde25d91a3398e6ba493b80a6cc` | `ba40a2ed146764cb634264dc01f006800141adb1c0ed06e700d786272aa50e2e` |
| G705 / `G705.png` | `6f2ef2260215646a3ebc216ce026263b79331904e97b8066c55e05c292cd0d1d` | `bfcd7ce347d572040a98422bd828c145276120d974226baf9fa867596314017d` |
| Superlight2 / `Superlight2.png` | `e7c9ba4ce33cdbfd855931d33b0723be1403933164a7a495fcd72488cc410284` | `18d8634dfecf93e6657df79998d99a793c87205ed3a302708e9b770081d0414f` |
| G502XMillennium / `G502XMillennium.png` | `01b4d9d60dca6d51a5c6e009aa1d5b7395dce74bcd3b3605c7a946200e2c1b06` | `2a0a5828ca61ee7b8ccab487d5b56802226f4c5c42be281b3b28c2047882ad89` |
| G309 / `G309.png` | `7344a7f87b1cfe02b93a8d762aeb163dc90cf85ecf4b74bfc9c411c2eb2a3ce6` | `7dc23d48278a606419ccae86767e2efb4856477659a4ad07d7698711aaaae5bd` |
| Pro2 / `Pro2.png` | `f41e61b0ca000242f998b8291567b9f3bdd33557d8ba0b93502e6723a8beb92c` | `7144f953a5c8d602acf408ba0a8ecd01cfe6178f706195492e2b02e99052230c` |
| Superlight2Dex / `Superlight2Dex.png` | `c12f91eada6c1541418f566926a14acb3a05d9ce1632ef8c4f417bff0bb5a365` | `2b0433adcf2294dd5f49a95a41e06ba7dd82bb98058948fdc53be905ef511ed5` |
| Superlight2c / `Superlight2c.png` | `4975cd3b4e700db474ab4dd5ab10e4a2279aa92f944e45c837e57c86697787ad` | `a345665231b3a28efc882aa870e343a4a4d5a4d00a66fe080d3533c4aab9e240` |
| G304X / `G304X.png` | `6cd4a77a055a927b74fb98c414b0e2e9af49f304541199c72444954a4e801297` | `30f2411c08c657dbd64e5a32bd5a4f91d5e7939ad57fac04d969a729b83a1e0b` |
| G305XSuperlight / `G305XSuperlight.png` | `ecab4a4c89a0254f96f2ce66e93563579841b036ea6dfd1d8af188efd01dd736` | `4d53d8e0cdc462f74c4294ee4df6e4551087a5b177cc9affd988b64ce23965f5` |
| Superstrike2 / `Superstrike2.png` | `0206c4ffb055b003e435235c93f366353bfdd72cb9097ad7cc22f07d29497e3e` | `49dfa891e43da685dee925181afecbf906145d674c32b8e7e5b6a162ef44f589` |
| Superstrike3 / `Superstrike3.png` | `dc3cefb91a8169e1eaad331f29a951b6ea4108acb9b032dda0a13712ca980ee8` | `3dd182eab30aec044a2c6eb5550e11cc1fc7bd583e49a4cfbee5acbca3ba5644` |
| Superstrike1 / `Superstrike1.png` | `f1523795b07704749761efa487fc8c885df9241ce3889a64da339f5c471b7ccb` | `6ef67eec0dceeadf18657a88eda3cda5fc221e08e5292984cc5357f6c7ef290e` |
