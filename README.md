# YoluPainter Unity Bridge

スタンドアロン版 [YoluPainter](https://github.com/YozoraKurage/YoluPainter) と Unity エディターをつなぐパッケージです（Live Link）。Unity のシーンのモデルをスタンドアロン版で開き、スタンドアロン版で書き出したテクスチャを Unity のマテリアルに適用します。描くのはスタンドアロン版です。

- Unity 2022.3 以降 / パッケージ名 `net.yozolab.yolupainter` / エディター専用 / 依存パッケージなし

## 導入

1. 次のどれかで入れる
   - **VCC（VRChat Creator Companion）**: Settings ▸ Packages ▸ Add Repository で `https://vpm.yozolab.net/index.json` を追加し、プロジェクトの
     Manage Project で YoluPainter を追加する
   - **Release の .zip**: GitHub の Releases から `net.yozolab.yolupainter.<版>.zip` を取り、プロジェクトの `Packages/net.yozolab.yolupainter/` を作って
     その中へ展開する（`package.json` が `Packages/net.yozolab.yolupainter/` の直下に来る形）
   - **Package Manager**: `+` → Add package from git URL で `https://github.com/YozoraKurage/YoluPainter-UnityBridge.git`
2. スタンドアロン版の YoluPainter を入れる。Windows ではインストーラーが書いた場所を自動で使う。ほかの OS では
   Edit ▸ Preferences ▸ YoluPainter で実行ファイルの場所を指定する（空のまま開くと、初回にファイルを選ぶダイアログが出る）

## 使い方

1. Hierarchy で GameObject を右クリック ▸ **Open in YoluPainter**（GameObject メニューにもある）。`YozoLab ▸ YoluPainter ▸ Live Link` のウィンドウのボタンでも同じ
2. スタンドアロン版が開く（起動していなければ起動する）。もう一度押すと、ポーズ・BlendShape・マテリアルの値を送り直す
3. スタンドアロン版で描いて書き出すと、Unity が PNG をインポートし、どのマテリアルのどのテクスチャが替わるかの一覧を出す。
   **Apply All**・**Apply Selected**・**Don't Apply** から選ぶ（適用は Undo で戻せる）

送るのは FBX とテクスチャのファイルの場所・ポーズ・BlendShape・マテリアルの値だけで、メッシュや画素は送りません（スタンドアロン版がファイルを自分で読む）。
FBX から来ていないメッシュは送らず、ウィンドウに理由を出します。シーン・FBX・マテリアル・テクスチャは、マテリアルへの適用を選んだときと、
書き出した PNG のインポート設定のほかは変えません。仕組みの詳しい説明は [`Editor/LiveLink/README.md`](Editor/LiveLink/README.md)、
受け渡しのファイルの形はスタンドアロン版のリポジトリの [docs/LIVELINK.md](https://github.com/YozoraKurage/YoluPainter/blob/main/docs/LIVELINK.md) にあります。

## 設定

Edit ▸ Preferences ▸ YoluPainter（この PC の自分だけの設定）:

- **言語**: 自動（OS の言語）・日本語・English
- **YoluPainter の実行ファイル**: Windows ではインストーラーが書いた場所が先
- **書き出し先**: スタンドアロン版が書き出し先として示すフォルダの元（既定は `Assets/YoluPainter`。その下に対象の名前のフォルダ）

## 0.4 以前から上げる方へ

0.5.0 で、Unity の中で描く機能を外しました。描く・`.ylp` を開く・保存する・PSD の読み書き・メッシュマップのベイクは、スタンドアロン版で行います。

- **上げる前に、Unity の YoluPainter のウィンドウで描きかけの作業を `.ylp` に保存してください。** `Library/YoluPainter` の復旧の checkpoint は、
  このパッケージでは開けなくなります（ファイルは消さずに残ります）
- `YozoLab ▸ YoluPainter (Prototype)` のウィンドウ、Project Settings ▸ YoluPainter、ブラシ・アセットのパネルは無くなりました。
  ウィンドウの配置にその場所が残っていても、Unity が知らないウィンドウとして外すだけです
- Assets に置いた `.ylp`・`.ylsmart` は、Unity では普通のファイルになります（サムネイル・情報・ダブルクリックで開く動きは無くなる）。
  ファイルはそのままなので、`.ylp` はスタンドアロン版で開けます。マテリアルに割り当てた PNG もそのままです（`.ylp` はテクスチャを出していなかったので、参照は切れません）
- 前の版の設定（`ProjectSettings/Packages/net.yozolab.yolupainter/`、`UserSettings/YoluPainter/` の取り込んだブラシ・自分の置き場）は、使わなくなりますが消しません。要らなければ手で消せます
- 依存パッケージ（Burst・Mathematics）を外しました。ほかに使うパッケージが無ければ、Package Manager がプロジェクトから外します

## 確かめた範囲

試験（EditMode）は Linux の Unity 2022.3.22f1 で回しています。Windows・macOS の Unity での確かめは少ないので、おかしな所があれば知らせてください。

## 文書

- [`Editor/LiveLink/README.md`](Editor/LiveLink/README.md) — Live Link の Unity の側の仕組み
- [docs/LIVELINK.md](https://github.com/YozoraKurage/YoluPainter/blob/main/docs/LIVELINK.md)・[docs/YLP_FORMAT.md](https://github.com/YozoraKurage/YoluPainter/blob/main/docs/YLP_FORMAT.md)（スタンドアロン版のリポジトリ） — 受け渡しのファイルと `.ylp` の形式
- [`Documentation~/THIRD_PARTY.md`](Documentation~/THIRD_PARTY.md) — 第三者の物について

## お問い合わせ

お問い合わせ、開発に関する質問、機能の要望は [Discord](https://discord.gg/c8NNfhJ94J) へどうぞ。
