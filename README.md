# STWin

在 Windows 上打开一个额外的桌面，可以在里面运行程序，也可以通过 MCP 查看画面、操作鼠标键盘和执行 PowerShell 脚本。

## 下载

[下载 Windows x64 版](https://github.com/noxmew/stwin/releases/latest/download/STWin-win-x64.zip) · [所有版本](https://github.com/noxmew/stwin/releases)

解压 `STWin-win-x64.zip`，打开文件夹中的 `STWin.exe`。整个文件夹都要保留，不需要安装 .NET。

需要 Windows x64。目前是开发预览版，桌面连接和多屏兼容性仍需验证。

## 使用

1. 打开 STWin，点击“启动桌面”。如果提示需要管理员权限，点击“以管理员身份重新打开”。
2. 已有分身桌面时，点击“继续连接”。
3. 右上角可以切换全屏；设置菜单中可以调整置顶、缩放、声音和组合键。

关闭程序时，选择“保留分身并退出”，里面的程序会继续运行，下次打开可以重新连接。选择“结束分身并退出”会关闭里面的程序，请先保存文件。Windows 账号注销、电脑重启或关机也会结束分身。

## MCP 连接

STWin 启动后会自动开启 MCP 服务，关闭 STWin 后服务停止。客户端需要支持 **Streamable HTTP**。

通常使用这个地址：

```text
http://127.0.0.1:51374/mcp
```

如果端口被占用，程序会在 `51374–51385` 中选择可用端口。实际地址保存在：

```text
%LocalAppData%\STWin\mcp-connection.json
```

按 `Win + R`，输入上面的文件路径即可查看。把文件里的 `url` 填到 MCP 客户端的服务器地址中；设置菜单里的“复制诊断信息”也包含这个地址。

| 工具 | 用途 |
| --- | --- |
| `get_desktop_status` | 查看连接状态和分辨率 |
| `take_screenshot` | 获取分身截图 |
| `mouse_move`、`mouse_click`、`mouse_scroll` | 移动、点击和滚动鼠标 |
| `key_press` | 发送按键或组合键 |
| `type_text` | 输入文字 |
| `run_script` | 在分身中执行 PowerShell 脚本 |

操作前先在 STWin 中连接桌面。鼠标坐标以最近一次截图为准；`run_script` 提交后返回截图，脚本的输出在分身里的 PowerShell 窗口中查看。

MCP 只监听本机，目前没有鉴权。本机其他能访问该端口的程序也能调用这些工具。分身与主桌面共享文件和用户权限，不是沙箱；脚本可以修改该用户的文件和系统设置。

## 源码构建

需要 Windows x64 和 .NET 10 SDK。在仓库目录运行：

```powershell
dotnet publish .\src\STWin\STWin.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\STWin
```

输出目录为 `artifacts/STWin`。GitHub Actions 会把整个目录打包为 `STWin-win-x64.zip`；推送与项目版本一致的 `v*` 标签时，会发布到 Releases。
