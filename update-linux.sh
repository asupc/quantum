kill -9 $( ps -e|grep Quantum |awk '{print $1}') 
git reset --hard HEAD
git pull
chmod 777 Quantum && nohup ./Quantum &