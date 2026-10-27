#!/bin/bash
# Creates the "admin" account with a random password shown once. An existing admin is not changed.
# shellcheck source=common.sh
source "$(dirname "$0")/common.sh"
trap on_exit EXIT

require_running_site
password="Bc!$(random_text 'A-Za-z0-9' 32)"
# The password goes through stdin, never through command-line arguments.
printf '%s\n' "$password" | docker compose exec -T web dotnet BeastContest.Web.dll --create-user admin --role Admin --name Administrator \
    || fail 'Администратор не создан. Если admin уже существует, его пароль не изменён (используйте Reset-Admin-Password.command).'
echo
ok 'Администратор создан.'
echo 'Логин:   admin'
echo "Пароль:  $password"
echo "Сайт:    $BEAST_URL/account/login"
echo 'Скопируйте пароль сейчас: он нигде не сохраняется.'
