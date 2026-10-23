#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
  echo '请先按 Docker 官方 Ubuntu 文档安装 Docker Engine 与 Compose 插件，再运行本脚本。'
  echo 'https://docs.docker.com/engine/install/ubuntu/'
  exit 1
fi
if [[ ! -f app/Snip.Sync.Server.dll ]]; then
  echo '未找到 app/Snip.Sync.Server.dll；请使用完整的同步服务部署包。'
  exit 1
fi
umask 077
if [[ ! -f .env ]]; then
  sync_invite=$(openssl rand -hex 24)
  printf 'SELF_SNIP_REGISTER_INVITE=%s\nSELF_SNIP_PORT=5080\n' "$sync_invite" > .env
  unset sync_invite
fi
chmod 600 .env
mkdir -p data
chmod 700 data
if [[ ${EUID} -eq 0 ]]; then
  chown -R 1654:1654 data
else
  echo '需要将 data 目录交给容器的非特权用户；sudo 仅用于此目录的属主设置。'
  sudo chown -R 1654:1654 data
fi
docker compose up -d --build
printf '\n同步服务已启动，HTTP 仅监听本机地址。\n'
printf '请把 nginx-location.conf 中的 location 段加入现有 HTTPS 网站的 server 块，检查 nginx -t 后再 reload。\n'
printf '客户端填写：https://你的域名/selfsnip/\n'
printf '首次注册邀请码位于此目录的 .env；请在服务器本机查看，不要分享整个文件或提交到代码仓库。\n'
printf '健康检查：curl -fsS http://127.0.0.1:5080/health\n'
