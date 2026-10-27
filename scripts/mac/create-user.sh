#!/bin/bash
# Creates a participant with a random 6-character password and saves the login details
# to credentials/participant-LOGIN-ID.txt (the folder is excluded from Git and Docker).
# Usage: create-user.sh [login] [display name]
# shellcheck source=common.sh
source "$(dirname "$0")/common.sh"
trap on_exit EXIT

login="${1:-}"
name="${2:-}"
if [ -z "$login" ]; then
    printf 'Логин участника (Enter = student01): '
    read -r login || true
    [ -n "$login" ] || login='student01'
fi
printf '%s' "$login" | grep -Eq '^[A-Za-z0-9_.-]{3,40}$' || fail 'Логин: 3–40 латинских букв, цифр, точек, дефисов или подчёркиваний.'
[ -n "$name" ] || name="$login"
[ "${#name}" -le 80 ] || fail 'Имя — не длиннее 80 символов.'
require_running_site

# Unambiguous characters; at least three distinct ones, as the server requires.
while :; do
    password="$(random_text 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789' 6)"
    [ "$(printf '%s' "$password" | fold -w1 | sort -u | wc -l | tr -d ' ')" -ge 3 ] && break
done

mkdir -p credentials
file="credentials/participant-$login-$(uuidgen | tr -d '-' | tr '[:upper:]' '[:lower:]').txt"
printf '%s\n' "$password" | docker compose exec -T web dotnet BeastContest.Web.dll --create-user "$login" --role Participant --name "$name" \
    || fail 'Аккаунт не создан. Проверьте, что сайт запущен и логин не занят. Существующие аккаунты не изменены.'
(umask 077; set -o noclobber; printf 'BeastContest — участник\nИмя: %s\nЛогин: %s\nПароль: %s\nСайт: %s/account/login\n\nАдрес работает на компьютере сервера. LAN пока не настроена.\nХраните файл в тайне и передайте только этому участнику.\n' \
    "$name" "$login" "$password" "$BEAST_URL" > "$file") \
    || fail "Аккаунт создан, но файл не сохранён. Логин: $login Пароль: $password"
ok "Участник создан: $login"
echo "Данные для входа: $BEAST_ROOT/$file"
