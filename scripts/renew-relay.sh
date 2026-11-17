#!/usr/bin/env bash
# AgentDeck Relay 续签脚本（Linux / 阿里云）
#
# 做四件事：停掉旧 relay → 把旧配置与旧 routes 改名留底 → 用一年有效期重签
# → 重新拉起 → 最后打印电脑端「配置云服务器」要填的三项。
#
# 用法：
#   ./renew-relay.sh                     沿用旧证书里的地址，签 365 天
#   ./renew-relay.sh 8.159.131.11        指定地址（可写 IP 或域名）
#   ./renew-relay.sh wss://relay.example.com:8443
#   ./renew-relay.sh -d 730 1.2.3.4      指定天数
#   ./renew-relay.sh -y 1.2.3.4          跳过确认
#
# 注意：换证书等于换钥匙，已配对的电脑和手机全部要重配。

set -euo pipefail

DAYS=365
ADDRESS=""
ASSUME_YES=0
UNIT=agentdeck-relay.service

die() { printf '错误：%s\n' "$*" >&2; exit 1; }
note() { printf '  · %s\n' "$*"; }
step() { printf '\n[%s/7] %s\n' "$1" "$2"; }
warn() { printf '警告：%s\n' "$*" >&2; }

usage() {
    awk 'NR > 2 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "$0"
    exit 0
}

# ---------- 参数 ----------
while [ $# -gt 0 ]; do
    case $1 in
    -d | --days)
        [ $# -ge 2 ] || die "$1 需要一个天数"
        DAYS=$2
        shift 2
        ;;
    -y | --yes)
        ASSUME_YES=1
        shift
        ;;
    -h | --help) usage ;;
    -*)
        die "不认识的选项 $1（用 -h 看用法）"
        ;;
    *)
        [ -z "$ADDRESS" ] || die "只接受一个地址参数"
        ADDRESS=$1
        shift
        ;;
    esac
done

case $DAYS in
'' | *[!0-9]*) die "天数必须是整数：$DAYS" ;;
esac
DAYS=$((10#$DAYS)) # 去掉前导零，避免 08 被当成八进制
[ "$DAYS" -ge 1 ] && [ "$DAYS" -le 3650 ] || die "天数必须在 1 到 3650 之间"

# ---------- 工作目录与二进制 ----------
cd "$(dirname "$0")"
DIR=$(pwd)

BIN=""
for candidate in ./agentdeck-relay-linux-amd64 ./agentdeck-relay ./agentdeck-lab ./agentdeck-lab-linux-amd64; do
    if [ -f "$candidate" ]; then
        BIN=$candidate
        break
    fi
done
[ -n "$BIN" ] || die "当前目录 $DIR 里找不到 relay 二进制，先把它传上来（agentdeck-relay-linux-amd64）"
[ -x "$BIN" ] || chmod +x "$BIN" || die "无法给 $BIN 加执行权限"

command -v openssl >/dev/null 2>&1 || die "缺少 openssl，先 apt install openssl"

# 旧版本不认识 -validity，跑下去会在重签那一步失败，提前拦住。
# 注意 -h 的退出码是 1（flag 包的行为），所以用 || true 接住，别被 pipefail 判成失败。
HELP=$("$BIN" relay-init -h 2>&1 || true)
case $HELP in
*-validity*) ;;
*) die "这个二进制是旧版，不认识 -validity。先把新的 agentdeck-relay-linux-amd64 覆盖上来再跑。" ;;
esac

SUDO=""
if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then
    SUDO="sudo"
fi

has_unit() { command -v systemctl >/dev/null 2>&1 && systemctl cat "$UNIT" >/dev/null 2>&1; }

# 进程查找：pidfile 最可靠（二进制名换过也能停掉），再用 pgrep / ps 兜底
relay_pids() {
    local pid
    {
        if [ -f relay.pid ]; then
            pid=$(tr -cd '0-9' <relay.pid)
            if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
                # 只在看起来确实是 relay 时才算数，防止 PID 被别的进程复用
                if ! ps -p "$pid" -o args= >/dev/null 2>&1 ||
                    ps -p "$pid" -o args= 2>/dev/null | grep -q agentdeck; then
                    printf '%s\n' "$pid"
                fi
            fi
        fi
        if command -v pgrep >/dev/null 2>&1; then
            pgrep -f 'agentdeck.*relay -config' 2>/dev/null || true
        else
            ps -eo pid=,args= 2>/dev/null | awk '/agentdeck.*relay -config/ {print $1}' || true
        fi
    } | sort -u | grep -vx "$$" || true
}

# 端口是否还有人在听。ss 优先，没有就 bash 自带 /dev/tcp 探一下
port_busy() {
    if command -v ss >/dev/null 2>&1; then
        ss -ltn 2>/dev/null | awk '{print $4}' | grep -qE "[:.]$PORT\$"
    else
        (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null
    fi
}


# 从旧配置里读证书的 SAN，省得手抄地址
old_san() {
    [ -f relay.local.json ] || return 1
    sed 's/\\n/\n/g' relay.local.json |
        awk '/BEGIN CERTIFICATE/ {sub(/^.*-----BEGIN/, "-----BEGIN"); f = 1} f {print} /END CERTIFICATE/ {if (f) exit}' |
        openssl x509 -noout -ext subjectAltName 2>/dev/null |
        sed -n 's/^ *\(IP Address\|DNS\): *//p' | head -1
}

# ---------- 1. 确定地址 ----------
step 1 "确定 Relay 地址"
if [ -z "$ADDRESS" ]; then
    ADDRESS=$(old_san || true)
    [ -n "$ADDRESS" ] || die "没有旧配置可参考，请把地址写在参数里：./renew-relay.sh 你的公网IP"
    note "沿用旧证书里的地址：$ADDRESS"
fi
ADDRESS=${ADDRESS#wss://}
ADDRESS=${ADDRESS#WSS://}
ADDRESS=${ADDRESS%%/*}
case $ADDRESS in
*:*)
    HOST=${ADDRESS%:*}
    PORT=${ADDRESS##*:}
    ;;
*)
    HOST=$ADDRESS
    PORT=8443
    ;;
esac
case $PORT in
'' | *[!0-9]*) die "端口不是数字：$PORT" ;;
esac
[ -n "$HOST" ] || die "地址里没有主机名"
RELAY_URL="wss://$HOST:$PORT"
note "证书将签给：$HOST，端口 $PORT，有效期 $DAYS 天"

if [ -f relay.local.json ]; then
    OLD_EXPIRY=$(sed 's/\\n/\n/g' relay.local.json |
        awk '/BEGIN CERTIFICATE/ {sub(/^.*-----BEGIN/, "-----BEGIN"); f = 1} f {print} /END CERTIFICATE/ {if (f) exit}' |
        openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2 || true)
    [ -n "${OLD_EXPIRY:-}" ] && note "当前证书到期：$OLD_EXPIRY"
fi

if [ "$ASSUME_YES" -eq 0 ]; then
    printf '\n这会换掉 Relay 证书，已配对的电脑和手机都要重新配置。继续？[y/N] '
    read -r answer || answer=""
    case $answer in
    y | Y | yes | YES) ;;
    *) die "已取消，什么都没改" ;;
    esac
fi

# ---------- 2. 停旧进程 ----------
step 2 "停止旧 Relay"
if has_unit; then
    note "停止 systemd 服务 $UNIT"
    $SUDO systemctl stop "$UNIT"
fi
if [ -n "$(relay_pids)" ]; then
    note "结束残留进程：$(relay_pids | tr '\n' ' ')"
    # shellcheck disable=SC2046
    kill $(relay_pids) 2>/dev/null || true
    for _ in $(seq 1 20); do
        [ -z "$(relay_pids)" ] && break
        sleep 0.5
    done
    if [ -n "$(relay_pids)" ]; then
        warn "进程没响应 TERM，强制结束"
        # shellcheck disable=SC2046
        kill -9 $(relay_pids) 2>/dev/null || true
        sleep 1
    fi
fi
[ -z "$(relay_pids)" ] || die "旧进程还在，先手工处理：ps -ef | grep agentdeck"
rm -f relay.pid
note "旧进程已退出"

# 进程列表没抓到、端口却还在听：多半是手工起的或二进制改了名。这时先报错，
# 别把还能用的配置改名换掉——用户损失的是一份好证书。
for _ in $(seq 1 10); do
    port_busy || break
    sleep 0.5
done
if port_busy; then
    warn "端口 $PORT 仍被占用，说明还有个旧 Relay 活着（本脚本没找到它）"
    command -v ss >/dev/null 2>&1 && ss -ltnp 2>/dev/null | grep -E "[:.]$PORT( |\$)" || true
    die "先手动停掉它再重跑本轮。配置文件一个字都没动。"
fi
note "端口 $PORT 已空闲"

# ---------- 3. 备份 ----------
step 3 "旧文件改名留底"
TS=$(date +%Y%m%d-%H%M%S)
backup() {
    local name=$1 target="$1.$TS.bak"
    [ -e "$name" ] || return 0
    [ -e "$target" ] && target="$target.$RANDOM"
    mv -- "$name" "$target"
    note "$name → $target"
}
backup relay.local.json
backup routes
mkdir -p routes
chmod 700 routes
note "routes 已重建为空目录"

# ---------- 4. 重签 ----------
step 4 "签发 $DAYS 天有效期的证书"
if ! FINGERPRINT=$("$BIN" relay-init -config ./relay.local.json -relay-url "$RELAY_URL" -validity "$DAYS"); then
    die "relay-init 失败，旧的 relay.local.json 已改名为 relay.local.json.$TS.bak，可改回去"
fi
FINGERPRINT=$(printf '%s' "$FINGERPRINT" | tr -d '[:space:]' | tr 'A-F' 'a-f')
case $FINGERPRINT in
*[!0-9a-f]* | '') die "relay-init 返回的指纹不像 64 位十六进制：$FINGERPRINT" ;;
esac
[ "${#FINGERPRINT}" -eq 64 ] || die "指纹长度不对：${#FINGERPRINT}"
note "新指纹 $FINGERPRINT"

# ---------- 5. 注册令牌 ----------
step 5 "准备注册令牌"
TOKEN_STATE="沿用"
TOKEN=""
if [ -s enrollment-token ] && grep -qE '^[0-9a-f]{64}$' enrollment-token; then
    TOKEN=$(tr -d '[:space:]' <enrollment-token)
else
    umask 077
    openssl rand -hex 32 >enrollment-token
    chmod 600 enrollment-token
    TOKEN=$(cat enrollment-token)
    TOKEN_STATE="新生成"
fi
note "注册令牌：$TOKEN_STATE（$TOKEN）"

# ---------- 6. 启动 ----------
step 6 "启动 Relay"
if has_unit; then
    ENVFILE=$(systemctl show -p EnvironmentFiles --value "$UNIT" 2>/dev/null | awk '{print $1}' || true)
    if [ -z "$ENVFILE" ]; then
        warn "单元没有 EnvironmentFile，脚本没法把令牌传给它；确认单元里 Environment=AGENTDECK_ENROLLMENT_TOKEN 与上面一致"
    elif [ ! -e "$ENVFILE" ]; then
        warn "单元声明的 EnvironmentFile $ENVFILE 不存在，服务可能因缺令牌起不来"
    elif ! grep -qx "AGENTDECK_ENROLLMENT_TOKEN=$TOKEN" "$ENVFILE"; then
        note "同步令牌到 $ENVFILE"
        printf 'AGENTDECK_ENROLLMENT_TOKEN=%s\n' "$TOKEN" | $SUDO tee "$ENVFILE" >/dev/null
        $SUDO chmod 600 "$ENVFILE"
    fi
    $SUDO systemctl start "$UNIT"
    sleep 1
    systemctl is-active --quiet "$UNIT" || die "服务没起来：journalctl -u ${UNIT%.service} -n 50 --no-pager（常见原因是令牌没进到单元的环境变量）"
    note "systemd 服务运行中"
else
    AGENTDECK_ENROLLMENT_TOKEN="$TOKEN" nohup "$BIN" relay -config ./relay.local.json \
        -enrollment-dir ./routes -listen "0.0.0.0:$PORT" >>relay.log 2>&1 &
    RELAY_PID=$!
    printf '%s\n' "$RELAY_PID" >relay.pid
    sleep 1
    kill -0 "$RELAY_PID" 2>/dev/null || die "relay 启动后立刻退出，看 $DIR/relay.log"
    note "后台进程 PID $RELAY_PID，日志 relay.log"
fi

# ---------- 7. 校验 ----------
step 7 "核对端口上跑的确实是新证书"
live_fingerprint() {
    echo | openssl s_client -connect "127.0.0.1:$PORT" -servername "$HOST" 2>/dev/null |
        openssl x509 -noout -fingerprint -sha256 2>/dev/null |
        sed 's/^.*=//; s/://g' | tr 'A-F' 'a-f'
}
LIVE=""
for _ in $(seq 1 30); do
    LIVE=$(live_fingerprint || true)
    [ -n "$LIVE" ] && break
    sleep 0.5
done

VERIFIED=1
if [ -z "$LIVE" ]; then
    warn "127.0.0.1:$PORT 上没握手成功，检查监听：ss -ltnp | grep $PORT"
    VERIFIED=0
elif [ "$LIVE" != "$FINGERPRINT" ]; then
    warn "端口上的证书和刚签发的不一致"
    warn "  刚签发：$FINGERPRINT"
    warn "  端口上：$LIVE"
    warn "多半是旧进程没退干净，或被 systemd 自动拉起。查：systemctl status ${UNIT%.service}；ss -ltnp | grep $PORT"
    VERIFIED=0
fi

EXPIRY=$(echo | openssl s_client -connect "127.0.0.1:$PORT" -servername "$HOST" 2>/dev/null |
    openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2 || true)

# ---------- 输出电脑端三项 ----------
printf '\n'
if [ "$VERIFIED" -eq 0 ]; then
    printf '!! 服务器侧未通过校验，先别填这三项，按上面的排查处理 !! \n'
fi
printf '================ 电脑端「配置云服务器」填这三项 ================\n'
printf '  Relay 地址          %s\n' "$RELAY_URL"
printf '  证书 SHA-256 指纹    %s\n' "$FINGERPRINT"
printf '  注册令牌            %s\n' "$TOKEN"
printf '==============================================================\n'
[ -n "${EXPIRY:-}" ] && printf '证书到期：%s\n' "$EXPIRY"

cat >windows-inputs.txt <<EOF
Relay 地址=$RELAY_URL
证书 SHA-256 指纹=$FINGERPRINT
注册令牌=$TOKEN
EOF
chmod 600 windows-inputs.txt
printf '三项已写入 %s/windows-inputs.txt（方便 scp 取走）\n' "$DIR"

cat <<EOF

电脑端还要做：退出托盘 → 删 %LOCALAPPDATA%\\AgentDeck 下的
  agent.local.json、registered-device、pairing\\
→ 重新打开 AgentDeck-Setup.exe，填上面三项。手机重新扫码配对。

留底文件：relay.local.json.$TS.bak、routes.$TS.bak（确认没问题后再删）
EOF

[ "$VERIFIED" -eq 1 ] || exit 2
