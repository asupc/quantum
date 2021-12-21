kill -9 $( ps -e|grep Quantum |awk '{print $1}') 
git fetch --all
git reset --hard origin/master 
git pull origin master 
chmod 777 Quantum && nohup ./Quantum &