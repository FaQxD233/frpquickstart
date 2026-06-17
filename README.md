# FRP QuickStart

这是一个简化 frp 使用流程的命令行工具：

- `frpquick-server`: 跑在 Ubuntu 服务器上，启动/管理 `frps`，接收 Windows 客户端发来的穿透请求。
- `frpquick-client`: 跑在 Windows 朋友电脑上，按提示输入服务器和本地服务信息，自动生成 `frpc` 配置并启动 `frpc`。

## 内置 frp

发布产物默认内置 frp `v0.69.1`：

- Windows 客户端内置 `frpc.exe`
- 服务端内置 Linux x64 `frps`，并额外内置 Windows x64 `frps.exe` 方便本机调试

首次运行时程序会把对应二进制释放到 `runtime/frp/`，用户不需要单独下载 frp。高级用法仍可通过 `--frpc <路径>` 或 `server-config.json` 里的 `FrpsPath` 指定自己的 frp 二进制。

## 需要准备

Ubuntu 防火墙/云安全组放行：
   - 控制服务端口，默认 `9080/tcp`
   - frps 端口，默认 `7000/tcp`
   - 你要暴露给外网访问的公网端口，例如 `25565/tcp`

## Ubuntu 端

首次运行：

```bash
chmod +x frpquick-server
./frpquick-server
```

程序会生成 `server-config.json`，启动 `frps`，并打印 `API 密钥`。把这个密钥发给 Windows 使用者。

建议编辑 `server-config.json` 设置服务器公网 IP 或域名：

```json
{
  "PublicAddress": "你的服务器公网IP或域名"
}
```

配置项较多，直接改对应字段即可。常用字段：

- `ControlPort`: Windows 客户端连接的控制端口，默认 `9080`
- `FrpsBindPort`: frpc 连接 frps 的端口，默认 `7000`
- `AllowedRemotePortStart` / `AllowedRemotePortEnd`: 允许客户端申请的公网端口范围
- `ApiSecret`: Windows 客户端请求控制服务的密钥
- `FrpAuthToken`: frpc/frps 的认证 token

## Windows 端

直接运行：

```powershell
.\frpquick-client.exe
```

按提示输入：

- Ubuntu 控制服务 IP/域名
- Ubuntu 控制服务端口，默认 `9080`
- 要开放在服务器上的公网端口
- 本地监听 IP，默认 `127.0.0.1`
- 本地监听端口
- Ubuntu 端打印的连接密钥

运行后保持窗口打开，穿透就会保持在线。

也可以一次性传参：

```powershell
.\frpquick-client.exe --server 1.2.3.4 --control-port 9080 --remote-port 25565 --local-ip 127.0.0.1 --local-port 25565 --secret <密钥>
```

## 发布

Windows x64：

```powershell
.\release\publish-win-x64.ps1
```

Linux x64：

```powershell
.\release\publish-linux-x64.ps1
```

这两个脚本会生成自包含产物，目标机器不需要安装 .NET Runtime。首次从 Windows 交叉发布 Linux x64 时，`dotnet publish` 可能需要联网下载 Linux runtime pack。

也可以在 Ubuntu 上运行：

```bash
./release/publish-linux-on-ubuntu.sh
```

发布产物会输出到 `artifacts` 目录。

当前默认产物：

- Windows: `artifacts/win-x64-self-contained/client/frpquick-client.exe`
- Ubuntu Linux: `artifacts/linux-x64-self-contained/server/frpquick-server`

这两个可执行文件都已经包含对应 frp 组件，分发时不需要额外附带 `frpc.exe` 或 `frps`。

## 注意

默认控制服务是 HTTP，密钥会经过网络传输。生产环境建议只给可信 IP 放行控制端口，或在前面加 HTTPS 反向代理。
