exec(open('evalc.py').read().split('if __name__')[0])
ALL=list(DEV)
def run(cand,cs,field,hi,med):
    global DEV
    sc=load(cand,cs); res=[]
    for vids in ([ALL[0]],[ALL[1]],ALL):
        DEV=vids; r=metrics(sc,field,hi,med); res.append(f"{'+'.join(v[:4] for v in vids)}: wr={r['wr']} sr={r['sr']} fbr={r['fbr']} fs={r['fs']}")
    DEV=ALL; print(f"{cand} {cs} {field} hi={hi} med={med}"); [print("   ",x) for x in res]
for cs in ("30_12","24_8"):
    for hi,med in [(0.32,0.14),(0.32,0.18),(0.32,0.20),(0.28,0.18),(0.25,0.20)]:
        run("minilm_lr",cs,"score",hi,med)
