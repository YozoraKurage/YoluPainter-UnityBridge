using System;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>The document's Normal-output settings (Height → Normal, strength, edges, file Y direction). One undo step per
    /// change; slider drags coalesce like other parameters. The settings change no layer composite, only the Normal output
    /// (<see cref="NormalMaps.Output"/>), so they mark no tiles in the change journal.</summary>
    public sealed partial class PaintDocument
    {
        NormalSettings normalSettings = NormalSettings.Default;
        public NormalSettings NormalSettings { get { return normalSettings; } }
        public void SetNormalSettings(NormalSettings settings, bool coalesce = false)
        {
            EnsureNoStroke(); if (settings == null) throw new ArgumentNullException(nameof(settings));
            var old = normalSettings; if (old.Equals(settings)) return;
            Execute(new DelegateCommand(() => normalSettings = settings, () => normalSettings = old, 64), coalesce ? (object)"normalSettings" : null);
        }
    }
}
