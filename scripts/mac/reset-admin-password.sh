#!/bin/bash
# Sets a new random password for the existing "admin" account and shows it once.
# shellcheck source=common.sh
source "$(dirname "$0")/common.sh"
trap on_exit EXIT

require_running_site
password="Bc!$(random_text 'A-Za-z0-9' 32)"
printf '%s\n' "$password" | docker compose exec -T web dotnet BeastContest.Web.dll --reset-admin admin \
    || fail 'Пароль не сброшен: аккаунт admin должен существовать.'
echo
ok 'Пароль администратора сброшен.'
echo 'Логин:   admin'
echo "Пароль:  $password"
echo "Сайт:    $BEAST_URL/account/login"
echo 'Скопируйте пароль сейчас: он нигде не сохраняется.'
