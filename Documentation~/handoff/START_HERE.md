# Unity Texture Painter — Claude Code 引き継ぎ

このフォルダーを Unity Editor を使用できる Claude Code の作業ディレクトリへ渡してください。

1. **HANDOFF_CLAUDE_CODE.md** の「そのまま渡せる依頼文」を読んで開始
2. **CLAUDE.md** がこのリポジトリでの作業契約
3. **spec/Unity_Texture_Paint_Spec_v0_1.md** が最終要望の仕様書（DOCX版も同梱）
4. 現物は **Packages/com.dot.texture-painter/**。Unity Package Manager の Add package from disk で package.json を選択
5. 実装範囲は **docs/STATUS.md**、検証結果は **docs/VALIDATION.md**

対象 Unity は **2022.3**。正確な patch、OS、パイプライン、ペンタブ、lilToon の版はこれから実環境で確認します。

この引き継ぎは、テスト済みG0/G1チェックポイントを固定したものです。仕様全体の完成版ではありません。次段のMask/Fill/Filter実装は始めておらず、未検証の機能途中コードは含めていません。

ソースにPCの接続情報、アカウント、資格情報、SDKバイナリは含みません。PSDは検証用にゼロから生成したfixtureです。
