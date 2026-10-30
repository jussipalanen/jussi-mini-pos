#!/bin/sh
set -eu
umask 077

mkdir -p /https
# Retain the local certificate across container rebuilds; renew it if it has
# less than a day left. No host certificate store or private key is imported.
if [ ! -s /https/localhost.key ] || [ ! -s /https/localhost.crt ] \
    || ! openssl x509 -checkend 86400 -noout -in /https/localhost.crt > /dev/null 2>&1; then
    openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
        -keyout /https/localhost.key -out /https/localhost.crt \
        -subj '/CN=localhost' \
        -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' \
        -addext 'extendedKeyUsage=serverAuth'
fi

exec dotnet JussiMiniPos.Api.dll
