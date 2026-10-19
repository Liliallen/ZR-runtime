import math, sys
def load(p):
    d=open(p,'rb').read(); parts=d.split(b'\n',3); w,h=map(int,parts[1].split()); return w,h,parts[3]
d=sys.argv[1]
w,h,T=load(d+'/truth.pgm'); _,_,D=load(d+'/dlss.pgm'); _,_,B=load(d+'/bilinear.pgm')
scroll=float(sys.argv[2]) if len(sys.argv)>2 else 63*3.0
bands=[(0,150),(150,300),(300,450),(450,600)]
for r0,r1 in bands:
    md=mb=0;n=0
    for y in range(0,h,2):
        for x in range(0,w,2):
            u=x+scroll-960; v=y-540; r=math.hypot(u,v)
            if r0<=r<r1:
                t=T[y*w+x]; md+=(D[y*w+x]-t)**2; mb+=(B[y*w+x]-t)**2; n+=1
    f=lambda m: 10*math.log10(255*255/(m/n)) if n and m else float('nan')
    print(f"radius {r0:3d}-{r1:3d}px (ring period ~{2*math.pi/(0.0018*max(r0,1)):.1f}px): DLSS {f(md):5.1f} dB  bilinear {f(mb):5.1f} dB  n={n}")
