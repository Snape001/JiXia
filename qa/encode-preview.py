from pathlib import Path
import sys

from PIL import Image


def on_background(file):
    with Image.open(file) as source:
        rgba = source.convert('RGBA')
        base = Image.new('RGB', rgba.size, '#1d1c1b')
        base.paste(rgba, mask=rgba.getchannel('A'))
        return base


directory, target = map(Path, sys.argv[1:3])
files = sorted(directory.glob('f-*.png'))
if len(files) < 55:
    raise ValueError('The native interaction recording is incomplete.')
times = [int(file.stem.rsplit('-', 1)[1]) for file in files]
durations = [max(10, b-a) for a, b in zip(times, times[1:])] + [400]
samples = files[::max(1, len(files) // 12)] + [directory / 'bin-open.png']
swatches = Image.new('RGB', (186 * len(samples), 305))
for index, file in enumerate(samples):
    swatches.paste(on_background(file).resize((186, 305), Image.Resampling.LANCZOS), (186 * index, 0))
palette = swatches.quantize(colors=256)
frames = [on_background(name).quantize(palette=palette, dither=Image.Dither.NONE) for name in files]
frames[0].save(target, save_all=True, append_images=frames[1:], duration=durations, loop=0, optimize=True)
with Image.open(target) as result:
    if result.n_frames < 24 or result.size != (372, 610):
        raise ValueError('Motion preview verification failed.')
    print('Verified', result.n_frames, 'GIF frames from', len(files), 'native renders')
