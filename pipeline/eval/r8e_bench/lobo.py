import json,sys
exec(open('evalc.py').read().split('if __name__')[0])
ALL=list(DEV)
def m_on(sc,field,hi,med,vids):
    global DEV
    DEV=vids
    try: return metrics(sc,field,hi,med)
    finally: DEV=ALL
for cand,cs,field in [("e5s","24_8","lr"),("e5b","30_12","lr"),("e5b","24_8","lr"),("minilm_lr","24_8","lr")]:
    sc=load(cand,cs)
    vals=sorted(c[field] for v in sc.values() for c in v if c.get(field) is not None)
    qs=[vals[int(len(vals)*q)] for q in (0.6,0.7,0.75,0.8,0.85,0.88,0.9,0.92,0.94,0.96,0.98)]
    out=[]
    for tr,te in [(ALL[0],ALL[1]),(ALL[1],ALL[0])]:
        best=None
        for hi in qs+[9]:
            for med in qs:
                if med>hi: continue
                r=m_on(sc,field,hi,med,[tr])
                if r["_fbr"]<=0.015:
                    key=(r["_sr"],r["wr"])
                    if best is None or key>best[0]: best=(key,hi,med)
        if best is None: out.append(f'train {tr[:4]}: no threshold with fbr<=0.015'); continue
        hi,med=best[1],best[2]; r=m_on(sc,field,hi,med,[te])
        out.append(f"train {tr[:4]} -> test {te[:4]}: hi={hi:.3f} med={med:.3f} wr={r['wr']} sr={r['sr']} fbr={r['fbr']} fs={r['fs']}")
    print(cand,cs,field); [print("  ",o) for o in out]
