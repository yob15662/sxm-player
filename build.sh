#!/bin/sh
# check if mpc is playing and remember state
if mpc status 2>/dev/null | grep -q '\[playing\]'; then
  WAS_PLAYING=1
  mpc stop || true
else
  WAS_PLAYING=0
fi

sudo systemctl stop SiriusXM
git pull

# publish the proxy service
dotnet publish -c Release -o /srv/SiriusXM/ SXMPlayer.Proxy/SXMPlayer.Proxy.csproj

sudo systemctl start SiriusXM

# resume playback if it was playing before
if [ "$WAS_PLAYING" -eq 1 ]; then
  sleep 5
  mpc play || true
fi