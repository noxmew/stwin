# STWin 项目约定

- 推送 `main` 后由 GitHub Actions 自动构建和发布，不手动修改发布版本号或推送发布 tag。
- 发布版本为现有最大 `v0.1.N` tag 的下一个版本；Action 将版本注入程序集，更新检查、诊断和 MCP 使用同一个程序集版本。项目文件中的版本仅用于源码构建。
- Release 只发布单文件 `STWin.exe`，文件名保持固定，供自动更新下载。
- 发布前重新核对 tag；同一次 Action 已发布时停止重试，不覆盖已有 Release 或资产。
