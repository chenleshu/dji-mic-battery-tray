# 大疆麦克风电量

在 Windows 通知区域实时显示 DJI Mic Mini 发射器电量的轻量托盘程序。

完整的安装、使用、升级、故障排查和技术边界请参阅：[软件说明](docs/软件说明.md)。

## 功能

- Windows 通知区域电池图标，颜色和填充量跟随电量档位
- 悬停显示 TX1/TX2 估算百分比与充电状态
- 正常电量使用绿色；估算低于 10% 使用橙色；5% 使用红色
- 右键菜单支持立即刷新、开机自动启动和退出
- 每 8 秒读取一次 DJI 接收器状态
- 保持 USB Audio 接口原驱动，不影响麦克风录音

DJI USB 协议返回的是 1–7 档电量状态，不是精确百分比。悬停显示的百分比会明确标注“约”，采用以下粗略映射：

| DJI 档位 | 悬停显示 | 图标颜色 |
| --- | --- | --- |
| 1 | 约 100% | 绿色 |
| 2 | 约 80% | 绿色 |
| 3 | 约 60% | 绿色 |
| 4 | 约 40% | 绿色 |
| 5 | 约 20% | 绿色 |
| 6 | 约 9% | 橙色 |
| 7 | 约 5% | 红色 |

这些百分比只用于粗略视觉判断，不代表 DJI 提供的精确剩余电量。

## 系统要求

- Windows 10/11 x64
- DJI Mic Mini 接收器 USB 数据接口（Interface 6）使用 WinUSB
- 音频接口保持原有 Windows USB Audio 驱动

## 使用发行版

从仓库 Releases 下载 `大疆麦克风电量.exe`，双击即可运行。首次启动后可在托盘右键菜单中启用开机自启。

## 从源码构建

不需要额外安装 .NET SDK，构建脚本使用 Windows 自带的 .NET Framework C# 编译器：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\tests\model.test.ps1
```

输出文件位于 `dist\大疆麦克风电量.exe`。

安装到当前用户并启用开机自启：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

卸载：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

## 实现说明

程序只读访问 `VID_2CA3&PID_4011` 的 Interface 6，通过 WinUSB 的 bulk-IN `0x86` 读取状态帧。协议识别参考了开源项目 [ShadowBitBasher/DJI-Mic-Control](https://github.com/ShadowBitBasher/DJI-Mic-Control)。

## 许可证

[The Unlicense](LICENSE)
