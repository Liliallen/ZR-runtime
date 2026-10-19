// Captures one X11/XWayland window (found by title substring, case-insensitive) to a PPM file.
// Only that window is read; nothing else on the desktop is captured. usage: xshot <title-substring> <out.ppm>
#define _GNU_SOURCE
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <stdio.h>
#include <string.h>

static Window find(Display *d, Window w, const char *needle, int *best_area) {
    Window found = 0; char *name = NULL;
    if (XFetchName(d, w, &name) && name) {
        if (strcasestr(name, needle)) {
            XWindowAttributes a; XGetWindowAttributes(d, w, &a);
            if (a.map_state == IsViewable && a.width * a.height > *best_area) { *best_area = a.width * a.height; found = w; }
        }
        XFree(name);
    }
    Window root, parent, *children = NULL; unsigned n = 0;
    if (XQueryTree(d, w, &root, &parent, &children, &n)) {
        for (unsigned i = 0; i < n; i++) { Window f = find(d, children[i], needle, best_area); if (f) found = f; }
        if (children) XFree(children);
    }
    return found;
}

int main(int argc, char **argv) {
    if (argc < 3) { fprintf(stderr, "usage: xshot <title-substring> <out.ppm>\n"); return 2; }
    Display *d = XOpenDisplay(NULL); if (!d) return 1;
    int area = 0; Window w = find(d, DefaultRootWindow(d), argv[1], &area);
    if (!w) { fprintf(stderr, "no window titled *%s*\n", argv[1]); return 1; }
    XWindowAttributes a; XGetWindowAttributes(d, w, &a);
    XImage *img = XGetImage(d, w, 0, 0, a.width, a.height, AllPlanes, ZPixmap);
    if (!img) { fprintf(stderr, "XGetImage failed\n"); return 1; }
    FILE *f = fopen(argv[2], "wb"); fprintf(f, "P6\n%d %d\n255\n", a.width, a.height);
    for (int y = 0; y < a.height; y++) for (int x = 0; x < a.width; x++) {
        unsigned long p = XGetPixel(img, x, y);
        unsigned char rgb[3] = { (p >> 16) & 255, (p >> 8) & 255, p & 255 }; fwrite(rgb, 1, 3, f);
    }
    fclose(f); fprintf(stderr, "%dx%d\n", a.width, a.height); return 0;
}
