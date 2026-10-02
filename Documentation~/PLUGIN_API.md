# プラグインの API（試作 0.1）

YoluPainter の機能を外から足すための受け口。**0.x のあいだは試作**で、互換を壊す変更をすることがある（壊すときは
`PainterApi.Version` の小さい数を上げる）。約束するのは `Yozolab.YoluPainter.Api` アセンブリの公開の型だけで、
`Yozolab.YoluPainter.Editor` や `Core` を直接使うプラグインは、YoluPainter の更新で壊れることがある。

## 作り方

1. プラグインのフォルダに Editor 専用のアセンブリ定義（asmdef）を作り、`Yozolab.YoluPainter.Api` を参照する
   （文書の型を使うなら `Yozolab.YoluPainter.Core` も）。
2. `PainterPlugin` を継ぐクラス（公開・引数なしのコンストラクタ）を書き、アセンブリに `ExportsPainterPlugin` を付ける。

```csharp
using System;
using Yozolab.YoluPainter.Api;
using Yozolab.YoluPainter.Core;

[assembly: ExportsPainterPlugin(typeof(Example.NoisePlugin))]

namespace Example
{
    public sealed class NoisePlugin : PainterPlugin
    {
        public override string Id => "com.example.noise";
        public override string DisplayName => "Noise";

        public override void Configure(IPainterPluginBuilder builder)
        {
            // 「Filter」メニューの終わりに入る。先頭が YoluPainter のメニューの名前でなければ「Plugins」メニューに入る
            builder.AddCommand("Filter/Noise/Add Noise Layer", session =>
            {
                var random = new Random(1);
                var image = new byte[session.Width * session.Height * 4]; // straight RGBA8、左下が原点
                for (int i = 0; i < image.Length; i += 4)
                {
                    byte v = (byte)random.Next(256);
                    image[i] = image[i + 1] = image[i + 2] = v; image[i + 3] = 255;
                }
                session.AddImageLayer("Noise", PaintChannel.Color, image); // 1 回の Undo
            });
        }
    }
}
```

## 読み込みと失敗の扱い

- ドメインの読み込みごとに、読み込んだアセンブリの `ExportsPainterPlugin` からプラグインを集め（アセンブリの名前の順）、1 つずつ
  作って `Configure` を呼ぶ。
- 断るもの: `PainterPlugin` を継いでいない・抽象・引数なしのコンストラクタが無い型、ID が 1〜64 文字の英数字・点・ハイフン・下線で
  ない、ほかのプラグインと ID が同じ、`RequiredApi` がこの YoluPainter の API より新しい（大きい数が違う、または小さい数が大きい）。
- `Configure` が例外を投げたら、そのプラグインだけを止め、それまでの登録（コマンド・ツールのアイコン）も外す。ほかのプラグインは
  そのまま読む。
- コマンドの置き場がほかのプラグインと同じなら、後のほうのそのコマンドだけを外して知らせる。
- コマンドの例外は受け止め、ステータスバーに理由を出す（ウィンドウは壊れない）。`enabled` の例外は「使えない」として扱う。
- 読み込んだプラグインと断った理由は、YoluPainter の ヘルプ ▸ プラグイン… で見られる。

## 登録の口（IPainterPluginBuilder）

| 口 | 内容 |
|---|---|
| `AddCommand(menuPath, run, enabled)` | メニューのコマンド。`menuPath` は `/` 区切りで 1〜6 段、各段 1〜64 文字。先頭が File・Edit・Layer・Select・Filter・3D・View・Window・Help ならそのメニューの終わり（区切りの後）に、それ以外は Plugins メニューに入る。ストロークの最中は灰色 |
| `SetToolIcon(toolId, icon, selected)` | ツールの帯の絵を差し替える（描き手の差し替えより優先）。テクスチャの持ち主はプラグインのまま |

## プロジェクトへの窓口（IPainterSession）

コマンドを呼んだウィンドウの、今のプロジェクト（今のテクスチャセット）。画像はどれも straight RGBA8、左下が原点、
`Width × Height × 4` バイト。

| 口 | 内容 |
|---|---|
| `Width`・`Height`・`ProjectPath`・`Model`・`Channel`・`SelectedLayer`・`Layers` | 読むだけ。`Model` は元のアセットなので変えないこと（YoluPainter は元のアセットを変えない約束） |
| `ReadComposite(channel)` | チャンネルの全レイヤーの合成（Normal は Unity 向けの出力: OpenGL の向き、Height からの導出込み） |
| `ReadLayer(layerId, channel)` | ペイントのレイヤーのチャンネルの画素 |
| `AddImageLayer(name, channel, rgba)` | 選んでいるレイヤーの上に、画像を中身にした新しいペイントのレイヤーを足して選ぶ（選択範囲に関係なく全体。1 回の Undo） |
| `ReplaceLayerPixels(layerId, channel, rgba)` | ペイントのレイヤーのチャンネルを画像で置き換える。選択範囲があればその中だけ（部分的な選択はプリマルチプライドで混ぜる）。チャンネルが無効なら同じ 1 回の Undo の中で有効にする |
| `ShowMessage(text)` | ステータスバーに出す |

変える口は、ストロークの最中なら `InvalidOperationException`、画像の大きさや層の種類が違えば例外で断り、文書は変わらない。

## これから足すもの（予定）

ドックのパネル、フィルター・生成（Generator）、書き出し（別のシェーダー向けのチャンネルの詰め合わせなど）、文書の出来事
（開いた・保存した・ストローク）。フィルターと生成は、プラグインが無い環境でもファイルを開けて設定を失わない保存の形を先に決める。
