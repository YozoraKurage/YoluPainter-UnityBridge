#!/usr/bin/env python3
"""Independent PSD byte/interop spike. No dependency is needed to build/inspect fixtures.
Optional Pillow, psd-tools and ImageMagick checks are reported separately; absent tools are SKIP.
This is not the C# implementation and cannot establish that C# tests or Unity tests passed.
Run: python Tools~/psd_spike.py --out Validation~/psd-spike [--csharp path.psd ...]
"""
import argparse
import hashlib
import importlib.metadata
import io
import json
import math
import os
import tempfile
from xml.sax.saxutils import escape as xml_escape
from pathlib import Path
import shutil
import struct
import subprocess


def be(fmt, *values):
    return struct.pack('>' + fmt, *values)


def block(data):
    return be('I', len(data)) + data


def tag(key, data):
    return b'8BIM' + key.encode('ascii') + block(data) + b'\0' * (len(data) % 2)


def fixture_layers():
    # Public order is top-to-bottom. Off-canvas and hidden RGB must survive storage.
    return [
        dict(id=303, name='非表示', left=-1, top=0, width=2, height=2,
             opacity=91, visible=False, rgba=bytes([19, 77, 211, 255] * 4)),
        dict(id=202, name='筆 🎨', left=1, top=0, width=2, height=2,
             opacity=177, visible=True, rgba=bytes([255, 32, 64, 255, 5, 220, 90, 128,
                                                  10, 20, 30, 0, 70, 80, 220, 200])),
        dict(id=101, name='下地', left=0, top=0, width=4, height=3,
             opacity=255, visible=True,
             rgba=bytes(v for y in range(3) for x in range(4)
                        for v in (20 + x * 20, 40 + y * 20, 140, 64 + 32 * ((x + y) % 4)))),
    ]


def composite(width, height, layers, white_matte=True):
    out = bytearray()
    for y in range(height):
        for x in range(width):
            color, alpha = [0.0] * 3, 0.0
            for layer in reversed(layers):
                lx, ly = x - layer['left'], y - layer['top']
                if not layer['visible'] or not (0 <= lx < layer['width'] and 0 <= ly < layer['height']):
                    continue
                p = (ly * layer['width'] + lx) * 4
                sample = layer['rgba'][p:p+4]
                a = sample[3] * layer['opacity'] / 65025.0
                color = [sample[c] * a + color[c] * (1-a) for c in range(3)]
                alpha = a + alpha * (1-a)
            if white_matte:
                color = [c + 255 * (1-alpha) for c in color]
            else:
                color = [c / alpha if alpha else 255 for c in color]
            out.extend(max(0, min(255, math.floor(c + .5))) for c in color + [alpha * 255])
    return bytes(out)


def packbits(row):
    # Literal-only PackBits is still valid RLE and independent of production writer (which emits raw).
    return b''.join(bytes([len(row[p:p+128])-1]) + row[p:p+128] for p in range(0, len(row), 128))


def plane(rgba, width, height, channel, rle):
    data = rgba[channel::4]
    if not rle:
        return be('H', 0) + data
    rows = [packbits(data[y*width:(y+1)*width]) for y in range(height)]
    return be('H', 1) + b''.join(be('H', len(row)) for row in rows) + b''.join(rows)


def write_fixture(width, height, layers, rle=False, unknown=False):
    records, channels = [], []
    for layer in reversed(layers):  # PSD physical records are bottom-to-top.
        planes = [plane(layer['rgba'], layer['width'], layer['height'], c, rle) for c in range(4)]
        fallback = layer['name'].encode('ascii', 'replace')[:255]
        pascal = bytes([len(fallback)]) + fallback
        pascal += b'\0' * (-len(pascal) % 4)
        unicode = layer['name'].encode('utf-16-be')
        extra = be('II', 0, 0) + pascal
        extra += tag('luni', be('I', len(unicode)//2) + unicode)
        extra += tag('lyid', be('i', layer['id']))
        if unknown and layer['id'] == 202:
            extra += tag('zzzz', b'opaque-unknown-record')
        record = be('iiiiH', layer['top'], layer['left'], layer['top'] + layer['height'],
                    layer['left'] + layer['width'], 4)
        record += b''.join(be('hI', c if c < 3 else -1, len(planes[c])) for c in range(4))
        record += b'8BIMnorm' + bytes([layer['opacity'], 0, 0 if layer['visible'] else 2, 0]) + block(extra)
        records.append(record)
        channels.extend(planes)
    info = be('h', -len(layers)) + b''.join(records + channels)
    info += b'\0' * (len(info) % 2)
    lm = block(info) + be('I', 0)
    merged = composite(width, height, layers)
    if rle:
        rows = [packbits(merged[c::4][y*width:(y+1)*width]) for c in range(4) for y in range(height)]
        image_data = be('H', 1) + b''.join(be('H', len(row)) for row in rows) + b''.join(rows)
    else:
        image_data = be('H', 0) + b''.join(merged[c::4] for c in range(4))
    return b'8BPS' + be('H6xHIIHH', 1, 4, height, width, 8, 3) + be('II', 0, 0) + block(lm) + image_data


class Reader:
    def __init__(self, data):
        self.data, self.p = data, 0

    def read(self, count):
        assert 0 <= count <= len(self.data) - self.p, 'truncated fixture'
        data = self.data[self.p:self.p+count]
        self.p += count
        return data

    def unpack(self, fmt):
        return struct.unpack('>'+fmt, self.read(struct.calcsize('>'+fmt)))

    def section(self):
        return Reader(self.read(self.unpack('I')[0]))

    @property
    def remaining(self):
        return len(self.data)-self.p


def unpack_row(data, width):
    r, row = Reader(data), bytearray()
    while r.remaining:
        n = r.unpack('b')[0]
        if n == -128:
            continue
        row += r.read(n+1) if n >= 0 else r.read(1) * (1-n)
        assert len(row) <= width, 'PackBits row overflow'
    assert len(row) == width, 'PackBits row underflow'
    return bytes(row)


def decode_channel(data, width, height):
    r = Reader(data)
    compression = r.unpack('H')[0]
    if compression == 0:
        pixels = r.read(width*height)
    else:
        assert compression == 1
        lengths = [r.unpack('H')[0] for _ in range(height)]
        pixels = b''.join(unpack_row(r.read(length), width) for length in lengths)
    assert not r.remaining
    return pixels


def inspect_bytes(data):
    r = Reader(data)
    assert r.read(4) == b'8BPS'
    version, channels, height, width, depth, mode = r.unpack('H6xHIIHH')
    assert (version, channels, depth, mode) == (1, 4, 8, 3)
    assert not r.section().remaining
    assert not r.section().remaining
    lm = r.section()
    info = lm.section()
    count = info.unpack('h')[0]
    assert count < 0
    layers = []
    for _ in range(-count):
        top, left, bottom, right, n = info.unpack('iiiiH')
        channel_info = [info.unpack('hI') for _ in range(n)]
        assert info.read(8) == b'8BIMnorm'
        opacity, clipping, flags, filler = info.unpack('4B')
        assert clipping == filler == 0
        extra = info.section()
        assert not extra.section().remaining and not extra.section().remaining
        n = extra.unpack('B')[0]
        extra.read(n)
        extra.read(-(n+1) % 4)
        tags = {}
        while extra.remaining:
            assert extra.read(4) == b'8BIM'
            key = extra.read(4).decode('ascii')
            body = extra.section().data
            extra.read(len(body) % 2)
            tags[key] = body
        units = struct.unpack('>I', tags['luni'][:4])[0]
        name = tags['luni'][4:4+units*2].decode('utf-16-be')
        layers.append(dict(id=struct.unpack('>i', tags['lyid'])[0], name=name, left=left, top=top,
                           width=right-left, height=bottom-top, opacity=opacity, visible=not(flags&2),
                           channel_info=channel_info, unknown=[k for k in tags if k not in ('luni','lyid')]))
    for layer in layers:
        rgba = bytearray(layer['width']*layer['height']*4)
        for channel, length in layer.pop('channel_info'):
            rgba[3 if channel == -1 else channel::4] = decode_channel(info.read(length), layer['width'], layer['height'])
        layer['rgba'] = bytes(rgba)
    assert info.read(info.remaining) in (b'', b'\0')
    assert not lm.section().remaining and not lm.remaining
    compression = r.unpack('H')[0]
    if compression == 0:
        planes = [r.read(width*height) for _ in range(channels)]
    else:
        assert compression == 1
        lengths = [r.unpack('H')[0] for _ in range(channels*height)]
        planes = [b''.join(unpack_row(r.read(lengths[c*height+y]), width) for y in range(height)) for c in range(channels)]
    assert not r.remaining
    merged = bytes(planes[c][p] for p in range(width*height) for c in range(4))
    layers.reverse()
    assert merged == composite(width, height, layers), 'stored merged image disagrees with declared layer stack'
    return width, height, layers, merged


def normalized_name(name):
    # psd-tools 1.10.x exposes UTF-16 surrogate code units as two Python characters.
    return name.encode('utf-16-be', 'surrogatepass').decode('utf-16-be')


def validate(path):
    data = path.read_bytes()
    width, height, layers, merged = inspect_bytes(data)
    result = dict(file=path.name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest(),
                  independent_byte_oracle='PASS', layers=[dict(id=x['id'], name=x['name'],
                    rgba_sha256=hashlib.sha256(x['rgba']).hexdigest(), unknown=x['unknown']) for x in layers])
    try:
        from PIL import Image
        image = Image.open(path)
        assert image.size == (width, height)
        assert image.tobytes() == merged, 'Pillow raw merged sample mismatch'
        result['pillow'] = dict(status='PASS', version=importlib.metadata.version('Pillow'),
                                note='Reads white-matted merged samples directly; this is not a color-managed render.')
    except ImportError:
        result['pillow'] = dict(status='SKIP', reason='not installed')
    try:
        from psd_tools import PSDImage
        psd = PSDImage.open(path)
        assert len(psd) == len(layers)
        for external, expected in zip(reversed(list(psd)), layers):
            assert normalized_name(external.name) == expected['name']
            assert external.layer_id == expected['id']
            assert external.opacity == expected['opacity']
            assert external.visible == expected['visible']
            assert external.bbox == (expected['left'], expected['top'], expected['left']+expected['width'], expected['top']+expected['height'])
            assert external.topil().tobytes() == expected['rgba'], 'External layer RGBA mismatch'
        expected = composite(width, height, layers, white_matte=False)
        forced = psd.composite(force=True).tobytes()
        errors = [abs(a-b) for a,b in zip(forced, expected)]
        assert max(errors, default=0) <= 1, 'Independent recomposition mismatch'
        result['psd_tools'] = dict(status='PASS', version=importlib.metadata.version('psd-tools'),
            layer_pixels='byte-exact', metadata='IDs, Unicode, order, bounds, opacity, visibility',
            max_recomposite_byte_error=max(errors, default=0))
    except ImportError:
        result['psd_tools'] = dict(status='SKIP', reason='not installed')
    if shutil.which('identify'):
        p = subprocess.run(['identify', str(path)], capture_output=True, text=True, check=True)
        result['imagemagick'] = dict(status='PASS', output=p.stdout.strip(),
            note='Recognition/decoding only; no Photoshop parity claim')
    else:
        result['imagemagick'] = dict(status='SKIP', reason='not installed')
    return result


def run_csharp(dotnet, paths, out):
    """Compile the actual Unity-independent C# source, not a reimplementation, using an existing SDK."""
    source = Path(__file__).resolve().parents[1]/'Runtime/Core/Psd'
    with tempfile.TemporaryDirectory(prefix='dot-psd-bridge-') as temp:
        temp = Path(temp)
        (temp/'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>')
        (temp/'Bridge.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><LangVersion>6</LangVersion>'
            '</PropertyGroup><ItemGroup><Compile Include="'+xml_escape(str(source/'*.cs'))+'" />'
            '</ItemGroup></Project>')
        (temp/'Program.cs').write_text(r'''using System; using System.IO; using Dot.TexturePainter.Core.Psd;
class Program {
 static int Main(string[] args) {
  foreach (string path in args) {
   byte[] bytes = File.ReadAllBytes(path); var read = PsdCodec.Read(bytes);
   Console.WriteLine(Path.GetFileName(path) + ": " + read.Mode);
   foreach(var diagnostic in read.Diagnostics) Console.WriteLine(diagnostic);
   if (path.Contains("unknown")) {
    if (read.Mode != PsdCompatibilityMode.PreserveOnly || read.Document != null) return 1;
    if (Convert.ToBase64String(bytes) != Convert.ToBase64String(read.CopyOriginalBytes())) return 2;
    try { PsdCodec.WriteEdited(read, new PsdDocument()); return 3; } catch (InvalidOperationException) { }
   } else {
    if (read.Mode != PsdCompatibilityMode.EditableRaster) return 4;
    byte[] output = PsdCodec.WriteEdited(read, read.Document);
    var check = PsdCodec.Read(output);
    if (check.Mode != PsdCompatibilityMode.EditableRaster) return 5;
    for (int i=0;i<read.Document.Layers.Count;i++) {
     if (Convert.ToBase64String(check.Document.Layers[i].PixelsRgba) !=
         Convert.ToBase64String(read.Document.Layers[i].PixelsRgba)) return 6;
    }
    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path), "csharp-" + Path.GetFileName(path)), output);
   }
  }
  return 0;
 }
}
''')
        env = os.environ.copy()
        env.update(DOTNET_CLI_HOME=str(temp/'home'), HOME=str(temp/'home'),
            DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_GENERATE_ASPNET_CERTIFICATE='false',
            DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1')
        command = [str(dotnet), 'run', '--configuration', 'Release', '--project', str(temp/'Bridge.csproj'), '--'] + [str(p.resolve()) for p in paths]
        executed = subprocess.run(command, env=env, text=True, capture_output=True, timeout=120, check=True)
        (out/'csharp-bridge.log').write_text(executed.stdout+executed.stderr, encoding='utf-8')
    return [p.with_name('csharp-'+p.name) for p in paths if 'unknown' not in p.name]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, default=Path('Validation~/psd-spike'))
    parser.add_argument('--csharp', type=Path, nargs='*', default=[])
    parser.add_argument('--dotnet', type=Path, help='Existing .NET SDK executable; compile/run the actual C# codec and inspect its outputs')
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    layers = fixture_layers()
    reports, fixture_paths = [], []
    for name, rle, unknown in [('independent-raw.psd', False, False),
                               ('independent-rle.psd', True, False),
                               ('unknown-record.psd', False, True)]:
        path = args.out/name
        path.write_bytes(write_fixture(4, 3, layers, rle, unknown))
        reports.append(validate(path))
        fixture_paths.append(path)
    generated = run_csharp(args.dotnet, fixture_paths, args.out) if args.dotnet else []
    for path in generated + args.csharp:
        reports.append(validate(path))
    report = dict(scope='Independent Python format/interoperability spike, plus explicit C#-generated inputs when provided',
        csharp_execution=('PASS: actual C# source compiled as C#6, independently generated raw/RLE imported, guarded rewritten outputs reimported, raster bytes retained; unknown-source editing rejected.' if args.dotnet else 'Not run by this invocation. See the separate .NET/Unity test report.'),
        photoshop_csp='NOT RUN: no licensed external application fixtures or runtime available', results=reports)
    (args.out/'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
