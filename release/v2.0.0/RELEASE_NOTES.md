## 大疆麦克风电量 v2.0.0

这是连接键映射功能的重大更新，并包含自 v1.4.3 以来的充电状态与稳定性改进。

### 连接键映射重写

- 将 DJI `USB\VID_2CA3&PID_4011&MI_00` 精确绑定 WinUSB，在 Windows HID/Shell 收到音量命令之前源头拦截。
- 解析 `06-01-00` 按下和 `06-00-00` 松开报告，每次短按只发送一次目标组合。
- 动态枚举 Interrupt-IN 端点，按每个 USB transfer 处理报告，并按物理接收器分别维护按下/释放状态。
- 内置右 Alt、右 Alt + Shift、右 Alt + 空格预设。
- 支持录制最多 6 个常规 Windows 按键组成的自定义组合，并自动保存设置。
- 删除 Raw Input、低级键盘钩子、Core Audio 音量恢复和按时间推断来源的旧方案。
- 其他键盘和其他 HID 设备的音量键不会被映射。
- 右 Alt 注入携带真实扫描码并保持 120 ms，可直接切换 Typeless 2.5.0 听写启停。
- 可在 Typeless 文字补全并停止变化后自动发送一次回车；最长等待时间与文字停止变化后再等待时间均可在托盘菜单自定义，默认分别为 20.0 秒与 0.8 秒。
- 自动回车采用全局 `Idle / Recording / AwaitingCompletion` 状态机；停止听写后只有原输入框文字确实发生变化，并在最后一次变化后保持不变达到设定时间才会提交。
- 发送回车前会再次检查前台窗口、输入焦点和最终文字指纹。空语音、识别失败、文字未变化、超时或切换目标时均安全取消，也不会保存识别文字。
- Typeless 的 `Thinking`/进度条没有提供可靠的 Windows UI Automation 完成信号，且会早于异步文字插入消失，因此本工具依据目标输入框的文字变化判定补全，而不是读取动画状态。
- Windows 不再收到大疆按键的 `Volume+`，因此系统音量和原生音量浮层均不会被该按键触发；普通音量键保持原行为。
- 自动读取 `MI_00` 当前注册的 WinUSB 接口 GUID，并提供使用 Zadig/libwdi 只切换该子接口的安全配置与恢复说明；`MI_01` 音频与 `MI_06` 电量接口不变。

### 电量与稳定性

- USB 发射器充电时，托盘电池、悬停提示和设备详情显示闪电标识。
- 电量读取改为后台轮询，避免 USB 读取阻塞托盘界面和按键响应。
- 保留蓝牙、USB、双接入和多麦克风支持，托盘图标继续显示所有在线麦克风中的最低电量。

### 验证

- 当前 Windows 11 已验证 `MI_00=WinUSB`、Interrupt-IN `0x82`、`MI_01=usbaudio` 与 `MI_06=WinUSB`。
- 物理连接键、Typeless 连续启停及系统音量浮层拦截已在当前 Windows 11 验证，并完成 2 个“开始—停止—文字补全—自动回车”实机周期。
- 最终源码通过 137 项自动化测试，覆盖按键协议、按键组合、自动回车参数与配置往返、停止前文字快照、补全稳定判定、多设备电量、充电状态和界面模型。
- 最终发布 EXE、完整包和源码包均提供 SHA-256 校验值。

### 驱动边界

WinUSB 绑定的是整个 `MI_00`，该接口下的 Consumer Control、Telephone 和两个厂商自定义 HID collection 都不再交给 Windows；独立的 `MI_01` USB Audio 与 `MI_06` 电量通道不受影响。需要 DJI 官方工具访问这些 `MI_00` HID collection 时，可按完整包中的 `docs/MI00-WINUSB.md` 恢复 Microsoft HidUsb。

当前按键报告只实机验证了 `VID_2CA3&PID_4011` USB 接收器。蓝牙直连按键和其他报告格式尚未宣称支持。`Ctrl+Alt+Del` 等系统安全序列不能由普通应用模拟。

正式包不包含本项目自制的驱动签名脚本。配置说明引导用户使用 Akeo Consulting 签名的 Zadig 程序，只为硬件 ID 精确匹配的 `MI_00` 安装 Windows `WinUSB`；务必不要选择父设备或 `MI_01` 音频接口。Zadig/libwdi 会为该设备即时生成自签名证书并加入本机 Root 与 Trusted Publishers，目录签名后销毁私钥；说明文档同时给出精确证书清理边界。不接受这项系统证书变更时，可只使用电量功能。托盘 EXE 当前没有商业代码签名，首次运行可能出现 Windows SmartScreen 提示。

### 下载

- `DJI-Mic-Battery-Tray-v2.0.0.exe`
- `DJI-Mic-Battery-Tray-v2.0.0.zip`（EXE、托盘 App 的 install/uninstall 脚本与说明文档；不内置 Zadig）
- `DJI-Mic-Battery-Tray-v2.0.0-source.zip`
- `SHA256SUMS.txt`
