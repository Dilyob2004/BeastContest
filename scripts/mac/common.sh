#!/bin/bash
# Shared helpers for the macOS scripts. Compatible with the bash 3.2 that ships with macOS.
set -euo pipefail

BEAST_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$BEAST_ROOT"
export BEAST_URL="http://localhost:5080"

fail() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }

# Random characters from an alphabet, read from the kernel CSPRNG.
# tr is stopped by SIGPIPE once head has enough; that is expected, not a failure under pipefail.
random_text() { { LC_ALL=C tr -dc "$1" < /dev/urandom || true; } | head -c "$2"; }

require_docker_cli() {
    command -v docker >/dev/null 2>&1 || fail 'Docker не найден. Установите Docker Desktop для Mac (Apple chip) с docker.com.'
}

docker_ready() { docker info --format '{{.ServerVersion}}' >/dev/null 2>&1; }

# Starts Docker Desktop when needed and waits up to two minutes for the engine.
ensure_docker() {
    require_docker_cli
    docker_ready && return 0
    echo 'Запускаю Docker Desktop...'
    open -a Docker >/dev/null 2>&1 || fail 'Не удалось открыть Docker Desktop. Запустите его вручную.'
    for _ in $(seq 1 60); do
        sleep 2
        docker_ready && return 0
    done
    fail 'Docker Desktop не запустился за 2 минуты. Откройте его и дождитесь статуса Running.'
}

# Creates .env with a random database password once; never overwrites an existing file.
ensure_env() {
    if [ ! -f .env ]; then
        (umask 077; printf 'BEAST_DB_PASSWORD=%s\n' "$(random_text 'A-F0-9' 48)" > .env)
        echo 'Создан .env с новым паролем базы данных.'
    fi
    BEAST_DB_PASSWORD="$(sed -n 's/^BEAST_DB_PASSWORD=//p' .env | head -n 1 | tr -d '\r')"
    [ -n "$BEAST_DB_PASSWORD" ] || fail 'В .env нет BEAST_DB_PASSWORD.'
    export BEAST_DB_PASSWORD
}

require_running_site() {
    require_docker_cli
    docker_ready || fail 'Docker Desktop не запущен. Сначала запустите Start-BeastContest.command.'
    [ -n "$(docker compose ps --status running -q web 2>/dev/null)" ] || fail 'Сайт не запущен. Сначала запустите Start-BeastContest.command.'
}

# Exit handler: optional failure hint, then keep a double-clicked window open until Enter.
on_exit() {
    local status=$?
    if [ "$status" -ne 0 ] && [ -n "${BEAST_FAILURE_HINT:-}" ]; then echo "$BEAST_FAILURE_HINT"; fi
    pause_if_interactive
    exit "$status"
}

# Interactive windows opened by double-click stay open until Enter is pressed.
pause_if_interactive() {
    if [ -t 0 ] && [ "${BEAST_NO_PAUSE:-}" != 1 ]; then
        printf '\nНажмите Enter, чтобы закрыть окно.'
        read -r _ || true
    fi
}
