#!/usr/bin/env python3
"""台の写し（pkg）に、GPU のメッシュマップのベイクを YOLUPAINTER_GPU_BAKE_OFF で止める 1 行が無ければ足す（2026-10-04 のつなぎ）。

0b00b1e より前の枝のコードには、GpuMeshBakeRayTracer.Unavailable() に環境変数の切り替えが無い。その枝の試験を台で回すと GPU のベイクが走り、
GPU のデバイスが消えて台が落ちる。担当の作業ツリーは変えず、台に写した写しだけに足す（次の同期で元に戻り、また足す）。
全部の枝が 0b00b1e を取り込んだら要らない。  使い方: guard-gpu-bake.py <台の pkg>
"""
import sys
from pathlib import Path

path = Path(sys.argv[1]) / 'Editor' / 'Gpu' / 'MeshBakeGpu.cs'
if not path.is_file():
    sys.exit(0)
text = path.read_text(encoding='utf-8')
if 'YOLUPAINTER_GPU_BAKE_OFF' in text:
    sys.exit(0)
head = 'public static string Unavailable()\n        {\n'
if head not in text:
    print('    注意: MeshBakeGpu.cs の Unavailable() が見つからず、GPU のベイクを止める行を足せなかった', file=sys.stderr)
    sys.exit(0)
line = ('            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("YOLUPAINTER_GPU_BAKE_OFF"))) '
        'return "GPU baking is turned off in this environment (YOLUPAINTER_GPU_BAKE_OFF)"; // guard-gpu-bake.py が台の写しにだけ足した\n')
path.write_text(text.replace(head, head + line, 1), encoding='utf-8')
print('    古い枝なので、台の写しにだけ GPU のベイクを止める 1 行を足した（guard-gpu-bake.py）')
