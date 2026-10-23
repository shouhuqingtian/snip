# 自用截图：Ubuntu 私有账号同步服务

这是自建账号服务，不是登录阿里云账号。部署后，在两台电脑的“账号与同步”中填写同一个 HTTPS 服务地址和自用截图账号。

## 部署：Ubuntu x64 优先使用 systemd

同步服务适合小规模自用；2 核、2 GiB 的 Ubuntu x64 服务器可以使用。本包带 Linux x64 自包含程序，运行时无需下载大模型、Docker 镜像或另装 .NET。Ubuntu 需有常规系统库（OpenSSL、ICU 等），以及 `curl`、`openssl`；这不表示已经验证了你的实际服务器。

1. 下载 `self-snip-sync-server-2.0.61.zip`，上传到 Ubuntu 并解压。保留完整目录，尤其是 `linux-x64/`、`install-systemd.sh`、`selfsnip-sync.service` 和 `nginx-location.conf`。
2. 进入解压目录，运行 `sudo bash install-systemd.sh`。脚本创建专用非登录系统用户，安装到 `/opt/selfsnip-sync/`，数据存放 `/var/lib/selfsnip-sync/`。生成的邀请码保存在仅 root 可读的 `/etc/selfsnip-sync.env`，不会在安装输出显示。
3. 执行 `curl -fsS http://127.0.0.1:5080/health`，应得到 `{"status":"ok","version":1}`。服务只监听本机，不需要开放公网 5080 或数据库端口。
4. 将 `nginx-location.conf` 的 location 段加入已有域名的 **HTTPS** `server { ... }` 块。先备份原站点配置，保留证书和其他 location，执行 `sudo nginx -t` 成功后再 `sudo systemctl reload nginx`。脚本不会修改现有网站、防火墙或阿里云安全组。
5. 浏览器打开 `https://你的域名/selfsnip/health` 检查结果。客户端填写 `https://你的域名/selfsnip/`。不要填写阿里云控制台网址、公网 IP 的 HTTP 地址或 SSH 地址。
6. 首次注册时，在服务器本机查看 `/etc/selfsnip-sync.env` 的 `SELF_SNIP_REGISTER_INVITE`，只将它填入客户端，不要分享环境文件。

如果已有宝塔/Nginx 网站，在对应域名的 HTTPS 配置中加入该 location；不要覆盖整份站点文件。如果尚无 HTTPS 网站，需要先配置域名解析和证书。脚本对当前服务设置 256 MiB 内存上限和半核 CPU 配额，不影响其他应用配额。

查看状态：`sudo systemctl status selfsnip-sync`；停止服务：`sudo systemctl stop selfsnip-sync`。更新前备份 `/etc/selfsnip-sync.env` 和 `/var/lib/selfsnip-sync/`，在新包目录重新运行安装脚本，会保留账号、邀请码和历史应用目录。新应用启动/健康检查失败时脚本回退先前应用链接；首次安装失败会停止本服务，数据不删除。旧版本应用目录可由管理员在确认稳定后自行清理。

## 可选：Docker 部署

若已经在用 Docker Engine 和 Compose，也可以使用原来的 `sudo bash setup.sh`。它使用 `app/` 的托管程序和官方 ASP.NET 8 镜像，配置为当前部署目录 `.env`，数据为 `data/`，容器仍只映射 `127.0.0.1:5080`。使用同一份 Nginx location。**systemd 和 Docker 任选一种，不要同时启用**，它们的配置、数据位置不同。

若 5080 已占用，应先确认原服务归属；不要终止其他服务。Docker 可修改 `.env` 的 `SELF_SNIP_PORT`，systemd 可修改 `/etc/selfsnip-sync.env` 的监听地址，同时调整 Nginx proxy_pass。systemd 安装脚本的健康检查使用默认 5080，改端口后应由管理员手动更新服务与健康检查。

Docker 官方镜像能否下载取决于服务器网络；脚本不添加第三方镜像源。选择 systemd 自包含程序可避免服务器拉取镜像的步骤。

## 两台电脑如何使用

1. 工作电脑升级对应版本，主窗口打开“账号与同步”，服务地址填写 `https://你的域名/selfsnip/`。
2. 勾选“首次使用：注册新账号”，输入 3–64 位英文/数字账号、至少 12 位密码，再确认密码。邀请码从 systemd 的 `/etc/selfsnip-sync.env`（Docker 为部署目录 `.env`）的 `SELF_SNIP_REGISTER_INVITE` 读取，填到客户端的“注册邀请码”。它不是模型密钥。
3. 点击“注册并登录”。云端为空时，会将本机服务配置加密上传。
4. 家里电脑输入同一个服务地址、账号和密码，直接点击“登录并同步”，无需再次注册。
5. 默认约每 30 秒检查变化，也可“立即同步”。本机和云端同时有修改时自动暂停，选择“使用云端配置”或“上传本机配置”。已有本机配置的设备首次登录也会提示选择方向。
6. 导入云端配置前，请关闭在线设置、AI 语音设置、通用设置窗口，以免覆盖未保存的编辑。退出同步账号只退出此设备，不删除本机模型密钥。

## 范围与数据保护

同步仅包含翻译/在线 OCR/AI 语音的服务地址、模型、音色、密钥及默认方式。截图、识别文字、翻译历史、诊断包、快捷键和模型文件不参与同步。

原始账号密码仅在客户端派生密钥，不会发送或保存。PBKDF2-SHA256（600,000 次）与 HMAC 的不同用途标签分别派生账号认证凭据、配置加密凭据；服务器保存认证凭据的 ASP.NET Identity 哈希。配置包采用独立随机盐/nonce 的 AES-256-GCM 认证加密。客户端会话与配置解密凭据由当前 Windows 用户的 DPAPI 保护；模型密钥导入后同样按本机用户重新加密。

密码忘记后，服务端无法直接恢复云端配置。仍保留本机配置的电脑可注册一个新账号并重新上传。第一版不提供忘记密码找回或密码修改接口，请妥善保存密码。共享邀请码只用于受邀注册，部署后可更换它，既有账号不受影响。

会话有效期 30 天，每账号最多 8 个设备会话；登录失败 5 次暂停该账号登录 15 分钟，并有请求限流。配置上传采用版本比较，拒绝过期版本。服务不记录请求体、认证头、模型密钥或原始密码；请保持 Nginx 使用常规日志格式，不额外记录请求体或 Authorization。

服务设计为单实例、私有小规模部署，最多 100 个受邀账号。数据使用原子文件替换与进程独占锁，不支持多个副本共享目录。`data/` 同时保存账号哈希和加密配置包，请定期备份；备份并不替代客户端密码。

## 验证范围

构建版本与提交见 `server-build-info.json`。发布时以实际 Linux 自包含程序和 Windows 托管程序运行真实 HTTP 注册、双设备登录、版本冲突、退出、重启持久化和限流检查；Windows 客户端另验证 DPAPI 重加密、配置不丢失、加密中断恢复和设置窗口保护。测试使用公开合成凭据，不接入付费模型。

这些检查没有连接你的实际阿里云实例，也没有替你安装系统服务或检查域名证书。实际部署仍需在服务器执行脚本并完成上述 HTTPS 健康检查。
