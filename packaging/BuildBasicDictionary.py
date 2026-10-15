"""Reproduce the bundled ECDICT subset; not used at application startup/build."""
import argparse,csv,hashlib,json,re,urllib.request
from pathlib import Path

REVISION='bc015ed2e24a7abef49fc6dbbb7fe32c1dadaf8b'
SHA256='1a6947e04785db63613a92e14903cdae7954f7e84860b10e68e5c7cbb3f9c3cf'
parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path);args=parser.parse_args()
source=args.source
if source is None:
    source=Path('/tmp/snip-ecdict.csv')
    urllib.request.urlretrieve(f'https://raw.githubusercontent.com/skywind3000/ECDICT/{REVISION}/ecdict.csv',source)
if hashlib.sha256(source.read_bytes()).hexdigest()!=SHA256:raise SystemExit('Source checksum mismatch')
required=set('screenshot settings clipboard copy paste offline online translate translation startup configuration hotkey language model server network computer hello world'.split())
phrases={'good morning','good night','good evening','good afternoon','thank you','you are welcome','how are you','see you','see you later','excuse me','never mind','of course','by the way','in fact','as well as','a lot of','for example','at least','in order to','look for','turn on','turn off','log in','sign in','check in','take care','come on','well done','no problem','not at all','operating system','artificial intelligence','machine learning','source code','open source'}
rows=[];extra=[]
with source.open(encoding='utf-8-sig',newline='') as file:
    for row in csv.DictReader(file):
        word=row['word'].strip().lower();meaning=row['translation'].strip().replace('\\n','; ')
        if not re.fullmatch("[a-z][a-z0-9' -]{0,47}",word) or not re.search('[\u4e00-\u9fff]',meaning):continue
        meaning=re.sub(r'\s+',' ',meaning)[:480]
        ranks=[int(row[k]) for k in ('bnc','frq') if row[k].isdigit() and int(row[k])>0]
        rank=min(ranks,default=10**9)
        if ' ' not in word:rows.append((rank,word,meaning))
        if word in required or word in phrases or (' ' in word and (row['oxford']=='1' or rank<=10000)):extra.append((rank,word,meaning))
selected={word:meaning for rank,word,meaning in sorted(rows)[:12000]}
selected.update({word:meaning for rank,word,meaning in extra})
# Small application vocabulary is maintained by this project, separate from the upstream subset.
supplements={'hello':'你好; 喂','settings':'设置','clipboard':'剪贴板','screenshot':'屏幕截图','hotkey':'快捷键','startup':'启动; 开机启动','offline':'离线','online':'在线'}
selected.update(supplements)
selected={**supplements,**selected}
root=Path(__file__).resolve().parents[1]
target=root/'src/Snip.Translation/Resources/basic-dictionary.json';target.parent.mkdir(exist_ok=True)
data=json.dumps(list(selected.items()),ensure_ascii=False,separators=(',',':'))+'\n';target.write_bytes(data.encode('utf-8'))
print(len(selected),'entries;',target.stat().st_size,'bytes;',hashlib.sha256(target.read_bytes()).hexdigest())
