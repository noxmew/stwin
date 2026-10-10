# STWin

Windows 桌面分身工具

## 原理

使用 Windows 的 [Child Sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions) 创建额外的桌面会话，通过系统自带的 RDP 控件连接和显示。

## 下载

[下载](https://github.com/noxmew/stwin/releases)

## CLI

```cmd
STWin.exe help
```

在已由 STWin 管理的分身会话中以管理员权限启动程序：

```cmd
STWin.exe launch "C:\Tools\App.exe" --session 2 --elevated --cwd "C:\Tools" --json -- --option "参数"
```

将 `2` 替换为 `STWin.exe status` 返回的分身会话 ID。STWin 主程序需已以管理员身份运行；权限不足时命令返回退出码 `5`。`--` 后的内容作为程序参数原样传递，返回信息中的 `elevated` 表示目标进程的实际提权状态。

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
