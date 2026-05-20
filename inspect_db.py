import sqlite3, os
p = r"g:\MyCode\Code\Windows Linker\symlink-manager.db"
conn = sqlite3.connect(p)
cur = conn.cursor()
cur.execute('SELECT Id, Path, IsExcluded, AddedTime FROM ScanDirectories')
rows = cur.fetchall()
print('DB', p)
print('count', len(rows))
for r in rows:
    print(r)
conn.close()
