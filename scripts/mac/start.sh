#!/bin/bash
# Builds and starts PostgreSQL, the site and the Judge Worker, then opens the login page.
# shellcheck source=common.sh
source "$(dirname "$0")/common.sh"
BEAST_FAILURE_HINT="Запуск не удался. Логи: docker compose logs web judge"
trap on_exit EXIT

ensure_env
ensure_docker
echo 'Собираю и запускаю контейнеры. Первая сборка скачивает несколько гигабайт и занимает время...'
docker compose up --build -d || fail 'Docker Compose не смог запустить проект. Проверьте Docker Desktop и что порт 5080 свободен.'

echo 'Жду готовности сайта...'
for _ in $(seq 1 90); do
    if curl -fsS --max-time 10 "$BEAST_URL/health" >/dev/null 2>&1; then
        ok "BeastContest запущен: $BEAST_URL"
        [ "${BEAST_NO_BROWSER:-}" = 1 ] || open "$BEAST_URL/account/login"
        echo 'Нет администратора? Запустите Create-Admin.command.'
        exit 0
    fi
    sleep 2
done
fail 'Сайт не ответил за 3 минуты. Логи: docker compose logs web'
