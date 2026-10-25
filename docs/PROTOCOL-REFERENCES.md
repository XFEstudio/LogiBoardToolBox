# 协议与开源参考

本项目使用独立的 C# 实现，通过 Windows 已有 HID 驱动配置设备，不安装内核过滤驱动。

## 参考项目

多型号的具体范围、G HUB 元数据来源及设备/接收器识别方式见 [多型号适配](MULTI-DEVICE-SUPPORT.md)。2026-10-25 新增对照 libratbag 的 GPW、SUPERLIGHT、G305、G403、G502、G703、G903 设备文件，以及 Solaar 的 0x8070 LED Control/LEDZoneSetting。软件灯帧不设置旧接口的闪存持久化位。

- [libratbag / hidpp20.c](https://github.com/libratbag/libratbag/blob/master/src/hidpp20.c)：功能 0x8100、扇区读写、目录、旧格式配置布局。
- [libratbag / hidpp20.h](https://github.com/libratbag/libratbag/blob/master/src/hidpp20.h)：按键、宏操作码、灯效结构；对应文件附 MIT 许可声明。
- [libratbag PR #1895](https://github.com/libratbag/libratbag/pull/1895)：完整宏写入、鼠标事件、循环与等待释放。它是未合并 PR，不能等同于已发布并普遍验证的实现。
- [libratbag PR #1850](https://github.com/libratbag/libratbag/pull/1850)：宏扇区写入顺序与部分固件在 write-end 返回错误的行为。
- [Solaar / hidpp20.py](https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/hidpp20.py)：按键编码、配置、255 字节尾部读取、目录与 CRC。
- [logi-ghub-linux / onboard.py](https://github.com/theo-antonescu/logi-ghub-linux/blob/main/src/logi_ghub/features/onboard.py)：以 G502 X 存储为依据的配置、宏和备份实现。
- [logi-ghub-linux / device.py](https://github.com/theo-antonescu/logi-ghub-linux/blob/main/src/logi_ghub/device.py)：G502 X 按键的存储顺序与物理名称。
- [lexr1/omm.py](https://github.com/lexr1/omm.py)：Windows HID++ 板载工具，以及 [宏控制说明](https://github.com/lexr1/omm.py/blob/main/docs/BUTTON_MAPS.MD)。
- [Deadband / macros.py](https://github.com/broroeror/gamesir-linux-tools/blob/370d3a8e5d4d3f3070ba1ca53a9d7ac449a63574/vendors/logitech/macros.py)：声明在有线 G502 X 实测的 43/44 键盘事件编码，与当前的修饰键字节、HID usage 字节顺序一致；不能代替本机 PLUS 接收器的执行验证。
- [OpenMouse 配置格式研究](https://github.com/OpenMouse-Project/mouse-protocol/blob/main/docs/logitech-onboard-profiles.md)：格式相关布局差异与验证方法。该资料没有证明本机的格式 10 写入。

这些项目具有各自的许可；协议资料与原始代码的许可不等于本项目的整体许可。没有复制 G HUB 的页面应用代码；设备图和字体按用户的本地还原要求提取，来源与权利边界见 ASSET-PROVENANCE.md。

## 实现要点

- 先通过 Root 查功能索引，不能把 0x8100 当作固定报文索引。
- HID++ 回复必须匹配设备地址、功能、函数和软件 ID；通知、其他程序回复和接收器错误不得被当作成功。
- 本机 G HUB 的板载模式切换先执行 0x8110 函数 2（关闭设备按键监听），再执行 0x8100 函数 1 的模式 1。设备监听状态可能在原程序退出后残留；板载切换和写入入口需独立完成这一交接，不能只清理本程序创建的软件会话。
- 本机 G HUB 编译的独立 Shift 动作为 `43 02 E1` / `44 02 E1`，同时保留修饰位和 HID usage。宏编译器采用该编码，软件执行时按键持有集合去重，避免重复按下或提前释放。
- 0x8100 函数 5 读取、6 开始写入、7 传输 16 字节、8 结束写入；字节地址中的函数位需左移 4 位。
- CRC-16/CCITT-FALSE：初值 FFFF，多项式 1021；末两字节以大端序保存，不参与自身计算。
- 255 字节扇区最后从偏移 239 读取 16 字节，不能从 240 越界读取。
- 宏指针是 12 位扇区与 16 位偏移；跳转操作码 60 的参数为偏移、页号。
- 宏数据先写入、配置随后、目录最后；分配时保护所有配置及其跳转所引用的旧宏空间。
- 本机 G HUB 的宏写入函数在结束符后补 `FF` 到偶数偏移。离线核对 `lghub_agent.exe` 的 RVA `0x0213BE5F..0x0213BE89`，可见写入 `FF`、检查偏移最低位、奇数时再补 `FF`；原生快照的入口 0/14/40/54/80/106 与此一致。分配器采用相同对齐，旧奇数入口可读但不再用于导入去重；迁移时保留原扇区和其他配置。
- 写入前完整备份并检查存储是否变化；每个扇区写入后比对全部原始字节；失败时恢复所有触及扇区并报告恢复结果。
- 格式 10 的第二组 DPI 数据不能当作旧格式的末尾按键记录解析。当前仅开放有限字段的实验性编辑，其他字节必须保持原值。

## 验证边界

模拟设备测试覆盖了正常提交、半途失败、回滚、原始主机模式恢复、过期快照、跨扇区宏、空间不足和未知布局等情况。模拟设备测试不能证明实际固件会执行新宏；物理鼠标写入和重连测试需要单独验证。

G HUB 的槽位编号应与其设备元数据匹配，不能直接照搬其他工具的 G 编号标签。根据本机物理反馈与 G HUB 原生配置写入，G502 X PLUS 的 G5 对应记录 4、G6 对应记录 5；导入、界面编号和侧视图点位已统一。2026-10-23 的接收器实测已确认左键按住重复的原生输出。键盘取消刀的编译字节与 G HUB 原生单次版本一致；原生对照记录到 G6 实体信号 `0x0020`，但在按键监听开启时没有键盘输出。板载交接遗漏的监听关闭步骤已修复，当时关闭监听后的真实执行尚未确认。2026-10-24 用户同意物理拔插接收器的对照，重新枚举后捕获 4 组完整的原生取消刀序列，且用户确认生效；该轮没有写入、切换模式或注入输入。USB 有线对照未执行。临时测试的全部 19 个扇区、模式和活动配置均已核对恢复，之后仅执行不截键、不改键的限时后台观察。

后续已实际修正并写入宏入口对齐：取消刀 6:39 → 8:0，左键连点 6:65 → 8:26，其他按键与配置字段不变。全部 19 个扇区匹配预期；取消刀字节仍为 `43003140003B4400314000C643001E40001944001E40000A02FF`。这证明布局和读回一致，不能证明实际输出。接收器只读固件为 4.2.9，通知标志 `000900`，未改动这些寄存器。未调用原生 `ExecuteMacro` 或其他生成输入的命令，也未继续实体按键对照。该对齐修正单独应用时仍无效，不能认定为根因。后续恢复前的 HIDCLASS 跟踪显示键盘读取 IRP 为 0；接收器重新枚举后原生执行恢复，初始停读触发原因尚未确定。恢复记录见 `work/ghub-binding-fix/resume-20261024/verified-cancel-recovery.json`。

## 本轮新增参考与实机只读结果

- [OpenLogi / extended_dpi](https://github.com/AprilNEA/OpenLogi/blob/6f1299c5c2a7d963dab44d4459da07902340a136/crates/openlogi-hidpp/src/feature/extended_dpi.rs)：0x2202 的分页 DPI 范围、双轴设置。
- [OpenLogi / rgb_effects](https://github.com/AprilNEA/OpenLogi/blob/6f1299c5c2a7d963dab44d4459da07902340a136/crates/openlogi-hidpp/src/feature/rgb_effects.rs)：0x8071 软件控制和固件效果参数。

本轮补充：

- [OpenMouse / mode-status](https://github.com/OpenMouse-Project/mouse-protocol/blob/bac031567b28f2a55c616fc525b03f2d5fa452cc/src/drivers/logitech/mode-status.ts)：0x8090 的四字节状态/掩码写入；LIGHTFORCE 仅修改状态位 0。
- [OpenLogi / calibration events](https://github.com/AprilNEA/OpenLogi/blob/6f1299c5c2a7d963dab44d4459da07902340a136/crates/openlogi-hidpp/src/feature/extended_dpi/event.rs)：0x2202 事件 1 的传感器、轴、修正值与垂直偏移；`0x8000` 为失败，不以请求应答当作完成。
- [Logitech HERO 校准说明](https://support.logi.com/hc/en-001/articles/16236814907927-How-does-the-HERO-2-Sensor-Calibration-feature-work-for-Gaming-mice)：源鼠标/DPI、传感器对齐、同时移动、分析偏差流程。

2026-10-23，本机 G502 X PLUS/27.1.22/LIGHTSPEED 的 0x8090、0x8071 设置切换/读回已实测并恢复。0x2202/8 在板载模式返回 0x05，在软件模式正常返回 `00 3C 00 7D 01 90 01 90`，即宽 60、长 125、校准基准 400×400 DPI；它不能据板载模式的错误推断为校准不可用。成功的双鼠标测量尚未验证。0x8100 格式 10 的单次连点宏已实际写入、物理触发并完整恢复；这些结论仅覆盖对应测试，不扩展为其他型号或所有宏模式均可用。
- [Solaar / hidpp20.py](https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/hidpp20.py)：0x1004 电量、0x0003 固件的版本编码。
- [LogitechGDeviceInfo](https://github.com/infinitysky/LogitechGDeviceInfo)：G502 X PLUS 接收器的 0x1004、0x8071 等兼容性参考。

本机格式 10 的 X DPI 位于 3..12，Y DPI 位于 86..95，按键位于 32..83 / 96..147，回报率枚举位于 20/21，灯效位于 208..251；0xFF 旧回报字段不可覆盖。支持 13 个分配项（11 个物理按键和上下滚轮）。2202 当前读到 10800×10800 DPI、100..44000 能力范围；8061 当前 1000 Hz。8071 报告的原生效果有限，其余通过 8081 八区 RAM 帧生成，映射顺序 3,7,2,4,8,6,5,1。

当前闪存备份的 19 个扇区、主机模式 2 和活动扇区 0 在验证前后逐字节一致。该结果只能证明验证未修改存储，不能证明写入兼容性。

WinUI 按钮模板状态依据本机 Windows App SDK 2.5.1 解析的 WinUI 2.3.9 generic.xaml 核对。ToggleButton 使用 CommonStates 的 Normal / Checked / CheckedPointerOver 等状态，不能使用 CheckBox 的 Unchecked 状态名。
