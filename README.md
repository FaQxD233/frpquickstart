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
   - 如果使用 `--tls acme`，还需要公网 `80/tcp` 可访问，供 Let's Encrypt HTTP-01 校验使用

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

生产环境建议启用 TLS。可选模式：

- `acme`: 调用 `acme.sh` 自动申请 Let's Encrypt shortlived IP 证书，客户端自动信任。
- `self-signed`: 自动生成自签证书，客户端必须配置证书 SHA256 指纹。
- `none`: 明文 HTTP，仅限本地测试或受信网络。

ACME 模式示例：

```bash
# 前提：已安装 acme.sh，且公网 IP 的 80/tcp 可访问
./frpquick-server --tls acme --acme-id <你的公网IP> --acme-email admin@example.com
```

ACME 模式默认会申请 7 天有效期的 Let's Encrypt shortlived IP 证书，并安装到 `runtime/acme/fullchain.pem` 和 `runtime/acme/key.pem`。`acme.sh` 续期覆盖证书文件后，服务端会在新连接握手前自动重新加载。

配置项较多，直接改对应字段即可。常用字段：

- `ControlPort`: Windows 客户端连接的控制端口，默认 `9080`
- `FrpsBindPort`: frpc 连接 frps 的端口，默认 `7000`
- `AllowedRemotePortStart` / `AllowedRemotePortEnd`: 允许客户端申请的公网端口范围
- `ApiSecret`: Windows 客户端请求控制服务的密钥
- `FrpAuthToken`: frpc/frps 的认证 token
- `TlsMode`: `none` / `self-signed` / `acme`
- `AcmeIdentifier`: ACME 标识符，IP 证书填写公网 IP
- `AcmeRenewDays`: acme.sh 续期阈值，默认 `7`
- `AcmeProfile`: 默认 `shortlived`
- `AcmeKeyLength`: 默认 `2048`

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
.\frpquick-client.exe --server 1.2.3.4 --control-port 9080 --tls acme --remote-port 25565 --local-ip 127.0.0.1 --local-port 25565 --secret <密钥>
```

如果服务端是 `self-signed` 模式，需要额外传入 `--tls-fingerprint <证书指纹>`。客户端在 TLS 验证失败时不会自动降级到 HTTP。

## 管理界面

服务端内置 Web 管理界面：

```text
https://<服务器IP>:9080/admin
```

管理界面和管理 API 需要 API 密钥认证。浏览器访问 `/admin` 时输入 `server-config.json` 中的 `ApiSecret` 即可。直接调用 API 时使用：

```bash
curl -H "X-Api-Secret: <API密钥>" https://<服务器IP>:9080/api/stats
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

发布产物会输出到 `artifacts` 目录。当前 `release/` 目录也保留了可直接分发的压缩包：

- `release/frpquick-server-linux-x64.tar.gz`
- `release/frpquick-client-win-x64.tar.gz`

当前默认产物：

- Windows: `artifacts/win-x64-self-contained/client/frpquick-client.exe`
- Ubuntu Linux: `artifacts/linux-x64-self-contained/server/frpquick-server`

这两个可执行文件都已经包含对应 frp 组件，分发时不需要额外附带 `frpc.exe` 或 `frps`。

## 注意

生产环境必须使用 `--tls acme` 或 `--tls self-signed`。`none` 模式会用明文 HTTP 传输 API 密钥，只适合本机测试或受信网络。
