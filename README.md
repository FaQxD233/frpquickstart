# FRP QuickStart

FRP QuickStart 是一个简化 frp 部署的控制层。服务端负责启动 `frps`、签发隧道配置、提供管理界面；客户端负责向服务端申请端口、生成 `frpc` 配置并保持隧道在线。

## 文档

- [快速开始](docs/快速开始.md)
- [服务端安装指南](docs/服务端安装指南.md)
- [客户端安装指南](docs/客户端安装指南.md)
- [Web 管理界面使用指南](docs/管理界面使用指南.md)

## 发布包

GitHub Release 需要包含以下文件：

| 文件 | 用途 |
| --- | --- |
| `frpquick-server-linux-x64.tar.gz` | Linux x64 服务端 |
| `frpquick-client-linux-x64.tar.gz` | Linux x64 客户端 |
| `frpquick-server-win-x64.zip` | Windows x64 服务端 |
| `frpquick-client-win-x64.zip` | Windows x64 客户端 |
| `install-server.sh` | Linux 服务端安装脚本 |
| `install-client.sh` | Linux 客户端安装脚本 |
| `checksums.sha256` | 发布包 SHA256 校验 |

服务端包内置对应平台的 `frps`，客户端包内置对应平台的 `frpc`。目标机器不需要单独安装 frp，也不需要安装 .NET Runtime。

## Linux 服务端快速安装

在 VPS 上执行：

```bash
PUBLIC_IP="$(curl -4fsSL https://ip.sb)"

curl -fsSL https://github.com/FaQxD233/frpquickstart/releases/latest/download/install-server.sh \
  | sudo bash -s -- \
      --tls acme \
      --frp-transport tcp \
      --acme-id "$PUBLIC_IP" \
      --acme-email admin@example.com
```

安装完成后查看日志：

```bash
sudo journalctl -u frpquickstart -n 80 --no-pager
```

日志中会打印 `frpquick://...` 分享链接。客户端可以直接粘贴这条链接导入服务端地址、TLS 模式、frp 传输协议和 API 密钥。

## Windows 客户端快速使用

下载 `frpquick-client-win-x64.zip`，解压后运行：

```powershell
.\frpquick-client.exe `
  --share "frpquick://<服务器IP>:9080/?tls=acme&transport=tcp&secret=<API密钥>" `
  --remote-port 18080 `
  --local-port 8080
```

不传参数直接运行也可以进入交互式配置。第一项可以填写服务端地址，也可以直接粘贴 `frpquick://` 分享链接。

## 常用端口

VPS 防火墙和云安全组至少需要放行：

- `80/tcp`: ACME HTTP-01 校验，仅 `--tls acme` 需要
- `9080/tcp`: 控制端口和 Web 管理界面
- `7000/tcp`: frpc 连接 frps 的默认端口。`tcp/websocket/wss` 放行 TCP，`kcp/quic` 放行 UDP
- 业务端口，例如 `18080/tcp`、`25565/tcp`

## Web 管理界面

服务端启动后访问：

```text
https://<服务器IP>:9080/admin
```

使用 `server-config.json` 中的 `ApiSecret` 登录。管理界面可以查看隧道、清理离线记录、释放端口，也可以强制关闭在线隧道。

注意：当前 frps 没有可靠的单隧道删除 API。强制关闭在线隧道时，FRP QuickStart 会重启托管的 `frps`，所有在线隧道都会断开，客户端保持运行时会自动重连。

## TLS

生产环境建议使用 `--tls acme`。该模式会调用 `acme.sh` 申请 Let's Encrypt short-lived IP 证书，默认 7 天有效期。`acme.sh` 续期后会覆盖 `runtime/acme/fullchain.pem` 和 `runtime/acme/key.pem`，服务端会在新连接握手前自动重新加载证书。

这里的 TLS 默认保护控制接口和 Web 管理界面，也就是 API 密钥、frp token、隧道配置下发等控制面数据。frpc 到 frps 的传输协议由 `--frp-transport` 控制，支持 `tcp`、`websocket`、`wss`、`kcp`、`quic`。服务端会把该协议写入 `frpquick://` 分享链接的 `transport=` 参数，客户端导入后自动使用。

如果需要更像普通 HTTPS/WebSocket 流量，可在服务端使用：

```bash
./frpquick-server --tls acme --frp-transport wss --acme-id "$PUBLIC_IP" --acme-email admin@example.com
```

`wss` 会复用 ACME 证书给 frps 使用，但不要求占用 443 端口；默认仍使用 `FrpsBindPort`，也就是 `7000`。如需换端口，修改 `server-config.json` 的 `FrpsBindPort` 后重启服务，并同步放行对应端口。

可选 TLS 模式：

- `acme`: 推荐。使用公开 CA 证书，客户端按系统信任链验证。
- `self-signed`: 自动生成自签证书，客户端需要配置 SHA256 指纹。
- `none`: 明文 HTTP，只适合本机测试或受信网络。

## 构建发布包

在 Windows 项目根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\release\build-release.ps1
```

脚本会生成四个发布包、两个 Linux 安装脚本副本和 `checksums.sha256`。

上传 GitHub Release 示例：

```powershell
gh auth login
git tag v1.2
git push origin v1.2

gh release create v1.2 `
  .\release\frpquick-server-linux-x64.tar.gz `
  .\release\frpquick-client-linux-x64.tar.gz `
  .\release\frpquick-server-win-x64.zip `
  .\release\frpquick-client-win-x64.zip `
  .\release\install-server.sh `
  .\release\install-client.sh `
  .\release\checksums.sha256 `
  --title "v1.2" `
  --notes "FRP QuickStart release"
```
