#!/bin/sh
path=`pwd`
sudo rm /etc/systemd/system/SiriusXM.service
sudo ln -s $path/SiriusXM.service /etc/systemd/system
sudo systemctl daemon-reload
