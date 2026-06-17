# FRP QuickStart

> 重要：生产环境请优先使用 `--tls acme`。它会通过 `acme.sh` 申请 Let's Encrypt 7 天 shortlived IP 证书；客户端可以直接粘贴服务端打印的 `frpquick://` 分享链接完成服务器、TLS 和密钥配置。
>
> 管理和排障请先看：[快速开始](docs/快速开始.md) / [服务端安装指南](docs/服务端安装指南.md) / [客户端安装指南](docs/客户端安装指南.md) / [Web 管理界面使用指南](docs/管理界面使用指南.md)。Web 管理界面支持清理离线隧道记录、释放被旧记录占用的端口，也可以强制关闭在线隧道。

## 文档导航

- [快速开始](docs/快速开始.md): 最短路径完成服务端部署和 Windows 客户端连接。
- [服务端安装指南](docs/服务端安装指南.md): systemd、自启动、ACME shortlived IP 证书、防火墙和升级说明。
- [客户端安装指南](docs/客户端安装指南.md): 交互式输入、命令行参数、分享链接、开机自启和故障排查。
- [Web 管理界面使用指南](docs/管理界面使用指南.md): `/admin` 页面、管理 API 和 API 密钥认证。

## 关键特性

- 内置 frp `v0.69.1`，发布包里已经包含 `frpc`/`frps`，目标机器不需要单独下载 frp。
- 服务端支持 `acme`、`self-signed`、`none` 三种控制面 TLS 模式。
- `acme` 模式会调用 `acme.sh` 获取 Let's Encrypt shortlived IP 证书，默认 `--cert-profile shortlived --days 7 --keylength 2048`。
- ACME 证书安装到 `runtime/acme/fullchain.pem` 和 `runtime/acme/key.pem`，续期覆盖文件后服务端会在新连接握手前自动重新加载。
- 服务端会打印 `frpquick://` 分享链接，客户端粘贴后自动导入服务器地址、控制端口、TLS 模式、API 密钥和自签证书指纹。
- 客户端仍保留手动交互式配置，不传参数时会逐项提示输入。
- Web 管理界面和管理 API 需要 `ApiSecret` 认证。
- Web 管理界面支持清理离线记录、释放端口和强制关闭在线隧道；强制关闭会重启本程序托管的 `frps`，所有在线隧道都会断开并等待客户端重连。
- 客户端重复申请指定端口时，服务端会先检查旧记录对应端口是否已经实际释放，避免离线老隧道残留导致端口 409 冲突。
- 客户端 TLS 验证失败时不会自动降级到 HTTP。

## 最短用法

服务端：

```bash
curl https://get.acme.sh | sh -s email=admin@example.com
./frpquick-server --tls acme --acme-id <你的公网IP> --acme-email admin@example.com
```

记录服务端打印的 `frpquick://...` 分享链接。

客户端：

```powershell
.\frpquick-client.exe --share "frpquick://<服务器IP>:9080/?tls=acme&secret=<API密钥>" --remote-port 25565 --local-port 25565
```

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

服务端启动后还会打印一条客户端分享链接：

```text
frpquick://<服务器公网IP>:9080/?tls=acme&secret=<API密钥>
```

如果 `PublicAddress` 为空，服务端会尝试自动查询公网 IPv4 来生成链接。分享链接包含 API 密钥和 TLS 配置，只能发给可信用户。

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

按提示输入。第一项可以输入服务器 IP/域名，也可以直接粘贴服务端打印的 `frpquick://` 分享链接：

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

使用分享链接：

```powershell
.\frpquick-client.exe --share "frpquick://1.2.3.4:9080/?tls=acme&secret=<密钥>" --remote-port 25565 --local-port 25565
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

常用管理操作：

- 点击“清理离线记录”会删除已经不再占用公网端口的历史隧道记录，并释放服务端内存里的端口占用。
- 离线隧道行可以点击“释放端口”，用于手动清掉单个旧记录。
- 在线隧道行可以点击“强制关闭”。当前 frps 没有可靠的单隧道删除 API，因此该操作会重启由 `frpquick-server` 托管的 `frps`，所有在线隧道都会断开，客户端需要保持运行以便自动重连。

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
