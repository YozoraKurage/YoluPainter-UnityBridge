#!/usr/bin/env python3
"""同梱の .NET / Roslyn / NUnit だけで Core の試験を組んで回す。"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET


class Arguments(argparse.ArgumentParser):
    def error(self, message):
        self.exit(3, f"引数エラー: {message}\n")


def fingerprint(paths):
    digest = hashlib.sha256()
    for path in sorted(set(paths)):
        digest.update(str(path).encode())
        digest.update(b"\0")
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def copy_build(source, destination):
    for name in ("Yozolab.YoluPainter.Core.dll", "Yozolab.YoluPainter.Tests.dll", "NUnitRunner.dll", "inventory.json"):
        shutil.copy2(source / name, destination / name)


def main():
    here = Path(__file__).resolve().parent
    parser = Arguments(description=__doc__, epilog=(
        "Unity の統合試験の代わりにはなりません。終了コード: 0 成功 / 1 試験失敗 / 3 結果なし。"
        " Burst は読み込まず管理コードを使います。--list で除外理由とクラス一覧を確認してください。"))
    parser.add_argument("--source", type=Path, default=here.parent.parent,
                        help="試験する worktree（既定: このスクリプトのリポジトリ）")
    parser.add_argument("--filter", default="", help="試験の完全名に対する .NET の正規表現")
    parser.add_argument("--list", action="store_true", help="組んで発見したクラス・件数・除外理由を表示（実行しない）")
    parser.add_argument("--output", type=Path, help="結果 XML・一覧 JSON・ログを残す新しいフォルダ（既定: 一時フォルダ）")
    parser.add_argument("--no-cache", action="store_true", help="ビルド結果を再利用せず組み直す（計測用）")
    args = parser.parse_args()
    source = args.source.resolve()
    for relative in ("Runtime/Core", "Tests/Editor"):
        if not (source / relative).is_dir():
            raise RuntimeError(f"ソースが無い: {source / relative}")

    unity = Path(os.environ.get("YOLUPAINTER_CORE_UNITY_DATA", "/opt/unity/Editor/Data"))
    dotnet = unity / "NetCoreRuntime/dotnet"
    roslyn = unity / "DotNetSdkRoslyn"
    config = json.loads((roslyn / "csc.runtimeconfig.json").read_text())
    version = config["runtimeOptions"]["framework"]["version"]
    runtime = unity / "NetCoreRuntime/shared/Microsoft.NETCore.App" / version
    mono = unity / "MonoBleedingEdge/bin/mono"
    api = unity / "UnityReferenceAssemblies/unity-4.8-api"
    if not dotnet.is_file() or not runtime.is_dir():
        raise RuntimeError(f"同梱の .NET が無い: {dotnet} / {runtime}")
    if not mono.is_file() or not api.is_dir():
        raise RuntimeError(f"同梱 Mono / API 参照が無い: {mono} / {api}")
    if os.environ.get("YOLUPAINTER_CORE_NUNIT"):
        nunit = Path(os.environ["YOLUPAINTER_CORE_NUNIT"]).resolve()
    else:
        project = Path(os.environ.get("YOLUPAINTER_UNITY_PROJECT", "/home/node/unity-testproject"))
        candidates = sorted((project / "Library/PackageCache").glob("com.unity.ext.nunit*/**/nunit.framework.dll"))
        if not candidates:
            raise RuntimeError("既存の nunit.framework.dll が無い。YOLUPAINTER_CORE_NUNIT でローカル DLL を指定してください。")
        nunit = candidates[-1]
    if not nunit.is_file():
        raise RuntimeError(f"NUnit の DLL が無い: {nunit}")

    temporary = None
    if args.output:
        out = args.output.resolve()
        out.mkdir(parents=True, exist_ok=False)  # 前の結果を今回の成功と取り違えない。
    else:
        temporary = tempfile.TemporaryDirectory(prefix="yolupainter-core-tests-")
        out = Path(temporary.name)
    started = time.perf_counter()
    try:
        shutil.copy2(nunit, out / "nunit.framework.dll")
        # ソース・参照 DLL・実行器の内容が一つでも変われば別のビルドになる。結果は毎回実行して得る。
        cache_root = Path(os.environ.get("YOLUPAINTER_CORE_CACHE", str(here.parent.parent / "temp~/core-tests-cache")))
        inputs = [here / "core-tests.py", here / "core-tests.sh", roslyn / "csc.runtimeconfig.json", nunit]
        inputs += list((here / "core-tests").glob("*.cs"))
        inputs += list((source / "Runtime/Core").rglob("*.cs")) + list((source / "Tests/Editor").rglob("*.cs"))
        inputs += list(api.rglob("*.dll")) + list(roslyn.glob("*.dll"))
        key = fingerprint(inputs)
        cached = cache_root / key
        cache_hit = not args.no_cache and (cached / "complete").is_file()
        if cache_hit:
            copy_build(cached, out)
        else:
            build(source, out, runtime, roslyn, dotnet, api, here)
            # ビルド中に編集された木を、最初に読んだ内容のキャッシュとして残さない。
            after = [p for p in inputs if "/Runtime/Core/" not in str(p) and "/Tests/Editor/" not in str(p)]
            after += list((source / "Runtime/Core").rglob("*.cs")) + list((source / "Tests/Editor").rglob("*.cs"))
            if fingerprint(after) != key:
                raise RuntimeError("コンパイル中にソースが変わりました。もう一度実行してください。")
            if not args.no_cache:
                cache_root.mkdir(parents=True, exist_ok=True)
                with tempfile.TemporaryDirectory(prefix="build-", dir=cache_root) as staging:
                    stage = Path(staging)
                    copy_build(out, stage)
                    (stage / "complete").touch()
                    try:
                        stage.rename(cached)
                    except OSError:
                        if not (cached / "complete").is_file():
                            raise
        bootstrap = time.perf_counter() - started
        print(f"コンパイル: CoreCLR {version} / 実行: 同梱 Mono / NUnit: {nunit}\nCore の下調べ: {source}（Burst なし・管理コード）", flush=True)
        inventory = json.loads((out / "inventory.json").read_text())
        print(f"ビルド: {'再利用' if cache_hit else '組み直し'} / 準備 {bootstrap:.3f}s / 元のコンパイル・選別 {inventory['buildSeconds']:.3f}s", flush=True)
        print(f"試験ファイル: 採用 {len(inventory['included'])} / 除外 {len(inventory['excluded'])}（全 {inventory['candidateFiles']}）。除外理由は --list。", flush=True)
        if args.list:
            print("\n採用したファイル:")
            for path in inventory["included"]:
                print(f"  {path}")
            print("\n除外したファイルとコンパイル診断（各ファイル先頭 3 件、全診断は inventory.json）:")
            for path, reasons in inventory["excluded"].items():
                print(f"  {path}")
                for reason in reasons[:3]:
                    print(f"    {reason}")
                if len(reasons) > 3:
                    print(f"    …ほか {len(reasons) - 3} 件")
            print("\nBurst・登録カーネルに関係するファイル（字句から抽出。反射の完全検出は保証しません）:")
            for path in inventory["managedDifferences"]:
                print(f"  {path}（{'採用' if path in inventory['included'] else '除外'}）")
            sys.stdout.flush()
        command = [str(mono), str(out / "NUnitRunner.dll"), args.filter, "list" if args.list else "run"]
        result = subprocess.run(command, cwd=out)
        if (out / "discovery.xml").is_file():
            cases = list(ET.parse(out / "discovery.xml").iter("test-case"))
            classes = {}
            for case in cases:
                name = case.get("classname", "不明")
                classes[name] = classes.get(name, 0) + 1
            inventory.update(classes=classes, discovered=len(cases), cases=[dict(c.attrib) for c in cases])
            if (out / "selection.xml").is_file():
                inventory["selected"] = int(ET.parse(out / "selection.xml").getroot().get("selected"))
            (out / "inventory.json").write_text(json.dumps(inventory, ensure_ascii=False, indent=2))
        elapsed = time.perf_counter() - started
        print(f"準備 {bootstrap:.3f}s / 合計 {elapsed:.3f}s", flush=True)
        if args.output:
            print(f"記録: {out}")
            (out / "wall-time.json").write_text(json.dumps({"cacheHit": cache_hit, "prepareSeconds": bootstrap, "totalSeconds": elapsed}))
        return result.returncode if result.returncode in (0, 1, 3) else 3
    finally:
        if temporary:
            temporary.cleanup()


def build(source, out, runtime, roslyn, dotnet, api, here):
    for name in ("Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll"):
        shutil.copy2(roslyn / name, out / name)
    version = runtime.name
    (out / "CoreTests.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
        "tfm": "net6.0", "framework": {"name": "Microsoft.NETCore.App", "version": version}}}))
    references = sorted(runtime.glob("*.dll")) + [out / name for name in (
        "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll")]
    rsp = ["-nologo", "-target:exe", "-langversion:9.0", "-optimize+", "-nostdlib+",
           f'-out:"{out / "CoreTests.dll"}"']
    rsp += [f'-r:"{p}"' for p in references]
    rsp.append(f'"{here / "core-tests/Program.cs"}"')
    (out / "bootstrap.rsp").write_text("\n".join(rsp) + "\n")
    command = [str(dotnet), "exec", str(roslyn / "csc.dll"), "/noconfig", f"@{out / 'bootstrap.rsp'}"]
    compiled = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    (out / "bootstrap.log").write_text(compiled.stdout)
    if compiled.returncode:
        print(compiled.stdout, file=sys.stderr)
        raise RuntimeError("コンパイル用実行器を組めませんでした。")
    command = [str(dotnet), str(out / "CoreTests.dll"), str(source), str(out), str(api),
               str(here / "core-tests/NUnitRunner.cs")]
    result = subprocess.run(command, cwd=out)
    if result.returncode:
        raise RuntimeError("Core と試験を組めませんでした。")

if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        print(f"Core 試験の結果なし: {error}", file=sys.stderr)
        sys.exit(3)
    except KeyboardInterrupt:
        print("Core 試験を中断しました。結果は未確定です。", file=sys.stderr)
        sys.exit(3)
