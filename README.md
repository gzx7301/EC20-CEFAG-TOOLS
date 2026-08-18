# EC20-CEFAG-TOOLS

EC20-CEFAG-TOOLS 是一个用于 Windows 的 Quectel EC20 CEFAG 电话 / 短信小工具。

它通过 EC20 的 AT 串口工作，并配合 Windows 中的 `AC Interface` 音频设备完成通话音频。

## 功能

- 自动寻找 Quectel USB AT Port。
- 优先识别真正的 `Quectel USB AT Port`，避免误选 NMEA / DM 端口。
- 开机后台自启动，托盘常驻。
- 单实例运行：重复打开 exe 时唤起已有界面。
- 显示连接状态、搜网状态和信号强度。
- 自动轮询 SIM、网络注册、运营商和信号状态。
- 支持重新搜网。
- 支持发送短信、读取短信、删除短信。
- 本机保存收到和发出的短信记录。
- 支持拨号、接听、挂断。
- 支持来电通知和来电弹窗。
- 保存通话历史。
- AT 信令页面可查看日志、保存日志，并手动发送 AT 指令。

## 使用前准备

1. 安装 EC20 对应的 Windows 驱动。
2. 插入 EC20 模块和 SIM 卡。
3. 在设备管理器中确认存在 `Quectel USB AT Port`。
4. 通话音频建议在 Windows 声音设置中将 `AC Interface` 的麦克风和扬声器设为默认通讯设备。

## 构建

项目使用 .NET Framework 4.8 WinForms。

可以使用 Visual Studio 打开 `EC20-CEFAG-TOOLS.csproj` 构建，也可以运行：

```powershell
.\build.ps1
```

构建后的 exe 会输出到：

```text
bin\Release\EC20电话短信工具.exe
```

## 数据保存位置

短信记录和通话记录默认保存在当前 Windows 用户的本地应用数据目录：

```text
%LOCALAPPDATA%\EC20电话短信工具
```

AT 信令日志点击保存后会保存在程序 exe 所在目录。

## 注意

- 工具依赖 EC20 暴露出的 AT 串口。
- 不同固件、运营商、SIM 卡套餐和网络制式可能影响语音、短信和注册状态。
- 当前来电弹窗主要依靠模块上报的 `RING` 信令触发。
- 如果拔插 EC20 后端口短时间被 Windows 占用，工具会尝试自动释放旧连接并重新寻找端口。
