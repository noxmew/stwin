# STWin

Windows 桌面分身工具

## 原理

使用 Windows 的 [Child Sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions) 创建额外的桌面会话，通过系统自带的 RDP 控件连接和显示。

## 下载

[下载](https://github.com/noxmew/stwin/releases)

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
