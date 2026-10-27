#!/bin/bash
# Stops the containers. Users, problems and contests stay in the Docker volumes.
# shellcheck source=common.sh
source "$(dirname "$0")/common.sh"
trap on_exit EXIT

require_docker_cli
docker_ready || { echo 'Docker Desktop не запущен, останавливать нечего.'; exit 0; }
docker compose down || fail 'Не удалось остановить контейнеры.'
ok 'BeastContest остановлен. Данные сохранены (не используйте docker compose down -v).'
