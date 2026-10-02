# YoluPainter — リポジトリルール

Unity 2022.3 向けのパッケージ `net.yozolab.yolupainter`（名前空間 `Yozolab.YoluPainter`）。
開発環境（DevContainer・テストデーモン・リリース用 Actions）は DaerD から切り出したもの。

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
Editor イメージ）。EditMode テストはコンテナ内で完結する。

```
.devcontainer/unity/run-tests.sh                      # 全件
.devcontainer/unity/run-tests.sh --filter 'Yozolab.YoluPainter.Tests.FooTests'   # 絞り込み(完全名)
.devcontainer/unity/test-daemon.sh start              # 常駐 Unity(推奨・下記)
```

- テストプロジェクトは `/home/node/unity-testproject`（名前付きボリューム）。
  このリポジトリを `file:/workspace` のローカルパッケージとして参照している
  ので、リポジトリ側には Library/ も Assets/ も生成されない。テストアセンブリは
  manifest の `testables` で拾われる。
- **テストデーモン**: `test-daemon.sh start` で常駐 Unity を立てると、以後の
  run-tests.sh は自動でそちらへ依頼される（死んでいればコールドへ自動フォールバック）。
  フィルタ実行が数秒になる（コールドは毎回数分の起動費を払う）。パッケージを
  出し入れしたら `restart`。常駐中は同じプロジェクトを別の Unity で開けない。
  常駐は GUI モード（xvfb 上）が既定、`start --batch` で batchmode。
- **常駐 Unity を直接使う** — `.devcontainer/unity/unity-do.sh`:
  ```
  unity-do.sh run -e 'return AssetDatabase.FindAssets("t:Material").Length;'
  unity-do.sh run snippet.cs         # メソッド本体。先頭の using 行は使える。Debug.Log は結果に入る
  unity-do.sh console error warning  # コンソール（--limit N / --full / --clear）
  unity-do.sh compile [--force]      # 再コンパイルしてエラー本文（--force は全アセンブリ）
  ```
  `run` はドメインリロード無しで 2〜3 秒。パッケージの `internal` 型は見えない
  （リフレクションで触るか、パッケージ側で `InternalsVisibleTo("YoluPainterSnippet")`）。
  任意の `static string Method(string)` は `exec-method.sh` で叩ける。
- 出力はサマリと失敗内容だけ。Unity の生ログは
  `$YOLUPAINTER_UNITY_PROJECT/Logs/tests.log`。ログを丸ごと読み込まないこと（数万行ある）。
- 終了コード: 0 = 全件成功 / 1 = テスト失敗 / 3 = コンパイルエラー等で結果が
  出なかった / 4 = ライセンス未設定 / 5 = デーモンが止まっていた
  （`TestDaemon/trace.log` と `Logs/daemon.prev.log` を見てから `test-daemon.sh restart`）。
- VPM パッケージ（VRChat SDK など）は `.devcontainer/unity/add-vpm.sh` で
  テストプロジェクトへ出し入れできる（`--list` で確認）。
- 初回だけ Unity Personal ライセンスの有効化が要る:
  `.devcontainer/unity/activate-license.sh --status` で状態を確認できる。
  未設定なら手順が表示されるが、ブラウザ操作を含むのでユーザーに依頼すること。
