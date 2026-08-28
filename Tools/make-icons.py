#!/usr/bin/env python
"""
Generates Lumigram's icons and app-bar glyphs.

Kept as a script rather than checked-in binaries so the artwork can be adjusted
and regenerated, and so there is a record of what the images actually are.

    python Tools/make-icons.py

Two different conventions are produced, and they are not interchangeable:

  App and tile icons  - full-bleed, brand colour background, white dove.
  App-bar icons       - 76x76, transparent background, a single flat shape
                        drawn inside a 26px margin. Windows Phone draws its own
                        circle around these; an icon that includes its own
                        circle ends up with two.

Needs: pip install opencv-python-headless numpy
"""

import os
import numpy as np
import cv2

BRAND = (196, 123, 15)        # BGR - a blue close to the phone's default accent
WHITE = (255, 255, 255)

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(HERE, 'Phone', 'Assets')
TILES = os.path.join(ASSETS, 'Tiles')


def _smooth(points, samples=24):
    """
    Rounds a closed outline off with a Catmull-Rom spline.

    Filling the control points directly gives a faceted shape - straight lines
    between every corner, which looks like folded paper rather than a bird.
    Sampling a spline through the same points keeps the intent and loses the
    facets.
    """
    pts = list(points)
    n = len(pts)
    out = []

    for i in range(n):
        p0 = np.array(pts[(i - 1) % n], dtype=float)
        p1 = np.array(pts[i], dtype=float)
        p2 = np.array(pts[(i + 1) % n], dtype=float)
        p3 = np.array(pts[(i + 2) % n], dtype=float)

        for s in range(samples):
            t = s / float(samples)
            t2, t3 = t * t, t * t * t
            point = 0.5 * ((2 * p1) +
                           (-p0 + p2) * t +
                           (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 +
                           (-p0 + 3 * p1 - 3 * p2 + p3) * t3)
            out.append(point)

    return out


# The bird, as three overlapping outlines in normalised 0..1 coordinates. Shared
# rather than inlined because the lock-screen icon has to be the same shape drawn
# a different way - a flat silhouette instead of white-on-blue.

# Tail: a narrow fan, swept back and slightly down.
TAIL = [(0.33, 0.47), (0.19, 0.39), (0.06, 0.40), (0.06, 0.40),
        (0.13, 0.51), (0.04, 0.63), (0.04, 0.63), (0.19, 0.61),
        (0.33, 0.57)]

# Body: tail root, along the back, into the head, down the breast.
BODY = [(0.32, 0.48), (0.50, 0.44), (0.66, 0.40), (0.78, 0.35),
        (0.87, 0.37), (0.91, 0.42), (0.99, 0.455), (0.99, 0.455),
        (0.88, 0.485), (0.81, 0.53), (0.70, 0.59), (0.55, 0.635),
        (0.42, 0.63), (0.33, 0.575)]

# Wing: a swept shape with a pointed tip up and back. The tip is repeated so the
# spline stays tight to it - a single control point gets rounded off and the wing
# turns into a balloon.
WING = [(0.47, 0.51), (0.56, 0.41), (0.545, 0.29),
        (0.47, 0.17), (0.36, 0.08), (0.36, 0.08), (0.36, 0.08),
        (0.30, 0.19), (0.32, 0.33), (0.39, 0.45)]


def lockscreen(size=38, margin=2):
    """
    The lock-screen icon: a white silhouette on transparency.

    Windows Phone uses the alpha channel and nothing else here - every visible
    pixel is repainted in the accent colour - so the wing outline and the eye
    that separate the shapes at tile sizes are dropped. They are drawn in the
    brand colour, which does not survive, and at 38 pixels they would close up
    into noise anyway.

    Drawn large and scaled down rather than filled at 38 pixels directly: the
    spline sampling and anti-aliasing both need room to work. The silhouette is
    then cropped to its own bounds and centred, because the bird is much wider
    than it is tall and would otherwise sit in a band across the middle of the
    square with most of the icon empty.
    """
    work = 512
    mask = np.zeros((work, work), dtype=np.uint8)

    for outline in (TAIL, BODY, WING):
        smooth = _smooth(outline)
        cv2.fillPoly(mask, [np.array([(int(x * work), int(y * work))
                                      for x, y in smooth], dtype=np.int32)],
                     255, cv2.LINE_AA)

    columns = np.where(mask.any(axis=0))[0]
    rows = np.where(mask.any(axis=1))[0]
    if len(columns) == 0 or len(rows) == 0:
        return np.zeros((size, size, 4), dtype=np.uint8)

    mask = mask[rows[0]:rows[-1] + 1, columns[0]:columns[-1] + 1]

    box = size - 2 * margin
    height, width = mask.shape
    scale = min(box / float(width), box / float(height))
    target = (max(1, int(round(width * scale))), max(1, int(round(height * scale))))
    mask = cv2.resize(mask, target, interpolation=cv2.INTER_AREA)

    img = np.zeros((size, size, 4), dtype=np.uint8)
    img[:, :, 0:3] = WHITE

    top = (size - mask.shape[0]) // 2
    left = (size - mask.shape[1]) // 2
    img[top:top + mask.shape[0], left:left + mask.shape[1], 3] = mask
    return img


def dove(size, scale=1.0):
    """
    The app mark: a white dove on the brand colour.

    Body, tail and wing are separate filled outlines that overlap, so the result
    is one connected silhouette rather than shapes stacked with gaps between
    them. Each outline is smoothed - see _smooth for why.

    Coordinates are normalised 0..1 so one drawing serves every size, from a
    71px tile to the 768px splash.
    """
    img = np.zeros((size, size, 4), dtype=np.uint8)
    img[:, :, 0:3] = BRAND
    img[:, :, 3] = 255

    W = WHITE + (255,)
    B = BRAND + (255,)

    def fill(points, colour):
        smooth = _smooth(points)
        cv2.fillPoly(img, [np.array([(int(x * size), int(y * size))
                                     for x, y in smooth], dtype=np.int32)],
                     colour, cv2.LINE_AA)

    fill(TAIL, W)
    fill(BODY, W)
    wing = WING
    fill(wing, W)

    # Outline the wing in the brand colour. Over the blue background it is
    # invisible; over the white body it is the edge that keeps the wing from
    # merging into the bird.
    outline = _smooth(wing)
    cv2.polylines(img, [np.array([(int(x * size), int(y * size))
                                  for x, y in outline], dtype=np.int32)],
                  True, B, max(1, int(size * 0.022)), cv2.LINE_AA)

    # Eye.
    cv2.circle(img, (int(0.835 * size), int(0.428 * size)),
               max(1, int(size * 0.017)), B, -1, cv2.LINE_AA)

    return img


def bubble(size, scale=1.0):
    """Kept as the name the rest of the script calls; the mark is the dove."""
    return dove(size, scale)


def wide(width, height):
    """Wide tile: the mark on the left, name alongside."""
    img = np.zeros((height, width, 4), dtype=np.uint8)
    img[:, :, 0:3] = BRAND
    img[:, :, 3] = 255

    mark = bubble(height)
    inset = int(height * 0.16)
    small = cv2.resize(mark, (height - 2 * inset, height - 2 * inset),
                       interpolation=cv2.INTER_AREA)
    img[inset:height - inset, inset:height - inset] = small

    cv2.putText(img, 'Lumigram', (height + inset // 2, height // 2 + int(height * 0.08)),
                cv2.FONT_HERSHEY_SIMPLEX, height / 240.0, WHITE + (255,),
                max(1, int(height / 90)), cv2.LINE_AA)
    return img


def appbar_plus(size=76):
    """App-bar glyph: a plus. Transparent, flat white, inside the safe margin."""
    img = np.zeros((size, size, 4), dtype=np.uint8)
    m = int(size * 0.30)
    t = max(2, int(size * 0.075))
    c = size // 2
    cv2.rectangle(img, (m, c - t // 2), (size - m, c + t // 2), WHITE + (255,), -1)
    cv2.rectangle(img, (c - t // 2, m), (c + t // 2, size - m), WHITE + (255,), -1)
    return img


def appbar_refresh(size=76):
    """App-bar glyph: a circular arrow."""
    img = np.zeros((size, size, 4), dtype=np.uint8)
    c = size // 2
    r = int(size * 0.22)
    t = max(2, int(size * 0.07))

    # An open arc, leaving a gap for the arrowhead.
    cv2.ellipse(img, (c, c), (r, r), 0, 40, 340, WHITE + (255,), t, cv2.LINE_AA)

    # Arrowhead at the open end.
    ax = int(c + r * np.cos(np.radians(40)))
    ay = int(c + r * np.sin(np.radians(40)))
    s = int(size * 0.10)
    head = np.array([[ax + s, ay - s // 2], [ax - s, ay - s // 2], [ax, ay + s]],
                    dtype=np.int32)
    cv2.fillPoly(img, [head], WHITE + (255,))
    return img


def write(path, img):
    cv2.imwrite(path, img)
    print('  %-52s %dx%d' % (os.path.relpath(path, HERE), img.shape[1], img.shape[0]))


def main():
    if not os.path.isdir(TILES):
        os.makedirs(TILES)

    print('app and tile icons:')
    write(os.path.join(ASSETS, 'ApplicationIcon.png'), bubble(100))
    write(os.path.join(ASSETS, 'Logo.scale-240.png'), bubble(106))
    write(os.path.join(ASSETS, 'SquareTile71x71.scale-240.png'), bubble(170))
    write(os.path.join(ASSETS, 'SquareTile150x150.scale-240.png'), bubble(360))
    write(os.path.join(ASSETS, 'StoreLogo.scale-240.png'), bubble(120))
    write(os.path.join(ASSETS, 'BadgeLogo.scale-240.png'), bubble(58))
    write(os.path.join(ASSETS, 'WideLogo.scale-240.png'), wide(744, 360))

    write(os.path.join(TILES, 'FlipCycleTileSmall.png'), bubble(159))
    write(os.path.join(TILES, 'FlipCycleTileMedium.png'), bubble(336))
    write(os.path.join(TILES, 'FlipCycleTileLarge.png'), wide(691, 336))
    write(os.path.join(TILES, 'IconicTileSmall.png'), bubble(110))
    write(os.path.join(TILES, 'IconicTileMediumLarge.png'), bubble(202))

    # Lock screen: alpha-only, and a fixed 38x38 - the OS does not scale it.
    write(os.path.join(TILES, 'LockScreenIcon.png'), lockscreen(38))

    # Splash: the mark centred on the brand colour.
    splash = np.zeros((1280, 768, 4), dtype=np.uint8)
    splash[:, :, 0:3] = BRAND
    splash[:, :, 3] = 255
    mark = cv2.resize(bubble(360), (300, 300), interpolation=cv2.INTER_AREA)
    splash[490:790, 234:534] = mark
    write(os.path.join(ASSETS, 'SplashScreen.scale-240.png'), splash)

    write(os.path.join(HERE, 'Docs', 'icon-preview.png'), dove(512))

    print()
    print('app-bar glyphs:')
    write(os.path.join(ASSETS, 'appbar.add.png'), appbar_plus())
    write(os.path.join(ASSETS, 'appbar.refresh.png'), appbar_refresh())


if __name__ == '__main__':
    main()
