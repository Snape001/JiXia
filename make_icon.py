from pathlib import Path
from PIL import Image, ImageDraw

size = 512
im = Image.new('RGBA', (size, size))
draw = ImageDraw.Draw(im)
draw.rounded_rectangle((14, 14, 497, 497), radius=114, fill='#26221F', outline='#655447', width=10)
draw.rounded_rectangle((118, 117, 389, 390), radius=37, fill='#34302C', outline='#A29785', width=14)
draw.rounded_rectangle((317, 143, 362, 364), radius=15, fill='#DDA45D')
draw.rounded_rectangle((160, 180, 265, 199), radius=8, fill='#F2E9DB')
draw.rounded_rectangle((160, 236, 236, 252), radius=7, fill='#B6A99A')
im.save(Path(__file__).parent / 'src' / 'SidePage.ico', sizes=[(n,n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
