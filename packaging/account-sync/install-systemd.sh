#!/usr/bin/env bash
# Native x64 deployment: no Docker image pull or .NET runtime installation required.
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
if [[ ${EUID} -ne 0 ]]; then
  echo '请在部署包目录运行 sudo bash install-systemd.sh。'; exit 1
fi
if [[ $(uname -m) != x86_64 || ! -f linux-x64/Snip.Sync.Server ]]; then
  echo '需要 x86_64 Linux 和完整的 linux-x64/ 部署文件。'; exit 1
fi
for sync_tool in systemctl curl; do
  if ! command -v "$sync_tool" >/dev/null; then echo "缺少命令：$sync_tool"; exit 1; fi
done
umask 077
sync_root=/opt/selfsnip-sync
sync_data=/var/lib/selfsnip-sync
sync_env=/etc/selfsnip-sync.env
if ! id selfsnip-sync >/dev/null 2>&1; then
  useradd --system --user-group --home-dir "$sync_data" --no-create-home --shell /usr/sbin/nologin selfsnip-sync
fi
install -d -m 755 "$sync_root" "$sync_root/releases"
install -d -o selfsnip-sync -g selfsnip-sync -m 700 "$sync_data"
if [[ ! -f $sync_env ]]; then
  printf 'ASPNETCORE_URLS=http://127.0.0.1:5080\nSELF_SNIP_DATA=%s\nSELF_SNIP_ALLOW_REGISTRATION=true\n' "$sync_data" > "$sync_env"
fi
chmod 600 "$sync_env"
sync_release=$(mktemp -d "$sync_root/releases/release-XXXXXXXX")
cp -a linux-x64/. "$sync_release/"
chmod -R a+rX "$sync_release"
chmod 755 "$sync_release" "$sync_release/Snip.Sync.Server"
sync_previous=$(readlink "$sync_root/current" || true)
ln -sfn "$sync_release" "$sync_root/current"
install -m 644 selfsnip-sync.service /etc/systemd/system/selfsnip-sync.service
systemctl daemon-reload
systemctl enable selfsnip-sync.service
sync_ready=false
if systemctl restart selfsnip-sync.service; then
  for sync_attempt in {1..30}; do
    if curl --max-time 2 -fsS http://127.0.0.1:5080/health >/dev/null; then sync_ready=true; break; fi
    sleep 0.3
  done
fi
if [[ $sync_ready != true ]]; then
  if [[ -n $sync_previous ]]; then
    ln -sfn "$sync_previous" "$sync_root/current"
    systemctl restart selfsnip-sync.service || true
  else
    systemctl stop selfsnip-sync.service || true
  fi
  echo '服务健康检查失败，已回退应用版本；配置与数据保留。请在服务器查看 systemctl status selfsnip-sync。'; exit 1
fi
printf '\n同步服务已启动，只监听 127.0.0.1:5080。\n'
printf '下一步：把 nginx-location.conf 加入已有 HTTPS 网站，nginx -t 成功后再 reload。\n'
printf '客户端地址：https://你的域名/selfsnip/\n'
printf '新账号可直接注册；注册开关可在 /etc/selfsnip-sync.env 设置。\n'
printf '更新仍可运行本脚本；保留账号数据、设置及之前的应用版本。\n'
