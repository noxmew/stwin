# STWin

Windows 桌面分身工具

## 原理

使用 Windows 的 [Child Sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions) 创建额外的桌面会话，通过系统自带的 RDP 控件连接和显示。子会话依附当前登录会话，主账号注销时也会结束。

## 下载

[前往 Releases 下载](https://github.com/noxmew/stwin/releases)

下载 `STWin.exe`，双击运行。

## MCP

启动 STWin 后，在 MCP 客户端中添加以下地址，连接方式选择 Streamable HTTP：

```text
http://127.0.0.1:51374/mcp
```
