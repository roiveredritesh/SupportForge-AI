# TEMPORARY diagnostic (throwaway branch): Python copy of GraphDbQueryTool.BuildSearchQuery.
import re, sys
STOP = set("""a an the is are was were be been being of to in on for with by at from as and or not no it its this that these those
which what who whom whose when where why how does do did can could should would will shall may might must there their they them we
our you your i me my he she his her about into than then so such just also any some all each every more most other only own same
very instead if else get class classes function functions method methods code file files implement implements implemented
implementation project repo repository source use used uses using call calls called handle handles handled work works happen
happens related terms documentation please tell show explain find""".split())
WORD = re.compile(r"[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+\d*|[A-Z]+\d*|\d+")
def stem(w):
    for s in ("ing", "ed", "es", "s"):
        if w.endswith(s) and len(w) - len(s) >= 4 and not (s == "s" and w.endswith("ss")):
            return w[:-len(s)]
    return w
def build(q):
    terms = []
    for m in re.finditer(r"[A-Za-z][A-Za-z0-9_]*|\d+", q):
        w = m.group(0); lower = w.lower()
        parts = [p.lower() for p in WORD.findall(w)]
        if len(parts) > 1 and lower not in STOP: terms.append(lower)
        for p in parts:
            if len(p) < 2 or p in STOP: continue
            terms.append(p)
            st = stem(p)
            if st != p: terms.append(st + "*")
    out = []
    for t in terms:
        if t not in out: out.append(t)
    return " ".join(out[:12])
print(build(sys.argv[1]))
