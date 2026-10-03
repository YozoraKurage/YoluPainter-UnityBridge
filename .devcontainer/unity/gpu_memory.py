#!/usr/bin/env python3
"""WSL の DXCore と D3DKMT による読み取り専用の GPU メモリ計測。

ABI の出典: Microsoft DirectX-Headers/include/directx/dxcore_interface.h、
WSL2-Linux-Kernel/include/uapi/misc/d3dkmthk.h、WinSDK/shared/d3dkmthk.h。
QueryStatistics の全体セグメント統計を使い、プロセス予算を全体と取り違えない。
対応範囲は Linux x86_64 の WSL ABI（sizeof(QueryStatistics) == 0x328）。
"""

import argparse
import ctypes as C
import datetime
import json
import platform
import struct
import sys
import time
import uuid

U32 = C.c_uint32
PTR = C.c_void_p
SIZE = C.c_size_t
STATUS = C.c_int32


class Guid(C.Structure):
    _fields_ = [("a", U32), ("b", C.c_uint16), ("c", C.c_uint16), ("d", C.c_uint8 * 8)]

    def __init__(self, value):
        super().__init__()
        C.memmove(C.byref(self), uuid.UUID(value).bytes_le, 16)


class QueryStatistics(C.Structure):
    _fields_ = [("kind", U32), ("luid", U32 * 2), ("process", C.c_uint64),
                ("result", C.c_ubyte * 0x308), ("details", U32 * 2)]


def com(obj, slot, result_type, arg_types, *args):
    address = C.cast(obj, C.POINTER(C.POINTER(PTR))).contents[slot]
    return C.CFUNCTYPE(result_type, PTR, *arg_types)(address)(obj, *args)


def checked(code, operation):
    if code < 0:
        raise RuntimeError(f"{operation}: 0x{code & 0xffffffff:08x}")


class Adapter:
    def __init__(self, lib, pointer, index):
        self.lib, self.pointer, self.index = lib, pointer, index
        size = SIZE()
        checked(com(pointer, 7, STATUS, [U32, PTR], 2, C.byref(size)), "GetPropertySize")
        description = C.create_string_buffer(size.value)
        checked(com(pointer, 6, STATUS, [U32, SIZE, PTR], 2, size.value, description), "GetProperty")
        self.name = description.value.decode("utf-8", errors="replace")
        self.luid = (U32 * 2)()
        checked(com(pointer, 6, STATUS, [U32, SIZE, PTR], 0, 8, C.byref(self.luid)), "InstanceLuid")
        self.dxcore_global_usage_supported = bool(com(pointer, 8, C.c_bool, [U32], 2))

    def query(self, kind, segment=0, pid=0):
        query = QueryStatistics()
        query.kind, query.process, query.details[0] = kind, pid, segment
        query.luid[:] = self.luid[:]
        checked(self.lib.D3DKMTQueryStatistics(C.byref(query)), "D3DKMTQueryStatistics")
        return bytes(query.result)

    def sample(self, pid=0):
        # AdapterInformation.NbSegments、SegmentInformation の先頭と Aperture。
        count = struct.unpack_from("<I", self.query(0))[0]
        if not 0 < count <= 64:
            raise RuntimeError(f"セグメント数が ABI の想定外: {count}")
        totals = {name: {"committed_bytes": 0, "resident_bytes": 0}
                  for name in ("dedicated", "shared")}
        segments = []
        for index in range(count):
            data = self.query(3, index)
            limit, committed, resident = struct.unpack_from("<QQQ", data)
            aperture = struct.unpack_from("<I", data, 40)[0]
            if aperture not in (0, 1):
                raise RuntimeError("Aperture が ABI の想定外")
            name = "shared" if aperture else "dedicated"
            if pid:
                # ProcessSegmentInformation は BytesCommitted を返す。常駐量ではない。
                committed = struct.unpack_from("<Q", self.query(4, index, pid))[0]
                resident = None
            totals[name]["committed_bytes"] += committed
            if resident is None:
                totals[name]["resident_bytes"] = None
            else:
                totals[name]["resident_bytes"] += resident
            segments.append({"index": index, "kind": name, "commit_limit_bytes": limit,
                             "committed_bytes": committed, "resident_bytes": resident})
        return {"adapter_index": self.index, "adapter": self.name,
                "luid": f"{self.luid[1]:08x}:{self.luid[0]:08x}",
                "scope": "process" if pid else "adapter", "pid": pid or None,
                "backend": "D3DKMTQueryStatistics",
                "dxcore_global_usage_supported": self.dxcore_global_usage_supported,
                **totals, "segments": segments}

    def close(self):
        if self.pointer:
            com(self.pointer, 2, U32, [])
            self.pointer = None


class Monitor:
    def __init__(self, library="/usr/lib/wsl/lib/libdxcore.so"):
        if platform.machine() != "x86_64" or C.sizeof(QueryStatistics) != 0x328:
            raise RuntimeError("WSL x86_64 の ABI が必要です")
        self.adapters = []
        self.lib = C.CDLL(library)
        self.lib.DXCoreCreateAdapterFactory.argtypes = [C.POINTER(Guid), C.POINTER(PTR)]
        self.lib.DXCoreCreateAdapterFactory.restype = STATUS
        self.lib.D3DKMTQueryStatistics.argtypes = [C.POINTER(QueryStatistics)]
        self.lib.D3DKMTQueryStatistics.restype = STATUS
        factory, listing = PTR(), PTR()
        try:
            checked(self.lib.DXCoreCreateAdapterFactory(
                C.byref(Guid("78ee5945-c36e-4b13-a669-005dd11c0f06")), C.byref(factory)), "DXCoreCreateAdapterFactory")
            checked(com(factory, 3, STATUS, [U32, PTR, PTR, PTR], 1,
                C.byref(Guid("0c9ece4d-2f6e-4f01-8c96-e89e331b47b1")),
                C.byref(Guid("526c7776-40e9-459b-b711-f32ad76dfc28")), C.byref(listing)), "CreateAdapterList")
            for index in range(com(listing, 4, U32, [])):
                pointer = PTR()
                checked(com(listing, 3, STATUS, [U32, PTR, PTR], index,
                    C.byref(Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e")), C.byref(pointer)), "GetAdapter")
                try:
                    self.adapters.append(Adapter(self.lib, pointer, index))
                except Exception:
                    com(pointer, 2, U32, [])
                    raise
        except Exception:
            self.close()
            raise
        finally:
            if listing:
                com(listing, 2, U32, [])
            if factory:
                com(factory, 2, U32, [])
        if not self.adapters:
            raise RuntimeError("D3D12 のアダプターが見つかりません")

    def close(self):
        for adapter in self.adapters:
            adapter.close()

    def sample(self, name=None, pid=0):
        selected = [a for a in self.adapters if name is None or name.lower() in a.name.lower()]
        if not selected:
            raise RuntimeError("指定されたアダプターが見つかりません")
        return {"time_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "adapters": [a.sample(pid) for a in selected]}


def main():
    parser = argparse.ArgumentParser(description="WSL の GPU メモリ使用量（アダプター全体、バイト／GiB）")
    parser.add_argument("--json", action="store_true", help="JSON を出す。未対応はエラーで終了し 0 にしない")
    parser.add_argument("--adapter", help="アダプター名の部分一致（省略時はすべて）")
    parser.add_argument("--pid", type=int, default=0, help="Linux PID の確保量。全体常駐量とは区別する")
    parser.add_argument("--watch", type=float, help="秒間隔で繰り返す（Ctrl-C で終了）")
    parser.add_argument("--shared-resident-bytes", action="store_true", help="単一アダプターの共有常駐バイト数だけ")
    parser.add_argument("--restart-limit-mib", type=int,
                        help="全体共有常駐量が閾値以上で、この PID の共有確保が 128 MiB 以上なら理由を出す")
    args = parser.parse_args()
    if args.pid < 0 or (args.watch is not None and args.watch <= 0):
        parser.error("PID は 0 以上、間隔は正の数を指定してください")
    if args.shared_resident_bytes and args.pid:
        parser.error("プロセスの確保量は常駐量として出せません")
    if args.restart_limit_mib is not None and (args.restart_limit_mib <= 0 or not args.pid or args.watch):
        parser.error("再起動判断には正の閾値と PID が必要です（繰り返し不可）")
    monitor = None
    try:
        monitor = Monitor()
        if args.restart_limit_mib is not None:
            total = monitor.sample(args.adapter)["adapters"]
            process = monitor.sample(args.adapter, args.pid)["adapters"]
            if len(total) != 1:
                raise RuntimeError("再起動判断は --adapter で単一アダプターを選んでください")
            shared = total[0]["shared"]["resident_bytes"]
            own = process[0]["shared"]["committed_bytes"]
            if shared < args.restart_limit_mib * 2**20 or own < 128 * 2**20:
                return 1
            print(f"GPU の全体共有常駐量 {shared / 2**30:.2f} GiB が閾値以上"
                  f"（この台の共有確保 {own / 2**20:.0f} MiB）")
            return 0
        while True:
            sample = monitor.sample(args.adapter, args.pid)
            if args.shared_resident_bytes:
                if len(sample["adapters"]) != 1:
                    raise RuntimeError("--adapter で単一アダプターを選んでください")
                print(sample["adapters"][0]["shared"]["resident_bytes"], flush=True)
            elif args.json:
                print(json.dumps(sample, ensure_ascii=False), flush=True)
            else:
                print(sample["time_utc"], flush=True)
                for adapter in sample["adapters"]:
                    print(f"{adapter['adapter']} ({adapter['scope']}, {adapter['backend']})")
                    for key, label in [("dedicated", "専用"), ("shared", "共有")]:
                        values = adapter[key]
                        resident = values["resident_bytes"]
                        text = "取得対象外" if resident is None else f"{resident / 2**30:.3f} GiB"
                        print(f"  {label}: 常駐 {text}, 確保 {values['committed_bytes'] / 2**30:.3f} GiB", flush=True)
            if args.watch is None:
                break
            time.sleep(args.watch)
    except (OSError, RuntimeError) as error:
        print(f"GPU メモリを取得できません: {error}", file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        return 0
    finally:
        if monitor:
            monitor.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
