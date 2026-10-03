#!/usr/bin/env bash
set -Eeuo pipefail

SERVICE_NAME="biz-bizadm-modelrouter"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${EUID}" -ne 0 ]]; then
    echo "오류: root 권한으로 실행하세요. sudo $0" >&2
    exit 1
fi

install -o root -g root -m 0644 "${SCRIPT_DIR}/${SERVICE_NAME}.service" "/etc/systemd/system/${SERVICE_NAME}.service"
systemctl daemon-reload
systemctl enable --now "${SERVICE_NAME}" || {
    systemctl --no-pager --full status "${SERVICE_NAME}" || true
    journalctl -u "${SERVICE_NAME}" -n 50 --no-pager || true
    exit 1
}
