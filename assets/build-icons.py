"""Render the launch mark at desktop and tray sizes."""
from pathlib import Path
from PIL import Image, ImageDraw
import io
import struct
root = Path(__file__).parent
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
def render(size):
    scale = size * 4 / 256
    image = Image.new('RGBA', (size * 4, size * 4))
    draw = ImageDraw.Draw(image)
    def points(values): return [(round(x * scale), round(y * scale)) for x, y in values]
    draw.rounded_rectangle([round(v * scale) for v in (12,12,244,244)], radius=round(58*scale), fill='#A64B35')
    arrow = [(128,49),(184,105),(164,125),(142,103),(142,161),(114,161),(114,103),(92,125),(72,105)]
    draw.polygon(points(arrow), fill='#FFFAEE')
    if size <= 24:
        draw.rounded_rectangle([round(v*scale) for v in (57,180,199,204)], radius=round(9*scale), fill='#FFFAEE')
    else:
        draw.line(points([(66,166),(66,193),(190,193),(190,166)]), fill='#FFFAEE', width=round(20*scale), joint='curve')
        for x,y in [(66,166),(66,193),(190,193),(190,166)]:
            draw.ellipse([round(v*scale) for v in (x-10,y-10,x+10,y+10)], fill='#FFFAEE')
    return image.resize((size,size), Image.Resampling.LANCZOS)
frames=[]
for size in sizes:
    img=render(size); img.save(root/f'startuphub-{size}.png')
    stream=io.BytesIO(); img.save(stream,format='PNG'); frames.append(stream.getvalue())
offset=6+len(sizes)*16
with (root/'startuphub.ico').open('wb') as file:
    file.write(struct.pack('<HHH',0,1,len(sizes)))
    for size,frame in zip(sizes,frames):
        file.write(struct.pack('<BBBBHHII',size%256,size%256,0,0,1,32,len(frame),offset)); offset+=len(frame)
    for frame in frames: file.write(frame)
render(512).save(root/'startuphub.png')
