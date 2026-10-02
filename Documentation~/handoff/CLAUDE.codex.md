# Claude Code 作業契約

## ゴール
Unity 2022.3 の専用 EditorWindow 内で2D/3Dテクスチャ制作を行う拡張を、同梱仕様の全スコープへ段階的に実装する。現在のコードを完成品と扱わず、最初に実Unityで未検証部分を調べて直す。

## 読む順序
1. HANDOFF_CLAUDE_CODE.md
2. docs/STATUS.md と docs/VALIDATION.md
3. spec/Unity_Texture_Paint_Spec_v0_1.md
4. docs/ARCHITECTURE.md、docs/PSD_COMPATIBILITY.md、docs/TESTING.md
5. Runtime/Core/README.md、Editor/Preview/README.md

## 不変条件
- 元シーン、元材質、元テクスチャ、Prefab、import設定を描画中に変更しない
- 任意のユーザーPrefab/GameObjectをInstantiateしてからscriptを止める方式に戻さない。現行はMeshだけのsnapshotとRenderer/Transform再構成
- フォーカス喪失、Escape、reload、play移行、例外でストロークが取り残されないようにする
- nativeのstraight RGBA8 sourceと透明画素のRGBを守る。GPU preview/低精度cacheを保存の唯一の正本にしない
- GPUのsrc/dst同一read-writeは禁止。メモリ予算・readback寿命・世代を守る
- PSD未対応情報を黙って捨てない。PreserveOnlyへ落ちた原本への編集書戻しは禁止
- シェーダーが似ているだけでlilToonへ自動適用しない。実version/variant/property/設定を確認する
- 名前だけで外部PSDレイヤーを対応付けたり、パス/生成レイヤーと外部画素を勝手に上書きし合ったりしない
- `current`を最後に置換する保存契約を守る。旧世代を無断削除しない
- 新機能を追加したら回帰試験・保存復元・Undo/取消・型/予算拒否も追加する
- .NET coreテスト、数学アダプター、Unity API実コンパイル、実GPU、Photoshop/CSP実機の結果を混同しない
- Generator/Filter/Anchor/Mask、編集可能3Dパス、マルチチャンネル、PSD調整は中核要望。未実装だからといってscopeから削除しない

## 許容する実装判断
IMGUI/UI Toolkit構成、GPU/CPU分担の改善などは、仕様の意味を保ち、測定/試験根拠を残して進める。新しいライセンス、資格情報、外部公開、元アセットへ適用する操作は別途ユーザーの意思を確認する。

## 変更記録
作業の各節目で docs/STATUS.md と docs/VALIDATION.md を更新する。コード存在だけで tested にしない。失敗した検証も消さず、原因と修正後の再試験を残す。
