# STWin

Windows 桌面分身工具

## 原理

使用 Windows 的 [Child Sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions) 创建额外的桌面会话，通过系统自带的 RDP 控件连接和显示。

## 下载

[下载](https://github.com/noxmew/stwin/releases)

## CLI

PowerShell：

```powershell
.\STWin.exe help | Out-Host
.\STWin.exe start | Out-Host
.\STWin.exe status --json | ConvertFrom-Json
.\STWin.exe screenshot desktop.png | Out-Host
.\STWin.exe stop --yes | Out-Host
.\STWin.exe quit | Out-Host
```

支持连接、断开、窗口和设置控制，以及鼠标、键盘、文本和脚本操作；完整命令见 `help`。`disconnect` 和 `quit` 保留分身中的程序，`stop --yes` 结束分身。

## MCP

```json
{
  "mcpServers": {
    "stwin": {
      "type": "http",
      "url": "http://127.0.0.1:51374/mcp"
    }
  }
}
```
