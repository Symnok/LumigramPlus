#!/usr/bin/env python
"""
Generates the appx package images from Art/logo.jpg.

    python Tools/make-appx-icons.py

A script rather than checked-in binaries, so the artwork can be replaced and
everything that derives from it regenerated in one step. Every image the
manifest references is produced here; there is nothing hand-made left in
App/Assets to fall out of step with the rest.

The source is a square: brand background, white bird. Two things are taken from
it - the background colour, sampled from a corner rather than assumed, and the
bird's outline, found as the pixels that differ from that background.

  Square tiles   the source, resized. It is already framed for a square.
  Wide tile      brand canvas, bird centred. Resizing the square would either
                 stretch the bird or crop it.
  Splash         the same, with the bird smaller against a larger field.
  Badge          24x24 nominal, 58x58 at scale-240, and a different thing
                 entirely - the lock screen draws the alpha channel in the
                 user's accent colour and discards the colours, so this is a
                 silhouette rather than a picture. The build rejects any other
                 size outright, so it is not a matter of taste.

Needs: pip install opencv-python-headless numpy
"""

import os
import numpy as np
import cv2

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(HERE, 'Art', 'logo.jpg')
ASSETS = os.path.join(HERE, 'App', 'Assets')

# Nominal size, then the file's real pixel size at scale-240. Only scale-240 is
# shipped: it is what a 1080p Lumia asks for, and the phone downsamples for the
# smaller ones rather than refusing to draw.
SQUARES = [
    ('Square150x150Logo', 360),
    ('Square71x71Logo', 170),
    ('Square44x44Logo', 106),
    ('StoreLogo', 120),
]

WIDE = ('Wide310x150Logo', 744, 360)
SPLASH = ('SplashScreen', 1152, 1920)

# How much of the shorter side the bird takes up on a canvas it does not fill.
WIDE_FILL = 0.62
SPLASH_FILL = 0.30

BADGE = 58


def load():
    """The source, its background colour, and the bird's bounding box."""
    image = cv2.imread(SOURCE, cv2.IMREAD_COLOR)
    if image is None:
        raise SystemExit('missing ' + SOURCE)

    # Sampled, not assumed. A re-exported logo can shift by a few levels, and a
    # hard-coded colour would leave a visible seam on the wide tile.
    background = image[2, 2].astype(int)

    distance = np.abs(image.astype(int) - background).sum(axis=2)
    mask = (distance > 60).astype(np.uint8)

    ys, xs = np.nonzero(mask)
    if len(ys) == 0:
        raise SystemExit('no bird found in ' + SOURCE)

    box = (xs.min(), ys.min(), xs.max() + 1, ys.max() + 1)
    return image, background, mask, box


def opaque(bgr):
    """BGR to BGRA. The package images are opaque; only the badge is not."""
    alpha = np.full(bgr.shape[:2] + (1,), 255, np.uint8)
    return np.concatenate([bgr, alpha], axis=2)


def write(name, image):
    path = os.path.join(ASSETS, name + '.scale-240.png')
    cv2.imwrite(path, image)
    print('%-22s %dx%d' % (name, image.shape[1], image.shape[0]))


def centred(source, box, background, width, height, fill):
    """The bird on a plain canvas, scaled to a share of the shorter side."""
    canvas = np.zeros((height, width, 3), np.uint8)
    canvas[:, :] = background

    x0, y0, x1, y1 = box
    bird = source[y0:y1, x0:x1]

    target = int(min(width, height) * fill)
    scale = target / float(max(bird.shape[0], bird.shape[1]))

    w = max(1, int(round(bird.shape[1] * scale)))
    h = max(1, int(round(bird.shape[0] * scale)))
    bird = cv2.resize(bird, (w, h), interpolation=cv2.INTER_AREA)

    # The crop carries its own background, and it is the same colour as the
    # canvas, so pasting the rectangle whole leaves no edge to blend.
    x = (width - w) // 2
    y = (height - h) // 2
    canvas[y:y + h, x:x + w] = bird

    return canvas


def badge(mask, box):
    """
    The lock screen icon: a silhouette in the alpha channel.

    White everywhere, because the lock screen recolours it - what survives is
    the shape. Cropped to the bird first: a 24x24 image of a mostly empty square
    is a bird a few pixels across.
    """
    x0, y0, x1, y1 = box
    shape = mask[y0:y1, x0:x1] * 255

    side = max(shape.shape[0], shape.shape[1])
    square = np.zeros((side, side), np.uint8)

    y = (side - shape.shape[0]) // 2
    x = (side - shape.shape[1]) // 2
    square[y:y + shape.shape[0], x:x + shape.shape[1]] = shape

    # A little margin, so the shape is not flush against the edges.
    inner = int(BADGE * 0.86)
    alpha = np.zeros((BADGE, BADGE), np.uint8)
    scaled = cv2.resize(square, (inner, inner), interpolation=cv2.INTER_AREA)

    offset = (BADGE - inner) // 2
    alpha[offset:offset + inner, offset:offset + inner] = scaled

    white = np.full((BADGE, BADGE, 3), 255, np.uint8)
    return np.concatenate([white, alpha[:, :, None]], axis=2)



def main():
    source, background, mask, box = load()

    for name, side in SQUARES:
        square = cv2.resize(source, (side, side), interpolation=cv2.INTER_AREA)
        write(name, opaque(square))

    name, width, height = WIDE
    write(name, opaque(centred(source, box, background, width, height, WIDE_FILL)))

    name, width, height = SPLASH
    write(name, opaque(centred(source, box, background, width, height, SPLASH_FILL)))

    path = os.path.join(ASSETS, 'BadgeLogo.scale-240.png')
    cv2.imwrite(path, badge(mask, box))
    print('%-22s %dx%d  (alpha only)' % ('BadgeLogo', BADGE, BADGE))


if __name__ == '__main__':
    main()
