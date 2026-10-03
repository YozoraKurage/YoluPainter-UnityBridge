# YoluPainter — リポジトリルール

Unity 2022.3 向けのエディタ専用パッケージ `net.yozolab.yolupainter`（名前空間 `Yozolab.YoluPainter`）。
専用 EditorWindow の中で 2D/3D のテクスチャ制作を行う拡張を、仕様書 v0.1 の全スコープへ
段階的に実装する。現在は G0/G1 のプロトタイプで、完成品として扱わない。
コードは Codex から引き継いだもの（原文は `Documentation~/handoff/`）。開発環境
（DevContainer・テストデーモン・リリース用 Actions）は DaerD から切り出したもの。

## 読む順序

1. `Documentation~/STATUS.md`（仕様全範囲に対する現状）と `Documentation~/VALIDATION.md`（実行した検証）
2. `Documentation~/spec/Unity_Texture_Paint_Spec_v0_1.md`（最終要望の仕様書。docx 版も同梱）
3. `Documentation~/ARCHITECTURE.md`、`YLP_FORMAT.md`（.ylp の形式と版・移行の決まり）、`PSD_COMPATIBILITY.md`、`TESTING.md`
4. `Runtime/Core/README.md`、`Editor/Preview/README.md`
5. エージェント（指揮役・作業者）として動くときは `Documentation~/ORCHESTRATION.md`（worktree、進捗メモ `.agent/progress.md`、
   一時停止、再起動・異常終了からの再開）

## 不変条件

- 元シーン、元マテリアル、元テクスチャ、Prefab、import 設定を描画中に変更しない
- ユーザーの Prefab/GameObject を Instantiate してからスクリプトを止める方式に戻さない。
  現行は Mesh だけの snapshot と Renderer/Transform の再構成
- フォーカス喪失、Escape、リロード、Play 移行、例外でストロークを取り残さない
- ネイティブの straight RGBA8 ソースと透明画素の RGB を守る。GPU プレビューや低精度
  キャッシュを保存の唯一の正本にしない
- GPU の src/dst 同一 read-write は禁止。メモリ予算・readback の寿命・世代を守る
- PSD の未対応情報を黙って捨てない。PreserveOnly に落ちた原本への編集書き戻しは禁止
- シェーダーが似ているだけで lilToon へ自動適用しない。実際の version / variant /
  property / 設定を確認する
- 名前だけで外部 PSD レイヤーを対応付けたり、パス/生成レイヤーと外部画素を勝手に
  上書きし合ったりしない
- 保存は最後の 1 回の置換で確定させる契約を守る（.ylp は検証済みの一時ファイルからの置換、
  復旧 checkpoint は `current` を最後に置換）。直前の版・旧世代を無断で削除しない（.ylp の
  バックアップの整理は、利用者が設定で選んだ数を超えた分だけ）
- 新機能には回帰テスト・保存復元・Undo/取消・型/予算拒否のテストも付ける
- Unity の EditMode、llvmpipe での GPU、コンテナの実 GPU（OpenGL 経由）、Windows の D3D11、
  Photoshop/CSP 実機の結果を混同しない
- Generator/Filter/Anchor/Mask、編集可能 3D パス、マルチチャンネル、PSD 調整は中核要望。
  未実装だからといってスコープから削除しない

IMGUI/UI Toolkit の構成や GPU/CPU 分担の改善などは、仕様の意味を保ち、計測・テストの
根拠を残して進めてよい。新しいライセンス、資格情報、外部公開、元アセットへ適用する
操作はユーザーの意思を確認する。

## 節目ごとに STATUS と VALIDATION を更新する

作業の区切りで `Documentation~/STATUS.md` と `Documentation~/VALIDATION.md` を更新する。
コードがあるだけで tested にしない。失敗した検証も消さず、原因と修正後の再試験を残す。

## ユーザー固有データを git 履歴に絶対に残さない

ユーザーから提供されるデータ（アバター名・キャラクター名・実プロジェクトのパス・
アセットや生成 cs の実物・エラーログに含まれるパス断片など）は、**いかなる形でも
git の履歴に入れてはならない**。コミットされるファイルだけでなく、コミットメッセージの
本文・テストコード・コメント・ドキュメントの例も対象。

- ユーザー提供の実ファイルは `temp~/` に置く（.gitignore 済み）。末尾のチルダは
  Unity にインポートさせないための慣習で、これが無いとテストプロジェクトの
  コンパイル対象に入ってしまう。原則 temp~/ 以外にコピーしないが、**解析のために
  Unity に読ませる必要があるときだけ**、テストプロジェクト（名前付きボリューム、
  どの git にも属さない）の `Assets/ZZ_UserAssets/` へ複製してよい。解析が済んだら消す。
- バグ報告のパスやログを再現テスト・コミットメッセージに書くときは、必ず汎用名に
  置換する（例: `Assets/Chara/Foo.mat`）。構造だけを保ち、固有名詞は残さない。
- **コミット前に必ず確認する**: ステージ内容とメッセージに対して固有名詞
  （アバター名・ユーザー名・実パス）を grep する。会話中に登場した固有名詞は
  すべて検索対象。
- 漏れて入れてしまった場合: 未 push なら履歴を書き換えて除去し、バックアップ ref
  （refs/original）も削除する。push 済みなら直ちにユーザーに報告して指示を仰ぐ。
- `temp~/` には Unity のライセンスファイル（.alf / .ulf）も置かれる。これも絶対に
  コミットしない。

## コミットメッセージは設計判断の一次資料

設計判断の記録は doc コメントではなくコミットメッセージが持つ。コードコメントは
「今どうであるか」を、コミットメッセージは「なぜそうなったか」を書く場所と使い分ける。

- 件名は conventional prefix（feat / fix / refactor / chore）+ 変更が使う人に
  もたらす意味を一文で。機能領域が特定できる語を件名か本文冒頭に一つは入れる。
- 本文には「何をしたか」ではなく**なぜこの形か**を書く: 動機になった問題、
  選んだ機構、その帰結。差分を読めば分かることは繰り返さない。
- **採らなかった案と捨てた理由を書く。** 既存の方式を置き換える・巻き戻す
  コミットは、捨てた理由と**失った能力**を必ず書く。
- **トレードオフと保証の射程を書く**: 何を犠牲にしたか、保証しないこと、
  意図的に対応しなかったケースとその理由。
- 本文中の数値の主張は、テストや計測の実際の範囲と一致させる。
- 互換性への影響（保存済みデータ・生成物・既存アセットの扱い）を書く。
  「既存には影響なし」も明示する価値のある主張。

## ブランチとリリース

- `main` へ PR をマージすると `.github/workflows/release.yml` が PR ラベル
  （major / minor / patch）で `package.json` の version を上げ、VPM 用の
  .zip / .unitypackage を Release として公開する。
- 作業ブランチ名はリリース予定のバージョン（例: `0.0.0`）。

## テストの走らせ方

この DevContainer には Unity 2022.3.22f1 が入っている（ベースは game-ci の
Editor イメージ）。テストはすべて EditMode テスト（`Tests/Editor`）で、コンテナ内で完結する。
詳細は `Documentation~/TESTING.md`。

```
.devcontainer/unity/test-daemon.sh start --batch-gl   # 常駐 Unity（シェーダー・GPU が正しく動く）
.devcontainer/unity/test-daemon.sh restart            # GUI モードに切り替え（EditorWindow の操作用）
.devcontainer/unity/run-tests.sh                      # 全件
.devcontainer/unity/run-tests.sh --filter 'Yozolab.YoluPainter.Tests.FooTests'   # 絞り込み(完全名)
```

- **この devcontainer の GUI モードではシェーダーが壊れる**（組み込みの `HLSLSupport.cginc`
  すら開けず、全シェーダーがマゼンタ。2026-10-02 実測、原因は Unity 内部で未特定）。
  `--batch-gl`（`-batchmode` だけ付けて `-nographics` は付けず xvfb 上の OpenGL）では
  正常。なので **GPU・シェーダーの検証は batch-gl、ウィンドウ操作の検証は GUI** で回す。
  GpuTests は壊れたエディタでスキップ、WindowTests は batchmode でスキップされる。
  区切りでは両モードで全件を回し、合わせて全テストが実行されたことを確かめる。
- `--batch-gl` の起動時に、GUI モードで壊れた状態で取り込まれたシェーダーを自動で
  取り込み直す。それでもシェーダーがおかしいときはテストプロジェクトの `Library` を
  消して作り直す（`rm -rf /home/node/unity-testproject/Library`、数分かかる）。
- グラフィックスはホストの実 GPU（RTX 3070）。WSL2 の `/dev/dxg` を、devcontainer に同梱した
  Mesa 24.2.8 の d3d12 ドライバで使う（OpenGL 4.6、Unity から CopyTexture・compute・
  AsyncGPUReadback が使える）。設定は `common.sh` が `/dev/dxg` を見て自動で行う。
  `gpu-check.sh [--unity]` で確認、`YOLUPAINTER_GPU=0` で llvmpipe、`YOLUPAINTER_MESA=system`
  でシステムの Mesa 23.2（OpenGL 4.2、CopyTexture・compute 無し）に戻せる。
  Linux 版 Unity なので API は OpenGL のまま。GPU の結果は「コンテナの実 GPU で一致」で
  あって、Windows の D3D11 での確認ではない。
- コンテナでは `sudo` がパスワードなしで使える（apt で道具を足すなど）。
- **テストデーモン**: 常駐 Unity を立てると run-tests.sh は自動でそちらへ依頼される
  （死んでいればコールドへ自動フォールバック。コールドは `-nographics` なので GPU と
  ウィンドウのテストはスキップ）。パッケージを出し入れしたら `restart`。
- **テストの台とキュー**: 常駐 Unity は台 0（今までのプロジェクト）に加えて、台 1（batch-gl）と
  台 2（GUI）を `runners.sh setup` / `runners.sh start` で並べられる。run-tests.sh は空いている台へ
  振り分け、台 1 以上ではソースを台の中の写しへ同期してから回す（`--mode gui`・`--sha HEAD`・
  `--source <worktree>`・`--both`）。詳細は `Documentation~/TESTING.md` の「テストの台（runners）とキュー」。
- **常駐 Unity を直接使う** — `.devcontainer/unity/unity-do.sh`:
  ```
  unity-do.sh run -e 'return AssetDatabase.FindAssets("t:Material").Length;'
  unity-do.sh run snippet.cs         # メソッド本体。先頭の using 行は使える。Debug.Log は結果に入る
  unity-do.sh console error warning  # コンソール（--limit N / --full / --clear）
  unity-do.sh compile [--force]      # 再コンパイルしてエラー本文（--force は全アセンブリ）
  ```
  `run` はドメインリロード無しで 2〜3 秒。スニペットのアセンブリ名は `YoluPainterSnippet`
  で、Editor アセンブリが `InternalsVisibleTo` しているので internal 型も触れる。
  任意の `static string Method(string)` は `exec-method.sh` で叩ける。
- 出力はサマリと失敗内容だけ。Unity の生ログは `$YOLUPAINTER_UNITY_PROJECT/Logs/`
  （デーモンは `daemon.log`）。ログを丸ごと読み込まないこと（数万行ある）。
- 終了コード: 0 = 全件成功 / 1 = テスト失敗 / 3 = コンパイルエラー等で結果が
  出なかった / 4 = ライセンス未設定 / 5 = デーモンが止まっていた
  （`TestDaemon/trace.log` と `Logs/daemon.prev.log` を見てから `test-daemon.sh restart`）。
- テストプロジェクトは `/home/node/unity-testproject`（名前付きボリューム）。
  このリポジトリを `file:/workspace` のローカルパッケージとして参照している。
  lilToon 2.3.4 を VPM で入れてある（`add-vpm.sh --list`）。パッケージは依存していない。
- 初回だけ Unity Personal ライセンスの有効化が要る:
  `.devcontainer/unity/activate-license.sh --status` で状態を確認できる。
  未設定なら手順が表示されるが、ブラウザ操作を含むのでユーザーに依頼すること。
