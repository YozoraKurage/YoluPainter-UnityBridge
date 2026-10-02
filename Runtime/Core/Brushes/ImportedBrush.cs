using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core.Brushes
{
    /// <summary>One brush read from another application's file. Settings carry the decoded tip and texture. Everything the
    /// importer could not represent is listed in Warnings instead of being dropped silently.</summary>
    public sealed class ImportedBrush
    {
        public string Name { get; private set; }
        /// <summary>Where it came from, e.g. "Photoshop ABR v6" or "GIMP GBR".</summary>
        public string Source { get; private set; }
        public BrushSettings Settings { get; private set; }
        public IReadOnlyList<string> Warnings { get; private set; }

        public ImportedBrush(string name, string source, BrushSettings settings, IEnumerable<string> warnings = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            Name = string.IsNullOrEmpty(name) ? "Untitled" : name; Source = source ?? ""; Settings = settings;
            Warnings = new List<string>(warnings ?? new string[0]).AsReadOnly();
        }
    }

    /// <summary>A malformed or unsupported brush file. The message says what was wrong; nothing is half-imported.</summary>
    public sealed class BrushImportException : Exception
    {
        public BrushImportException(string message) : base(message) { }
        public BrushImportException(string message, Exception inner) : base(message, inner) { }
    }
}
