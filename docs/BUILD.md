# 构建与测试

需要 Windows x64、.NET 8 SDK、Node.js 20+ 和 PowerShell 7.4+。Npcap 是游戏网络观测的可选依赖，自动化测试不要求安装。

```powershell
npm ci
dotnet run --project src/MabiSync.App -c Release
```

在源码启动的界面中关闭“操作游戏”可进行模拟；测试探针自动使用模拟输入，不操作桌面游戏。

## 回归

```powershell
pwsh -File tools/Test-Quick.ps1
```

此入口包含原生核心、协议解析、游戏输入边界、连接生命周期、真实 WebSocket 房间，以及 WPF 控件联机测试。需要单独检查服务时可运行 `npm test`。

## 生成便携包

准备 Windows x64 的 `node.exe` 和官方 `cloudflared.exe`。构建会把它们与自包含 .NET 运行库放入 App，首次运行无需下载组件。许可文件在构建时从对应官方版本取得。

```powershell
pwsh -File tools/Build-Release.ps1 -CloudflaredPath C:/Tools/cloudflared.exe
```

默认先运行回归，输出到 `outputs/releases/`。包体包含文件哈希清单与 ZIP 校验文件，不包含配置、原始抓包、调试符号或测试数据。保留简体、繁体和英语运行库资源；其他系统语言使用英语回退。

```powershell
pwsh -File tools/Test-Preview.ps1 -Directory <解压目录>
```

该检查从其他工作目录启动根程序，验证捆绑运行时、乐器文件、文件完整性和本地房间服务，不发送游戏输入。

## 房间服务

```powershell
npm start
```

默认监听 `127.0.0.1:3132`。需要局域网访问时设置 `MABISYNC_HOST=0.0.0.0`，端口由 `MABISYNC_PORT` 控制。公网服务应使用 TLS 反向代理。房间码用于加入，成员令牌用于恢复；请勿将邀请或令牌写入公开日志。

## 协议适配

`adapters/` 保存台服协议编号和结构描述，作为 Core 的内嵌资源；不包含游戏客户端代码、原始网络记录或完整乐谱。客户端更新后应重新验证字段和演奏语义。服务和客户端通过产品标识、协议版本、功能标识和启动代次核对兼容性。
