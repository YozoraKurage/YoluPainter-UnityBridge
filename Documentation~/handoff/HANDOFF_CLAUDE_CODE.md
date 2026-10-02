# Claude Code への引き継ぎ

## そのまま渡せる依頼文

このフォルダーの Unity テクスチャペイント拡張を引き継いでください。対象は Unity 2022.3 です。そちらには Unity Editor があるので、まず実際の Editor で package import、C# API/asmdef コンパイル、EditMode tests、shader compile、2D/3D 描画の smoke test を行い、失敗を修正してください。その後、同梱した22ページ仕様の残りを段階的に実装してください。

最初に CLAUDE.md、docs/STATUS.md、docs/VALIDATION.md、spec/Unity_Texture_Paint_Spec_v0_1.md を読んでください。仕様書は全スコープ、STATUS は現在との差分です。全機能を一度に質問して止まらず、実環境で調べられる patch/OS/pipeline/lilToon/入力情報は調べ、安全な作業用プロジェクトで進めてください。既存の制作プロジェクトを upgrade したり、元シーン/材質/テクスチャを変更したりしないでください。

現時点は疎タイル正本・Undo・安全保存・限定PSDの実C#テストに成功していますが、Unity実行は未検証です。GPUブラシのshaderは試験用であり、通常描画の正本はCPUです。DAGバリデーターがあることをGenerator/Filter/Anchor完成と扱わないでください。未対応PSDは原本保持/編集禁止にし、調整・テキスト・スマートオブジェクト等を黙って消さないでください。

最終目標から、Mask/Filter/Generator/Anchor、mesh map、マルチチャンネル一筆、ポーズ/BlendShape、編集可能な3Dパス、lilToon出力、PSDの型別互換などを削除しないでください。区切りごとに実装したもの・実行したテスト・まだ未検証のものを簡潔に報告し、STATUS/VALIDATIONも更新してください。

## 現状の要点

- コード: editor-only UPM package `com.dot.texture-painter` version `0.1.0-preview.1`
- メニュー: `Window → dot → Texture Painter (Prototype)`
- 2D/3Dは同じレイヤーとチャンネルを編集。3Dはstatic readable meshのみ
- `Demo cube` は六つのUV島を持つtool-owned mesh。元シーンへ生成しない
- 1本のIMGUI入力経路。圧力曲線、size/opacity/flow割当、erase、stroke単位Undo/Redo/Cancel
- CPU source brush + GPU tile compositor。GPUメモリへ全レイヤー全面を常駐させない
- Normal/Multiply/Screen は native encoded-sample compositing。PSD上書き互換ではない
- Color/Roughness/Metallic/Height/Normal/Emission を別sourceに保持。現UIは選択した1chのみの一筆
- 保存はnative sparse sourceと対応ch PSDを世代化。古いcurrentを最後まで守る
- PSDはbounded RGB8 normal rasterの限定subset。未知featureはPreserveOnly、原バイト保持、edit拒否
- DependencyGraphは型、channel semantic、同Texture Set、順序、循環、利用中削除を検証する純C#基盤

## 実行済みの証拠

2026-10-02、隔離Linux環境/.NET SDK8.0.425での結果。

- 純C# production core/PSD NUnit **59/59 PASS**。内訳31 core +28 PSD。500 mutation入力はこのPSD試験の一部
- 描画→Undo→native保存→再読込→PSD、保存中断/改変等の結合 **8/8 PASS**
- 実ソースの .NET Standard2.1 compile **0 warning /0 error**
- 実SurfaceGeometry +数学アダプター **19/19 PASS**。Unity runtimeによる試験ではない
- C#8 syntax **20 source files /0 error**。Unity API型解決/Shaderコンパイルは含まない
- 実C#コーデック出力をindependent byte parser、Pillow、psd-tools、ImageMagickで確認
- fixtureのレイヤーRGBAはbyte-exact。独立合成との差は最大1byte。Photoshop/CSPの認証結果ではない
- 未対応PSDの保護/書戻し拒否は試験済み

ログ/XML/PSD fixtureはvalidation/にあります。`docs/PSD_COMPATIBILITY.md` にE/P/R/Tと試験条件があります。

## 最優先: 実Unityゲートを閉じる

1. インストール済Editorの正確な2022.3 patch、OS、graphicsAPI、pipeline、color spaceを記録。ライセンス済であることを既存環境から確認し、アカウント/キーをチャットへ出さない
2. 使い捨ての検証用projectを用意し、Package Manager → Add package from disk → `Packages/com.dot.texture-painter/package.json`
3. 実compile/importエラーを先に修正。特にPreviewRenderUtility、Unsupported.useScriptableRenderPipeline、IMGUI入力、RenderTexture/CopyTexture、asmdef/testablesの版差を確認
4. project manifestのtestablesへ `com.dot.texture-painter` を追加し、Unity Test FrameworkでEditModeを実行。Unity-only preview3件を含めて実行
5. graphics有効のEditorで `Window → dot → Run GPU brush parity probe`。画像上下、RGBA8丸め、色空間、GPU対象とCPU sourceの誤差を記録
6. Windowで256/512から開始。2D点/線/erase/Undo/Redo/Cancel、Demo cube、seam/裏面/近接衣服/slot境界、両canvas同期を確認
7. focus loss、ウィンドウ外release、例外、reload、Play切替、close/reopen、複数Windowでinput/resourceが残らないか確認
8. Save As→Open、recovery、PSD保護、外部改変拒否、保存失敗の実OS動作を確認
9. 元scene dirty状態・mesh/material/texture値/asset hashが変わらないことを測る
10. 実ペンタブで筆圧0〜1、低速/高速/HiDPI/複数monitorを確認。Unity eventの取得成功と低遅延品質を分ける
11. lilToonがある場合はinstalled version/variantを調べる。現previewはneutral opaque shaderなのでlilToon対応とはまだ言わない
12. 4K sparse/dense/channels/layers workloadで実RSS/VRAM/peak save/median-p95-p99 latencyを記録。小さなcore試験から性能を推測しない

## ローカルで再現するコマンド

.NET8 SDKがPATH上にある場合:

```sh
./tools/run_all_portable_tests.sh
```

SDKを明示する場合:

```sh
DOTNET=/absolute/path/to/dotnet ./tools/run_all_portable_tests.sh
```

スクリプトはwritableな一時CLI/NuGet cacheを使用します。WindowsではWSL/Git Bashで実行するか、csprojごとにdotnet run/buildを実行できます。

```sh
dotnet run --project tools/CoreHarness/CoreHarness.csproj -c Release
dotnet run --project tools/CoreNUnit/CoreNUnit.csproj -c Release -- --result=validation/core-psd-nunit.xml
dotnet build tools/CoreCompile/CoreCompile.csproj -c Release
dotnet run --project tools/SyntaxCheck/SyntaxCheck.csproj -- Packages
```

GeometryHarnessはrun.shで最新の純幾何test本文を生成します。stub/adaptersをUnity API確認に流用しないでください。

PSD oracleはPython3で:

```sh
python tools/psd_spike.py --out validation/psd-spike --dotnet /absolute/path/to/dotnet
```

Pillow/psd-toolsがなければ外部decoder検証はskipになります。使用した版はPillow11.3.0、psd-tools1.10.9。runtime依存ではありません。`report.json` のskipを成功扱いしないでください。

Unity batch例（実行ファイル/パスは実環境に合わせる）:

```sh
Unity -batchmode -projectPath /path/to/test-project -runTests -testPlatform EditMode -testResults /path/to/editmode-results.xml -logFile /path/to/editor.log
```

GPU/描画確認にはgraphicsを有効にし、`-nographics`を合格根拠にしないでください。

## 既知の制約と修正候補

- UIは未実Unity実行。まずそれを確かめること。構文/一部API-shape mock compileは代替にならない
- GPU compositorはrevision変更時にoccupied/previous tilesを再合成する。真のdirty DAG schedulerとtile upload cacheではない
- color profile/linear-gamma displayの実機確認前。native sample-spaceをPSD見た目完全一致と呼ばない
- CPU brushはsource/rollback/history予算がある。payload数値はCLR overhead、realRSS、GPUを含まない
- sparse sourceは256MiB、active rollback64MiB、history64MiBが既定。PSDもsource/output/decoded予算がある
- explicit saveは同期でinputを止める。snapshot immutable化はあるが非同期GPU save pipelineではない
- native recovery15秒。世代の自動pruning/永続操作journal/disk spillは未実装。長時間の容量増加に注意
- File.Flush(true)は実行するがdirectory fsync/power loss保証は未試験。外部app全体をlockできるわけではない
- source layerのblendがNormal以外ならPSD projectionを拒否し、確認後native-only保存。勝手にflattenしない
- off-canvas PSD pixelsはnative importを拒否。v1 nativeは0..canvas内のタイルだけ
- PSD changesの差分/取り込み/merge UIは未実装。現在は改変検出して通常saveを止め、Save As/Openで分離
- out-of-range UV、skinnedや省略geometryのある不完全snapshotはsurface paintを止める
- neutral opaque preview。cutout/transparent/displacement、exact lilToon parityはない
- specialized normal-vector blendはない。Normal channelの保存をNormal制作完成と扱わない
- DAGは保存された制作graphでも評価engineでもない。Mask/Fill/Filterの次段コードはこのbundleにない

## 継続実装の順序案

実Unity G0/G1の修正を優先し、それと並行して純C#で確認可能な次段を進める。

1. Mask/Fill/主要native Filterをsource model、Undo、保存schema、CPU/GPU表示、UIまで縦につなぐ
2. マルチチャンネル一筆/チャンネル別設定、選択/塗潰し/gradient等の日常制作
3. native effect graph/Anchor段階出力/mesh map来歴/Generatorの評価とstale/循環/削除/並べ替え
4. skinned preview、pose/BlendShape、model fingerprint、editable surface path/再結合
5. 版固定lilToon adapter、PSD native adjustment型別の6工程往復、より広い色/bit/PSD機能
6. GPU正本brush/async readback、dirty scheduler、BVH/Jobs/Burst、disk cache/予算、性能測定を段階ごとに並行改善

ユーザーはSubstance型のgenerator/filter/anchor/mask、編集可能3Dパス、PSD hybrid projectを求めています。土台だけを最終完成として終了しないでください。
