using System;

namespace Yozolab.YoluPainter.Api
{
    /// <summary>
    /// YoluPainter のプラグイン。アセンブリに <see cref="ExportsPainterPluginAttribute"/> を付けて知らせると、YoluPainter が
    /// 読み込みのたびに 1 つ作り、<see cref="Configure"/> でコマンドやツールのアイコンを登録させる。プラグインのアセンブリは
    /// このアセンブリ（Yozolab.YoluPainter.Api）だけを参照すればよい。版の約束は <see cref="PainterApi"/>。
    /// </summary>
    public abstract class PainterPlugin
    {
        /// <summary>重ならない ID（例: "com.example.noise"）。英数字・点・ハイフン・下線、1〜64 文字。</summary>
        public abstract string Id { get; }

        /// <summary>メニューやプラグインの一覧に出す名前。</summary>
        public virtual string DisplayName => Id;

        /// <summary>この版より古い API では読み込まない（既定は 0.1）。</summary>
        public virtual Version RequiredApi => new Version(0, 1);

        /// <summary>登録する。ここで投げた例外は YoluPainter が受け止め、このプラグインだけを無効にして知らせる。</summary>
        public abstract void Configure(IPainterPluginBuilder builder);
    }

    /// <summary>このアセンブリが YoluPainter のプラグインを持つことを知らせる（アセンブリに付ける。いくつでも）。</summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class ExportsPainterPluginAttribute : Attribute
    {
        public Type PluginType { get; }
        public ExportsPainterPluginAttribute(Type pluginType) { PluginType = pluginType; }
    }

    /// <summary>
    /// プラグインの API の版。0.x のあいだは試作で、互換を壊す変更をすることがある（壊すときは版の小さい数を上げる）。
    /// 安定を約束するのはこのアセンブリの公開の型だけで、YoluPainter.Editor や Core を直接使うプラグインは、YoluPainter の
    /// 更新で壊れることがある。
    /// </summary>
    public static class PainterApi
    {
        public static readonly Version Version = new Version(0, 1);
    }
}
