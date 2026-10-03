# Unity向けテクスチャペイント仕様の調査補助

確認日: 2026-10-02。一次資料はAdobe公式／CELSYS公式。実装・SDK・PSDライブラリを検証した報告ではない。以下は「確認済み製品の挙動」と「本製品への設計提案」を明確に分ける。特定バージョンで機能が存在することは、このUnity製品やPSDライブラリで実装済みであることを意味しない。

## 1. 結論

1. **PSDはラスターレイヤーの交換・保存基盤、独自プロジェクトは編集の正本**とする。ただしPSD由来の編集可能ラスターレイヤーは、明示的に受理した外部PSDリビジョンを正本とするハイブリッド方式がよい。二つの正本が同じプロパティを同時に所有しない。
2. **PSDのバイト保持・外観再現・意味を保った再編集は別能力**。未知ブロックをコピーできても、見た目やPhotoshop再編集まで保証できない。互換レポートで別々に示す。
3. テキスト／スマートオブジェクト／未対応調整は非破壊の保護対象。原本と未知データを保持し、未対応機能を黙ってラスタライズして上書きしない。
4. 全調整レイヤーのPhotoshop同等再現を約束しない。ネイティブで編集可能な調整とPSDへの意味的マッピングを型・版・色空間・ビット深度ごとに宣言する。
5. 外部更新の自動検知と**差分の自動適用は別**。3-way比較で候補を出すが、未保存ローカル変更がある状態は確認なしでマージしない。生成レイヤーに外部描画されたら必ず所有権の選択が必要。
6. PSDと独自プロジェクトの複数ファイル保存は単純な同時atomic renameでは原子的にならない。リビジョン付きマニフェストと回復可能なトランザクションを要求する。

## 2. PSDの公式仕様から言える範囲

Adobeの公開仕様はファイル構造を示し、データの解釈方法までは説明しないと明記する。PSD/PSBには画像リソース、レイヤー／マスク情報、画像データがあり、追加レイヤーデータもある。仕様上、8/16/32bit、複数カラーモード、レイヤーID、文字、調整、スマートオブジェクト等の記録が存在する。合成画像は「互換性を優先」設定により存在しない場合がある。したがって、拡張子がPSDであることだけで表示用の完成画像や編集可否を判断しない。

出典: [Adobe Photoshop File Formats Specification, November 2019](https://www.adobe.com/devnet-apps/photoshop/fileformatashtml/)

**重要な留保:** 公開仕様が2019年版であるため、その後のPhotoshop機能や全Descriptorの意味を網羅するとの仮定は置かない。未知キー、未知バージョン、プラグインデータ、既知型内の未知フィールドを通常系として扱う。

## 3. 要求能力マトリクス（設計提案、未実装）

凡例: **E** = 本製品で意味を理解して再編集、**P** = 元の意味データを保護保持、**V** = キャッシュ等の表示のみ、**R** = 読み取り専用／変換コピーのみ。Pは外観保証ではない。最終的なE宣言には選定バックエンドの検証とPhotoshop/CSP往復試験が必要。

| 要素 | 推奨契約 | PSD保存条件・危険点 |
|---|---|---|
| RGB 8bitラスターレイヤー | Eの基準対象 | RGBA、透明度、座標、範囲外画素、名前、非表示、ロック、順序を保持。透明画素のRGBやマスク外画素を勝手に消さない |
| RGB/Gray 16bitラスターレイヤー | Eの必須拡張対象、別試験群 | 読込・合成・編集・保存のどこでも8bitへ暗黙量子化しない。GPU作業精度とPSD入出力精度は別に宣言 |
| グループ／階層 | E | 入れ子、開閉、順序、グループ不透明度、独立合成／通過の差を保存。単なるフォルダーUIに縮退させない |
| ラスターマスク | E | 白黒／アルファの意味、反転、無効、位置、リンク、密度・ぼかし等を対応項目ごとに判定。未対応パラメータはP |
| ベクターマスク／シェイプ | P+V、実装した型のみE | パスをラスターマスクと同一視しない。変形・アンチエイリアス・フィルルールが違えば外観一致は未保証 |
| クリッピング | Eの基準対象 | 直下レイヤーのアルファだけの簡易実装をPS互換とは呼ばない。基底、連続クリップ連鎖、グループ、合成オプションを試験 |
| 通常・乗算・スクリーン等の合成 | 検証済みモードだけE | 数式が同名でも合成色空間、丸め、透明度、FillとOpacity、グループ順序で差が出る |
| Dissolve/HSL系/特殊合成 | 型ごとにEまたはP+V | 乱数再現、境界値、8/16bit差を別試験。対応していないモードをNormalとして黙って保存しない |
| Blend If、Knockout、高度なレイヤースタイル | 原則P+V/R | 下位レイヤーの編集が見た目を変えるため、当該レイヤー無編集でも合成を安全に再計算できないことがある |
| 調整レイヤー | 対応型・バージョンのみE | パラメータを維持するPSDノードで保存。画像へ焼いた結果を「編集可能調整」と呼ばない。詳細は次節 |
| 単色／グラデーション／パターン塗り | 調整とは別のFillノード | 塗り設定、変換、パターン資産、マスクを保持。未対応はP+V |
| テキスト | P+Vが初期の安全契約 | フォント、シェーピング、縦書き、言語、ワープ、段落、版差の解釈なしに再組版しない。代替フォントで確定上書きしない |
| 埋込スマートオブジェクト | P+V | 埋込ソース本体、識別子、配置・ワープ・関連情報を保護。ラスタープレビューとソースは同じものではない |
| リンクスマートオブジェクト | P+V | 外部依存、リンク先変更、欠落、相対/絶対パス、更新時刻、入れ子依存を検出。無断でリンク先読込・更新・再パッケージしない |
| Smart Filter、3D/video、アートボードなど | P+V/R | 全体構成との依存を解釈できない場合は元PSD上書きを停止。単にブロックが読めたことを対応と表示しない |
| ICC/RGB色管理 | Eの基準対象 | 入力プロファイル、作業空間、表示変換、出力プロファイルを分離。未タグ画像には選択または明示した既定値 |
| CMYK/Lab/Indexed/Duotone等 | 初期Rまたは変換コピー | 元の色モードを維持した保存を保証できない場合、RGBへ黙って変換しない |
| 32bit HDR／PSB／巨大PSD | 能力別に明示 | 保存対応を推測しない。サイズ上限・メモリ上限・圧縮形式をプリフライトし、対応範囲外はR |
| 未知の文書/レイヤー/リソースレコード | P | ブロック長、パディング、配置、参照関係を保持。既知ノード内の未知フィールドも捨てない。安全性判定できない構造変更をブロック |
| 合成画像／サムネイル | 現在revisionの整合した表示だけ | 元合成画像を残したまま「更新済み」とすることは禁止。新しい合成を正しく作れない場合は外観保証不可を表示し安全保存へ |

### 3.1 合成・ビット深度について一次資料で確認したこと

- Photoshopの16bit画像はレイヤーと調整レイヤーに対応する。しかし「Photoshopが16bitを扱う」ことは採用PSD実装の16bit対応を保証しない。[Adobe bit depth](https://helpx.adobe.com/uk/photoshop/using/bit-depth.html)
- PhotoshopのグループはPass Throughと、それ以外の独立した合成で下位／外部レイヤーへの影響範囲が違う。クリップ合成オプションも存在する。[Adobe layer opacity and blending](https://helpx.adobe.com/nz/photoshop/using/layer-opacity-blending.html)
- クリッピングは連続した層の基底に依存する。「Blend Clipped Layers As Group」の設定も見た目に関わる。[Adobe clipping masks](https://helpx.adobe.com/photoshop/desktop/create-masks/layer-masks/create-and-manage-clipping-masks.html)
- Photoshopの32bit対応合成モードは限定される。8/16bitでは負の演算結果の扱いなども重要。[Adobe blending mode descriptions](https://helpx.adobe.com/in/photoshop/desktop/repair-retouch/adjust-light-tone/blending-mode-descriptions.html)

## 4. 編集可能調整レイヤー

### 4.1 型一覧と仕様上の区別

Adobe公式の調整カタログ: Brightness/Contrast、Levels、Curves、Exposure、Hue/Saturation、Color Balance、Black & White、Photo Filter、Channel Mixer、Color Lookup、Selective Color、Invert、Posterize、Threshold、Gradient Map、Color & Vibrance。単色、グラデーション、パターンはFillとして別管理する。

出典: [Adobe work with adjustment layers](https://helpx.adobe.com/photoshop/desktop/create-manage-layers/color-adjustment-fill-layers/work-with-adjustment-and-fill-layers.html)

Photoshopの調整は元の画素を変えずに別レイヤーへ指示を保存し、その下への処理になる。[Adobe adjustment/fill overview](https://helpx.adobe.com/sg/photoshop/desktop/create-manage-layers/color-adjustment-fill-layers/adjustment-and-fill-layers-overview.html)

本製品の要件表は以下の4列を**調整タイプごと**に持つべきである:

1. ネイティブUIのパラメータ再編集可否
2. 本製品の2D/3D表示での適用可否と対象チャンネル
3. 既存PSDからの意味的読込可否
4. Photoshop/CSPへの編集可能な調整としての書出／再読込可否

### 4.2 推奨実装順の提案（完成範囲の無断縮小ではない）

- 基準検証セット: 明るさ・コントラスト、レベル補正、トーンカーブ、色相・彩度、反転、グラデーションマップ
- 次の検証セット: 露光量、カラーバランス、ポスタリゼーション、2値化、自然な彩度、白黒、チャンネルミキサー、写真フィルター、色の置き換えではなくSelective Color、Color Lookup
- どのセットも正確なPS互換が未完成なら「ネイティブ調整として再編集可」「PSDではP/またはユーザーが選んだ焼き込みコピー」を分ける。未対応調整を消す選択肢を既定にしない。

### 4.3 外観一致の難所（設計上の検証事項）

同じ名前・似たスライダーだけでは一致しない。Legacy/新方式、RGBチャンネル別と合成カーブ、色相範囲、輝度の定義、プロファイル、ガンマ、クリッピング・グループ範囲、調整マスク、パラメータ外の既定値、Gradient Map補間、Color LookupのLUT資産、整数丸めをfixtureに含める。Roughness等のデータチャンネルに色相・彩度を自動適用しない。適用可能な調整だけを選べるようにし、Base Color向け色調整と数値マップ向けカーブ／レンジ変換を区別する。

## 5. テキスト／スマートオブジェクトの安全保持

Adobe公式ではスマートオブジェクトは埋込と外部リンクを区別し、元データと非破壊変形／フィルターを維持する。通常の直接ペイントには制約がある。[Adobe Smart Objects](https://helpx.adobe.com/vn_en/photoshop/using/create-smart-objects.html)

リンク元は変更・欠落し得る。Photoshopもリンク直接参照と入れ子参照を同じようには更新しない。[Adobe update linked Smart Objects](https://helpx.adobe.com/ca/photoshop/desktop/create-manage-layers/smart-objects/update-linked-smart-objects.html)

**本製品の提案:**

- 「保護された外部機能」ノードとして明示し、種類、元アプリ、表示キャッシュのrevision、保存の制約、欠落依存をレイヤーパネルに示す
- ストローク先に選ぶと、原本を保持した新規ラスターレイヤーへの描画を案内。編集不能な元レイヤーへ勝手に描かない
- 原本の元バイト列または復元可能な保護コピー、未知レコード、関連リソースを独自プロジェクトに保存。ファイルサイズや秘密情報の可能性も考え、第三者への公開は通常の資産共有確認に従う
- バイト保持は意味依存を修復しない。レイヤーID参照、共有ソース、文書単位リソースとの対応が不明な場合は並べ替え・複製・削除・サイズ変更等を安全とは判断しない
- 当該レイヤーの画素キャッシュが利用できても、元ファイルを編集後に下位画素との合成を再現できるとは限らない
- 再合成が検証できない文書は、原本PSD上書きをブロックし、独自プロジェクト保存／元PSD保持／別名の互換PSDコピーを選べるようにする。必要ならPhotoshopで原本を更新して再読込
- 互換PSDコピーでラスタライズする場合も、何をどこまで焼き込み、どの機能が再編集不能になるかを項目別に確認する。元のPSDと独自プロジェクトの保護情報は残す
- リンク資産に対するネットワークアクセス、無制限再帰、外部アプリの自動起動、絶対パス書換えを行わない。パス検証、サイズ・再帰制限、任意コード非実行を読み込み契約に含める

## 6. 独立ペイントプロジェクトとPSDの分担

### 6.1 独自形式で保存する編集正本（SPP風の体験、SPP互換ではない）

- プロジェクトUUID、schema/engine version、同一commit revision、カラー設定
- Mesh/Renderer/Material/TextureSetの安定識別子、元アセット参照、トポロジー・UV・ポーズ識別／ハッシュ
- レイヤーUUID、型、親、順序、チャンネル別値／有効・不透明度・合成、マスク、依存グラフ
- ブラシ設定のスナップショット、使用資産とハッシュ、入力イベント・確定ストローク、乱数seed、筆圧曲線、ストローク順序
- surface3D pathの制御点・接線・点ごとの圧力、オブジェクト／三角形／重心座標による表面アンカー、閉路、モード、幅・繰返し・継ぎ目等のパラメータ
- 調整ノードの型とパラメータ、選択、定規／対称、必要な編集状態
- PSD binding、前回受理したbaseline、PSD IDとの対応、raw保護情報、依存資産
- 復旧ジャーナルと検証済みcheckpoint。キャッシュは再生成可能データとして区別

独自拡張子を使い、.sppの読み書きやSubstance資産互換を含むと誤認させない。Adobeの.sppは公式APIで扱われるPainterプロジェクトであり、この提案は同一ファイル仕様ではない。[Adobe project module](https://experienceleague.adobe.com/en/docs/substance-3d-dev/painter-python/api/substancepainter-package/project)

### 6.2 PSD側

PSDは外部で編集できるラスタースタックと検証済みPSDネイティブ調整を受け持つ。パスから生成するレイヤーには、本製品で編集できるパス本体とPSDに渡すラスタープロキシの関係を付ける。PSDの画像として編集されたものから、元のsurface3D pathを逆算できるとは約束しない。

**マルチチャンネルの提案:** TextureSet×チャンネルごとにPSDを対応させる方式が最も明確。Base Color用RGB PSD、データマップ用Gray/RGB PSDをマニフェストで同じレイヤーUUIDへ束ねる。単一PSD内にチャンネル別グループを置く交換方式も可能だが、全グループを重ねた合成画像が最終マテリアルにならないため閲覧・再読込規則を明示する。PSDのMultichannelカラーモードをPBRチャンネルの意味管理と同一視しない。

Painterの各レイヤーは複数チャンネルを持ち、表示中のチャンネルとは独立に有効チャンネルへ描画し、チャンネルごとに不透明度・合成モードを持つ。[Adobe layer stack](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/interface/layer-stack/layer-stack)

## 7. PSD再読込／競合と安定ID

### 7.1 差分モデル

- Base = 前回受理・保存したPSDスナップショット
- Local = 現在のネイティブ作業状態とそこから出力されるPSD表現
- Incoming = 更新された外部PSDの安定した読込スナップショット
- 原本・Local・Incomingをそれぞれ保持してから判定する。ファイル監視の通知だけで即読込しない。書込途中、rename連打、同一サイズ・mtime、削除再作成、共有フォルダー遅延に対応
- 文書全体ハッシュだけでなくレイヤーの型、親、順序、属性、マスク、チャンネル画素、調整、unknown payload、サイズ／プロファイルを比較する

### 7.2 ID契約

独自UUIDを第一識別子にし、PSDのレイヤーIDとの対応を保持する。外部アプリがIDを維持することを前提にしない。重複、欠落、再割当てを検出。IDがなくなった場合は型・親・内容指紋等から候補を提示し、確定できなければユーザーが対応付ける。レイヤー名・インデックスだけで同一性を決めない。PSDの同じレイヤーIDを持つ別ファイルまで同一扱いしない。

### 7.3 必須の競合UI

| 状態 | 挙動 |
|---|---|
| 外部だけ変更、Local clean | 差分要約付きの再読込確認。設定によりプレビュー自動更新は可能でも復旧元を保持 |
| Localだけ変更 | 通常保存。置換直前にも外部revisionが変わっていないか再確認 |
| 双方が同じレイヤーを変更 | Base/Local/Incoming比較。ローカル優先／外部優先／別レイヤーとして保存／保留を明示 |
| 双方が別レイヤーを変更 | 自動マージ候補を提示できるが、ユーザーの「競合時に聞く」要件を守り無言で適用しない |
| 外部がパス生成レイヤーを描き換え | パスを正として再生成、外部ラスタを別層に取込、外部結果を採用し生成を凍結、の所有権選択。パス情報は残す |
| 削除／改名／順序／グループ変更 | 外部の新規・削除をLocalへ自動反映せず影響を示す。マスクや調整の範囲が変わる構造変更は高優先競合 |
| サイズ、UV、チャンネル、プロファイル変更 | 通常の画素マージを停止。変換・再投影の独立プレビューと確認が必要 |
| 未対応要素／新しい未知レコード | 対応レベルを下げ、原本保存と安全な別名保存へ。未知情報を消して続行しない |

Undoは受理した再読込を一つの操作として復元可能にする。却下した外部revisionは次の別revisionまで繰り返し通知しない。保存失敗・競合中もブラシ作業の独自プロジェクト復旧保存は可能にする。

## 8. PSD＋プロジェクトの保存トランザクション

これは製品への設計要求であり、PSD標準が定義する機能ではない。

1. 保存予定revisionと依存資産一覧を確定。現行Baseの内容ハッシュと保存先revisionを検証
2. 同じファイルシステム上の一時領域へPSD群、独自プロジェクト、マニフェストをステージング。既存ファイルは触らない
3. 全ファイルを再読込して構造、サイズ、チェックサム、UUID対応、revision、利用可能なら合成比較を検証
4. 未完了commitを記すジャーナルと旧版復元情報を確保。データをflushした後にコミット
5. コンテナ/世代ディレクトリ＋小さなcurrentポインターを正式プロジェクトの単一commit点にすると整合性を設計しやすい。外部編集用の通常PSDパスも必要なら、その発行は別の状態として追跡
6. 固定パスのPSD複数個＋別プロジェクトを置換する場合、真の単一atomic操作とは呼ばない。途中停止を検知し、起動時に全体を旧版か新版へ復元／完了。部分成功を成功表示しない
7. 置換直前の外部変更はCAS相当で検知して保存を止める。外部アプリがファイルロックやrename制約を持つ場合も失敗を回復可能にする
8. PSD発行失敗でも、ネイティブ編集が安全に保存されているなら「プロジェクト保存済み・PSD未更新」と分けて伝える

独自プロジェクトもPSDも世代バックアップを保持。単一プロセスのUndoだけを復旧機構にしない。

## 9. Paint Along Path / Filled / Ribbonの一次資料確認

| 名称 | 導入 | 役割 | 必須区別 |
|---|---|---|---|
| Paint Along Path | Painter 9.0、2023-06-20 | 表面上の再編集可能な曲線に沿ってブラシストローク。Eraser/Smudgeも対象 | 繰返しスタンプで描く経路。閉じた内部を塗るFilledとは違う |
| Filled path | Painter 11.0、2025-03-11 | 表面上の閉じた領域に均一の塗りを作る | 線幅を太くしたパスではない |
| Ribbon path | Painter 11.1、2025-11-18 | 模様画像を曲線に合わせて変形し、繰返し／伸長する | 独立したブラシスタンプではなく連続する帯・patch。端部と角の扱いがある |

出典: [Painter 9.0 release notes](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/release-notes/old-versions/version-9-0)、[Painter 11.0 release notes](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/release-notes/old-versions/version-11-0)、[Painter 11.1 release notes](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/release-notes/version-11-1)

現行公式Path概要では表面の3D空間だけが対象で、UV空間やスクリーン空間への作成をサポートしないと明記されている。頂点再編集、再選択、点ごとの圧力、対称、表示制御等を持つ。[Path overview](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/painting/path-tools/path)

Filledは均一な塗り形状、Ribbonはstart/end、角、tile/stretch、aspect、自己重なりのチャンネル合成等を持つ。Ribbonの角にはMiter/Round/Bevel/Cutがあり、ストレッチか縦横比維持かを選ぶ。[Filled path](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/painting/path-tools/filled-path)、[Ribbon path](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/painting/path-tools/ribbon-tool)

### 9.1 Unity製品への推奨契約

- surface3Dを主要パスタイプにする。2D/UVパスを追加するなら別座標型とし、Painterの既存機能と混同しない
- 制御点の表面アンカー、接線、閉路、幅・圧力、各チャンネル／マテリアル、ブラシ資産と乱数seedを保存。選択・追加・削除・移動・smooth/corner・分割・結合・複製・非表示・並べ替え・Undoを要求
- Surface pathのジオメトリとPSDラスタ結果は分離し、再読込／再起動／解像度変更後に編集できることを受入条件にする
- メッシュ変形とトポロジー変更は別。三角形アンカーを復元できない変更は自動的に別面へ飛ばさず、再バインド候補をプレビューし確認
- UVシームを通る曲線は3D連続性を保持し、UV側で複数島へラスタライズ。重複UV、背面、非連結面、非多様体、薄い二重面は選択方針と警告が必要
- Filledの複数輪郭、穴、自己交差、球面など閉じた表面の内外、裏面への回り込みを仕様化。不明な形状で無制限に面を塗らない
- Ribbonの幅、角、端、tile/stretch、繋ぎ、重複時のalpha／チャンネル処理を保存し、跨シームで模様が切れないことを検証
- Erase/Smudgeは下位画像に依存するため、パス移動が後続ストロークの入力を変える。順序と再評価範囲を定義し、単純なベクター線差替えと同じとしない

## 10. CSP風の基本お絵描き機能カタログ

以下は要求の抜けを防ぐカタログ。CSP全版・全ツールで利用可能、あるいは本製品で同時にすべて実装されるとの主張ではない。各行に「必須/拡張」「2D/3D/両方」「対象層」「保存形式」「品質試験」を付けて採否を決める。

| 領域 | 本製品で定義するべき項目 | 一次資料 |
|---|---|---|
| 入力 | マウス、ペン、圧力、傾き、速度、消しゴム側、欠落時の既定値、グローバル／ブラシ別圧力カーブ、校正 | [Pen pressure](https://help.clip-studio.com/en-us/manual_en/240_brushes/Adjusting_pen_pressure.htm)、[Customizing brushes](https://help.clip-studio.com/en-us/manual_en/240_brushes/Customizing_brush_tools.htm) |
| ブラシ基礎 | ペン/鉛筆/エアブラシ/テクスチャブラシ/消しゴム、サイズ・硬さ・濃度・不透明度、先端形状・角度、間隔、散布、texture、色jitter、入抜き、プリセット | [Customizing brushes](https://help.clip-studio.com/en-us/manual_en/240_brushes/Customizing_brush_tools.htm) |
| 手振れ補正 | 描画中stabilization、速度に応じた強度、描画後補正、遅延表示、線端taper。補正量と入力遅延のトレードオフを表示 | [Correction](https://help.clip-studio.com/en-us/manual_en/810_subtools/C.htm) |
| 高度な筆 | 混色/にじみ/ぼかし/指先、dual brush、watercolor edge、textureの先端単位／線単位適用。再現に使うseedと入力を保存 | [Brush catalog](https://help.clip-studio.com/en-us/manual_en/240_brushes/240_brushes.htm)、[Texture settings](https://help.clip-studio.com/en-us/manual_en/810_subtools/T.htm) |
| 色 | 色相環/スライダー/数値、スポイト、palette、前景/副色/透明色、表示色と素材値の採取を区別 | [CSP manual](https://help.clip-studio.com/en-us/First_Topic.htm) |
| 選択 | 矩形/楕円/投げ縄/折線/選択ペン/消去、自動選択、色域、加算/減算/交差、反転、全選択/解除、拡縮・ぼかし、選択保存/読込 | [Selection tools](https://help.clip-studio.com/en-us/manual_en/330_selection/Selection_area_tool.htm)、[Select menu](https://www.clip-studio.com/site/gd_en/csp/userguide/csp_userguide/500_menu/500_menu_select.htm) |
| 塗り | 編集層/他層参照、連続性、色許容差、隙間閉じ、領域拡張、囲って塗る/投げ縄塗り/塗り残し、キャンセル、線形/円形等グラデーション | [Fill](https://help.clip-studio.com/en-us/manual_en/420_fill/Fill_Tool.htm)、[Fills/gradients](https://help.clip-studio.com/en-us/manual_en/420_fill/420_fill.htm) |
| 変形 | 移動/拡縮/回転/反転/自由変形/歪み/遠近/メッシュ、補間、基準点、縦横比、確定/取消、Liquify、整列 | [Transform tools](https://help.clip-studio.com/en-us/manual_en/360_transform/360_transform.htm) |
| 図形・編集線 | 直線、折線、Bezier、矩形、楕円、多角形、stroke/fill。ネイティブpathとラスタ焼き込みを区別 | [Vector layers](https://help.clip-studio.com/en-us/manual_en/180_layers/Vector_layers.htm) |
| 定規・スナップ | 直線/曲線/図形、平行、放射、同心円、ガイド、遠近、grid、対称・回転対称、スナップ対象・強度・有効範囲 | [Rulers](https://help.clip-studio.com/en-us/manual_en/510_ruler/510_ruler.htm)、[Ruler creation](https://help.clip-studio.com/en-us/manual_en/510_ruler/Basics_of_creating_rulers.htm) |
| 対称 | ミラー/放射、中心と軸、ブラシ・消去・塗り・選択それぞれの適用、3D object/world座標、左右のUV非対称への扱い | [Ruler snapping](https://help.clip-studio.com/en-us/manual_en/510_ruler/Drawing_while_snapping_to_a_ruler.htm) |
| レイヤー | 作成/複製/削除/結合/並べ替え/グループ/検索/複数選択、表示/ロック/透明画素ロック/不透明度/合成、クリップ、参照層、マスク | [Layers](https://help.clip-studio.com/en-us/manual_en/180_layers/180_layers.htm) |
| マスク | 保護非破壊、編集対象の明示、link/unlink、反転、無効、表示、部分透明。CSPのalpha式とPSD式を変換して扱う | [Layer masks](https://help.clip-studio.com/en-us/manual_en/180_layers/Layer_masks.htm) |
| ベクターの補助機能 | 制御点、線幅/不透明度再編集、単純化、線の結合・分割、交点まで消去。3D Surface pathとは別の型として設計 | [Vector layers](https://help.clip-studio.com/en-us/manual_en/180_layers/Vector_layers.htm) |
| 日常操作 | Undo/Redo履歴、切取/コピー/貼付、キャンバスzoom/pan/回転、ショートカット/一時ツール、ブラシカーソル、操作取消、保存・復旧、設定プリセット | [Preferences](https://help.clip-studio.com/en-us/manual_en/720_preferences/Preferences.htm) |

注意: CSPのvector layerはFill/Gradient/Blendや混色に制限がある。SVG入出力も全ブラシ情報の互換ではない。これを「すべての道具がすべてのレイヤーで使える」という要件に変換しない。[CSP Vector layers](https://help.clip-studio.com/en-us/manual_en/180_layers/Vector_layers.htm)

### 10.1 CSPとPSDの交換について確認できた範囲

- CSPはレイヤーのあるPSD/PSB交換にSave Duplicateを案内する。ICC埋込やRGB/CMYKの出力選択もある。[Save file](https://help.clip-studio.com/en-us/manual_en/210_file/Save_file.htm)
- CSPのPSDテキスト出力には「画像のみ」「画像＋非表示テキスト」「テキストのみ」がある。CSPはPSDテキストを必ずラスタライズする、という主張は誤り。[CELSYS text PSD export](https://support.clip-studio.com/en-us/faq/articles/20220034)
- CSPではCMYK入力をRGBへ変換して開くと公式説明される。元色モード保持の完全往復とは別の話。[Open file](https://help.clip-studio.com/en-us/manual_en/210_file/Open_file.htm)
- Open_file.htmの詳細な層変換表は**IllustStudio/ComicStudio入力用**。PSD互換の証拠として流用しない
- 調整タイプごとのPhotoshop↔CSP↔本製品の編集可能性・描画一致は、今回確認した資料だけでは保証できない。版を固定した実機往復試験を受入条件にする

## 11. 低スペック対応と非破壊性の両立（設計提案）

Painter公式も低解像度作業から高解像度への再計算を説明し、チャンネルが増えるとメモリ・性能に影響すると注意する。チャンネルの保存形式と色管理は同じ設定ではない。[Texture Set settings](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/interface/texture-set/texture-set-settings)

- 2Dラスタ編集、3D表示、パス編集、最終品質再計算、PSD保存を別ワークロードとして予算化
- 常駐GPUメモリ上限、CPU/RAM上限、キャッシュディスク上限を設定。タイル単位のdirty領域、遅延読込、LRU、チャンネルの有効化、非表示スタックの遅延評価、checkpointを利用する設計
- 低品質モードはプレビュー解像度／更新頻度／重い表示効果を落とし、原本16bit・パス・未対応PSDレコードを捨てない
- ストローク入力の記録と反応を優先し、高品質再計算はバックグラウンド。暫定画像と確定画像、処理中・キャンセル可能性をUIで示す
- 「再生成できるパス／手順」と「外部PSDから持ち込んだ有限解像度ラスタ」を区別。低解像度ラスタを高解像度化しても元以上の細部は戻らない
- 履歴を毎回全レイヤー全解像度コピーしない。タイル差分＋checkpoint／必要な元入力を保存し、実行不能になった履歴は破棄でなくcheckpointに移す
- 凍結／キャッシュ化は編集正本を残した可逆操作とする。恒久ラスタライズと違いを表示
- 8/16bit、解像度、層数、チャンネル数、UDIM枚数、Mesh三角形数、path点数、調整数ごとに性能試験を作る。未計測のFPS/遅延/動作最低GPUを断言しない
- 低スペック受入条件は具体的な基準PCと作業セットを決めてから数値を確定。UIフリーズ時間、入力遅延P95、ストローク確定時間、保存時間、ピークRAM/VRAM、復旧成功率を測る

## 12. 必須互換・復旧テスト（提案）

1. PS作成fixture → 本製品で無編集往復 → PSで構造・調整パラメータ・文字・埋込資産・外観確認
2. PS作成fixture → 本製品で対応ラスタ1層だけ編集 → PSで残りと未知データの保護確認
3. CSP作成fixtureも同様。CSPとPSの版・OS・ICC・bit depthを記録
4. グループpass-through、クリップ連鎖、マスク位置/disable/link、非表示調整、Blend If、特殊style、同名層／duplicate ID／missing ID
5. RGB8/RGB16/Gray16、ICCあり/なし、プロファイル変更、0/最大/半透明/負値候補、チャンネル範囲
6. 埋込/リンクSmart Object、リンク欠落、外部変更、入れ子、共有source、テキストの欠落フォント／縦書き
7. 外部改名、削除、追加、順序変更、同じ画素への同時編集、異なるレイヤーの同時編集、パスproxyの外部変更
8. 保存各段階でクラッシュ・ディスク満杯・権限エラー・ロック・外部上書き、再起動後に旧版か新版へ整合復旧
9. Path再編集、解像度変更、UVシーム、薄い二重面、重複UV、mesh topology変更、Filled自己交差、Ribbon角／端／閉路
10. 画素完全一致を要求できる部分と色差許容が必要な部分を分け、差の理由と閾値を文書化。「見た感じ同じ」だけで合格にしない

## 13. 未検証・担当外

- PSDバックエンド製品/OSS/商用SDKの選定、ライセンス、対応保証、実際の生成ファイルは未検証
- CSPの各調整タイプごとのPSDネイティブ往復は未検証
- lilToon固有のプロパティ名・シェーダーvariant・各パイプラインのマッピングは本調査の担当外。別の一次ソース確認結果を統合すること
- この資料は設計提案であり、コード・プロトタイプ・性能測定を実施していない

## 14. 追加確認: Generator / Filter / Anchor / Maskを中核概念にする

追加確認日: 2026-10-02。以下は新しい要件を受けた追記。単なる「将来の拡張候補」ではなく、ネイティブ編集モデルの中核として扱う。ただしAdobeの独自ファイル・計算エンジン互換を意味しない。

### 14.1 公式Painter資料で確認できた意味

| 概念 | 確認済みの意味と制約 | 一次資料 |
|---|---|---|
| Generator | ベイクしたPosition/Curvature/World Space Normal等を用いてmask/textureを生成。多くはグレースケールだがmask専用ではなくcontentにも適用できる | [Generators](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/effects/generators/generator) |
| Filter | 既存のlayer contentまたはmaskを変換。局所効果と、Passthroughで下位スタック合成へかける効果を区別 | [Filter](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/effects/filter) |
| Mask/effect stack | layer contentとmaskには別のeffect stack。maskはグレースケール。マスクの削除/付け直しは効果も消すため保護が必要 | [Masking and effects](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/interface/layer-stack/masking-and-effects) |
| Anchor | layer/mask上の参照点。Fill Layer/Fill Effect/Filter・Generator入力から使う。同一TextureSet内に限定、参照されるアンカーは参照側より下位に必要。順序を逆にすると参照が壊れる | [Anchor Point](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/effects/anchor-point) |
| ベイク | TextureSet/UV Tile、対象map、高低mesh、self bake、cageとfront/rear距離、解像度等を選び、結果ログを確認 | [How to bake mesh maps](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/baking/how-to-bake-mesh-maps) |

Effectsは順序を持ち、多くが合成・不透明度を持つ。Fill EffectはFill Layerに相当するものをcontent/mask内の操作として使える。[Effects](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/effects/effects)、[Fill](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/effects/fill)

アンカーは評価位置を定め、何を取得するかは参照側で選ぶという意味が導入時の公式説明にある。現在の公式APIにもチャンネル対応、単一チャンネル参照、alpha処理、Levelsがある。したがって単なる「レイヤー名へのリンク」では不十分。[2017.2公式説明](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/release-notes/old-versions/version-2017-2)、[SourceReference API](https://experienceleague.adobe.com/en/docs/substance-3d-dev/painter-python/api/substancepainter-package/source-module/reference)

Mask Editorの公式例はPosition、Thickness、Curvature、AO、World Space Normalを要求し、Texture/Micro Normal/Micro Height等にはアンカーを入力できる。必要mapはGeneratorごとに違う。全Generatorにすべてのmapが必要とはしない。[Mask Editor](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/effects/generators/mask-editor)

### 14.2 本製品への必須設計契約（提案）

- レイヤーとは別にEffectノードを第一級データとして保存。Paint、Fill、Generator、Filter、Levels/Adjustment、Anchor等を、contentまたはmaskの順序付きスタックへ配置する
- Generatorには型、版、パラメータ、seed、入力slot型、必要/任意map、入力のrevisionを保存。Filterには適用範囲、対象チャンネル、既存入力、パラメータ、境界/UVシーム処理を保存
- マスクは描き込み、Fill、Generator、Filter、Anchor参照を組み合わせる非破壊スタック。反転、無効、solo、コピー、順序変更、合成、不透明度、手描き補正、Undoを備える
- ネイティブGeneratorの例: 曲率由来のエッジ/くぼみ、AO由来の汚れ、Position/World Normal由来の上下・方向性、Thickness由来の透過用mask、noise/grunge合成。素材固有の見た目は入力・パラメータとして持たせる
- ネイティブFilterの例: Blur、Sharpen、Invert、Levels/Curve、Threshold、Dilation/Erosion、Warp、Height→Normal等。各効果の色/数値/法線型の適用可否を宣言し、色チャンネル用処理を法線へ自動適用しない
- Anchor参照はAnchorUUID、TextureSetUUID、content/maskの区別、effect-stage UUID、source channel、destination input、alpha変換・数値変換を保存。表示名変更でリンクが切れない
- **循環拒否は本製品の設計提案**: 依存DAGを検証し、自己参照・循環を生成する操作を拒否。Painter資料は下位限定を明記するが、内部の全cycle検出実装まで公開しているとは主張しない
- 下位限定の順序、削除、移動、group変更、TextureSet移動を検証し、参照先/参照元一覧と影響を示す。無効参照を黙って黒や0に置き換えて確定保存しない
- アンカーは「その位置までの評価結果」を公開し、その後のeffectを含む最終結果と区別する。マスクをかける前/後などの評価段階を仕様・UIで曖昧にしない
- 参照される非表示/無効化した表示用レイヤーの計算が必要になる場合がある。非表示なら常に省略する最適化は禁止。表示到達性と依存到達性を分ける
- 依存先の編集は影響する下流ノードとdirty tileのみ再評価し、重いGenerator/Filterの低解像度previewと確定品質を分ける。cache keyにnode type/version、parameters、seed、mesh/map/input revision、解像度・色空間を含める

### 14.3 ベイク資産とエラーの扱い

Normal、World Space Normal、AO、Curvature、Position、Thickness、ID等を型付きMeshMap資産として管理する。これは提供するべき入力カタログであり、全mapを同じ方法で生成できるという主張ではない。low mesh/UV、optional high mesh、cageまたはray距離、名前対応、normal convention、解像度/AA/padding、ベイク設定versionとハッシュを保存。外部ベイクmapの明示取込も許す。

メッシュ/UV/ポーズ/ベイク入力が変わったら依存mapをstale表示。欠落・古い・不一致のmapを持つGeneratorは原因と必要操作を示し、最後の有効previewを「古い」と明記する。再ベイク完了後は検証して一括切替し、途中mapだけの混合revisionを確定出力にしない。低スペック向けの低品質ベイクも元の高品質設定と分離する。

### 14.4 正本とPSD交換

**独自プロジェクトの編集グラフがGenerator/Filter/Anchor/Maskの正本。PSDはその特定revision・解像度・チャンネルでのラスタ投影**とする。PSDに対応するマスクや調整を表現できる場合も、独自グラフを省略しない。PSD内の未知メタデータだけを唯一の保存先にしない。

外部PSDで生成結果が描き換えられた場合、(a)グラフから再生成、(b)外部ラスタを独立入力/補正層に取り込み、(c)外部結果を採用して生成を凍結、の選択を確認する。ラスタ差分からGeneratorパラメータやアンカー配線を逆算できると約束しない。どの選択でも元グラフと依存資産を保持する。

**互換性の境界:** この要件はSubstance風の操作・概念を独自に提供するもの。.sbsar/.spsm/.sppのimport/export、Substance Engineの実行、Adobe標準Generator/Filterの完全再現、Adobe付属資産の再配布やライセンス取得を含めない。将来必要なら別途SDK/ライセンス/セキュリティ/互換テストを伴う独立要件として決める。

### 14.5 追加受入テスト

手描きheight→Anchor→別層のmask→Generator→Filterを保存・再起動・再編集し下流が更新されること。Anchor前後へのFilter挿入で参照結果が仕様どおり変わること。参照のrename/複製/順序変更/削除/Undo/TextureSet移動で誤結線しないこと。自己/相互参照を拒否すること。ベイク欠落/stale/再ベイク/低品質previewを識別すること。外部PSDの生成層変更からグラフを失わず競合解決できることを必須にする。
