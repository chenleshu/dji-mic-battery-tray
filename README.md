# 大疆麦克风电量

在 Windows 通知区域实时显示 DJI Mic Mini 电量的轻量托盘程序，支持蓝牙与 USB 无线双接入，也支持同时连接多个麦克风和多个 USB 接收器。

完整的安装、使用、升级、故障排查和技术边界请参阅：[软件说明](docs/软件说明.md)。

## 支持的连接模式

- **大疆蓝牙连接**：读取 DJI Mic Mini 通过 Windows HFP 上报的电量百分比
- **USB 无线连接**：通过 DJI Mic Mini 接收器读取 TX1/TX2 的无线发射器电量档位和充电状态
- **双接入与多设备**：蓝牙和 USB 同时读取；一个托盘图标显示所有在线麦克风中的最低电量

## 界面截图

### 状态栏悬停提示

![大疆麦克风蓝牙与 USB 多设备电量悬停提示](docs/images/dji-mic-tray-tooltip-v1.4.3.png)

悬停提示使用蓝牙与 USB 图标区分接口，每支麦克风一行，只显示型号短名和电量，不显示序列号。

### 设备详情

设备详情按蓝牙和 USB 接收器分组；每支麦克风独占一行，电池图标中央直接显示百分比，并列出设备型号和识别信息。公开文档不展示真实设备序列号。

## 功能

- Windows 通知区域电池图标，颜色和填充量跟随电量档位
- USB 发射器充电时，托盘电池与详情电池徽标显示黄色闪电；悬停和详情文字同步标注充电状态
- 蓝牙连接时读取 Windows HFP 电池指示并显示设备上报的百分比
- 悬停以 `📶`、`🔌` 区分蓝牙和 USB，并逐行显示型号短名与电量，不显示序列号
- 右键“设备详情”逐项显示所有麦克风的电量与充电状态
- 设备详情按蓝牙与各 USB 接收器分组，每支麦克风独占一行并显示百分比电池图标
- 读取并显示麦克风产品类型与识别号，可区分 `DJI Mic Mini`、`DJI Mic Mini 2`、`DJI Mic Mini 2S`
- 正常电量使用绿色；估算低于 10% 使用橙色；5% 使用红色
- 右键菜单支持设备详情、立即刷新、登录后自动启动和退出
- 通过接收器 `MI_00` 的 WinUSB 中断端点直接读取大疆连接键，从源头阻止 Windows 音量命令与音量浮层，再映射为自定义按键或组合键
- 内置 `右 Alt`、`右 Alt + Shift`、`右 Alt + 空格` 三组一键预设，也可录制最多 6 个键的自定义组合
- 使用右 Alt 控制 Typeless 时，可在文字补全并停止变化后自动发送一次回车；最长等待时间和文字停止变化后再等待时间均可自定义
- 每 8 秒同时刷新蓝牙和 USB；蓝牙掉线后自动排除其旧电量并切换到 USB
- 保持 USB Audio 接口原驱动，不影响麦克风录音

蓝牙模式不需要 WinUSB。当前实机已验证一支 DJI Mic Mini 的免提录音端点、电量显示和掉线切换；程序只把状态为 Active 的免提录音端点视为在线，避免继续显示断开前的旧电量。设备通过 HFP 向 Windows 上报的是百分比，因此不会标注“约”。当前 Windows 蓝牙设备节点没有提供可靠的充电属性，所以蓝牙模式不猜测充电状态；USB 接收器报文提供充电位，可准确显示“充电中”。

USB v2 身份帧会提供接收器和发射器的序列号、产品名称。详情菜单按接收器归类 TX1/TX2，并直接使用设备上报的产品名称；未取得身份帧时明确显示“未识别”，不会根据接口编号猜测型号。

DJI USB 协议返回的是 1–7 档电量状态，不是精确百分比。界面按以下映射显示百分比，但不再添加“约”或波浪号；这些 USB 数值仍属于档位换算结果：

| DJI 档位 | 悬停显示 | 图标颜色 |
| --- | --- | --- |
| 1 | 100% | 绿色 |
| 2 | 80% | 绿色 |
| 3 | 60% | 绿色 |
| 4 | 40% | 绿色 |
| 5 | 20% | 绿色 |
| 6 | 9% | 橙色 |
| 7 | 5% | 红色 |

这些百分比只用于粗略视觉判断，不代表 DJI 提供的精确剩余电量。

## 连接键映射

部分 DJI 麦克风的连接键会被 Windows 识别为“音量加”。2.0.0 将接收器 `USB\VID_2CA3&PID_4011&MI_00` 精确绑定到 WinUSB，由程序直接读取 Interrupt-IN 报告；Windows 不再为该接口创建 Consumer Control HID，因此不会收到大疆按键的音量命令，也不会触发原生音量浮层。右键托盘图标，展开“连接键短按映射”即可选择预设、录制自定义组合或关闭映射。首次运行默认启用并映射为 `右 Alt`，选择的组合会保存到当前用户配置。

程序动态枚举 `MI_00` 的 Interrupt-IN 端点，并按一次 USB transfer 解析报告：`06-01-00` 为按下，`06-00-00` 为松开；按设备分别维护状态，每个按下沿只发送一次目标组合，其他报告直接忽略。普通键盘和其他设备仍由 Windows 原样处理。自定义组合最多包含 6 个常规 Windows 按键；`Ctrl+Alt+Del` 等系统安全序列无法由普通应用模拟。

默认 `右 Alt` 已针对 Typeless 2.5.0 的 Dictate 快捷键适配：注入时携带真实键盘扫描码，并保持 120 ms 后再释放；按一次开始听写，再按一次停止。`右 Alt + Shift` 预设实际发送 Typeless 使用的右 Shift，`右 Alt + 空格` 可对应 Ask Anything。

“识别完成后自动回车”默认开启，仅用于 `右 Alt` 启停式听写。第一次短按会记录当前前台窗口和输入框；第二次短按且右 Alt 完全释放后开始等待文字补全。只有原输入框文字相对停止听写前的内容确实发生变化，并在最后一次变化后连续保持不变达到设定时间，程序才发送一次回车。默认最长等待时间为 20.0 秒，文字停止变化后再等待时间为 0.8 秒。前台窗口或输入焦点改变、空语音、识别失败、文字未变化或等待超时时均不会发送回车。

本工具并不直接读取 Typeless 的“结束输入”状态。Typeless 2.5.0 的 `Thinking` 和进度条没有向 Windows UI Automation 暴露可稳定使用的完成信号，而且动画消失早于异步文字插入完成。因此程序通过原目标输入框的文字变化与停止变化时间来判定补全。目标应用需提供可写的 `ValuePattern`，或由 `Edit` 控件提供 `TextPattern`。程序只在内存中比较文字长度与指纹，不记录文字正文。

当前 Windows 11 与 Typeless 2.5.0 组合已实机完成 2 个“开始—停止—文字补全—自动回车”周期验证；程序自身诊断记录两次回车均成功注入，并确认系统音量仍为源头拦截状态。

WinUSB 绑定范围是整个 `MI_00`，因此该接口下的 Consumer Control、Telephone 和两个厂商自定义 HID collection 都不再交给 Windows。`MI_01` USB Audio 和 `MI_06` 电量接口保持独立；蓝牙直连按键不经过此 USB 接口，不在当前按键拦截范围内。安装与恢复步骤见 [MI_00 WinUSB 安全配置说明](docs/MI00-WINUSB.md)。

## 系统要求

- Windows 10/11 x64
- 蓝牙模式：DJI Mic Mini 已在 Windows 中配对，并启用 `Hands-Free` 录音端点
- USB 电量：接收器 Interface 6 使用 WinUSB
- 连接键映射：接收器 Interface 0 使用由 Zadig/libwdi 配置的 Windows `WinUSB`；Interface 1 音频保持 Windows `usbaudio`

## 使用发行版

从仓库 Releases 下载 `DJI-Mic-Battery-Tray-v2.0.1.exe` 即可直接运行电量托盘。需要自动安装、桌面快捷方式和登录后自动启动时，请下载完整包 `DJI-Mic-Battery-Tray-v2.0.1.zip` 并运行其中的安装脚本。新电脑若要使用连接键完全拦截，请按照包内的 [MI_00 WinUSB 安全配置说明](docs/MI00-WINUSB.md)，使用 Akeo Consulting 签名的 Zadig 程序只替换 Interface 0。Zadig/libwdi 会为该设备生成自签名目录证书并加入本机 Root 与 Trusted Publishers，随后销毁私钥；不接受这项系统证书变更时，请只使用电量功能。

v2.0.1 将自动启动从 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 改为当前用户的 Windows 登录计划任务，托盘菜单名称为“登录后自动启动”。安装位置固定为 `%LOCALAPPDATA%\DjiMicBatteryTray\DjiMicBatteryTray.exe`；安装器同时创建桌面快捷方式，并通过受控方式启动该任务，核对实际进程路径、版本、进程 ID、启动来源和任务启用状态。该受控启动链已在当前 Windows 11 实机验证；本次没有注销或重启 Windows，因此不把真实的下一次登录触发列为已验证项目。

## 从源码构建

不需要额外安装 .NET SDK，构建脚本使用 Windows 自带的 .NET Framework C# 编译器：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\tests\model.test.ps1
```

输出文件位于 `dist\大疆麦克风电量.exe`。

安装到当前用户、创建桌面快捷方式并启用登录后自动启动：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

卸载：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

## 实现说明

蓝牙模式只读访问 DJI `Hands-Free AG` 设备节点上的 Windows HFP 电池属性。USB 电量模式访问 Interface 6 的 WinUSB bulk-IN `0x86`；连接键映射从 Windows 设备注册表读取 Interface 0 当前注册的接口 GUID，再访问其 WinUSB Interrupt-IN 并动态查询端点地址。所有候选路径仍严格核对 `VID_2CA3&PID_4011&MI_00`。Interface 1 始终保留 Windows USB Audio 驱动。电量轮询和按键读取均在后台执行。协议识别参考了开源项目 [ShadowBitBasher/DJI-Mic-Control](https://github.com/ShadowBitBasher/DJI-Mic-Control)。

## 许可证

[The Unlicense](LICENSE)
