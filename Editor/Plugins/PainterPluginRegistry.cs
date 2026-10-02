using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using Yozolab.YoluPainter.Api;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// プラグインの読み込み（Yozolab.YoluPainter.Api の <see cref="PainterPlugin"/>）。読み込んだアセンブリのうち
    /// <see cref="ExportsPainterPluginAttribute"/> の付いたものからプラグインの型を集め、ドメインの読み込みごとに 1 つずつ作って
    /// Configure させる。プラグインの例外（作る・Configure・コマンド）はここで受け止め、そのプラグインだけを止めて理由を残す
    /// （Configure の途中で止まったプラグインの登録は全部外す）。
    /// </summary>
    internal static class PainterPluginRegistry
    {
        /// <summary>YoluPainter のメニューの名前（メニューバーの順）。コマンドの置き場の先頭がこれなら、そのメニューに入る。</summary>
        public static readonly string[] BuiltInMenus = { "File", "Edit", "Layer", "Select", "Filter", "3D", "View", "Window", "Help" };
        /// <summary>それ以外のコマンドの入るメニュー。</summary>
        public const string PluginsMenu = "Plugins";
        const int MaxMenuDepth = 6, MaxMenuSegment = 64;
        static readonly Regex IdPattern = new Regex("^[A-Za-z0-9._-]{1,64}$");

        /// <summary>読み込んだ（または断った）プラグイン。</summary>
        internal sealed class Entry
        {
            public Type Type; public PainterPlugin Plugin; public string Id, Name, Error;
            public readonly List<string> Notes = new List<string>();
            public readonly List<string> Icons = new List<string>();
            public bool Loaded => Plugin != null && Error == null;
        }

        /// <summary>メニューのコマンド。</summary>
        internal sealed class Command
        {
            public Entry Owner; public string Menu, Path;
            public Action<IPainterSession> Run; public Func<IPainterSession, bool> Enabled;
        }

        static List<Entry> s_entries;
        static readonly List<Command> s_commands = new List<Command>();

        public static IReadOnlyList<Entry> Entries { get { EnsureLoaded(); return s_entries; } }
        public static IReadOnlyList<Command> Commands { get { EnsureLoaded(); return s_commands; } }
        /// <summary>Plugins のメニューを出すか（そこに入るコマンドがあるか）。</summary>
        public static bool HasPluginsMenu => Commands.Any(c => c.Menu == PluginsMenu);
        /// <summary>そのメニューに入るコマンド（登録の順）。</summary>
        public static IEnumerable<Command> CommandsIn(string menu) => Commands.Where(c => c.Menu == menu);

        static void EnsureLoaded() { if (s_entries == null) Load(Discover()); }

        /// <summary>読み込んだアセンブリから、知らせてきたプラグインの型を集める（アセンブリの名前の順。同じアセンブリでは書いた順）。</summary>
        internal static IEnumerable<Type> Discover()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
            {
                ExportsPainterPluginAttribute[] exports;
                try { exports = assembly.GetCustomAttributes<ExportsPainterPluginAttribute>().ToArray(); }
                catch (Exception) { continue; } // 読めない属性のアセンブリは飛ばす（ほかのプラグインを止めない）
                foreach (var export in exports) yield return export.PluginType;
            }
        }

        /// <summary>この型の並びで読み込み直す（前の登録は全部外す）。テストはここに自分の型を渡す。</summary>
        internal static void Load(IEnumerable<Type> types)
        {
            UnloadAll();
            s_entries = new List<Entry>();
            foreach (var type in types)
            {
                var entry = new Entry { Type = type, Id = type?.FullName ?? "(null)", Name = type?.Name ?? "(null)" };
                s_entries.Add(entry);
                try { LoadOne(entry); }
                catch (Exception ex)
                {
                    entry.Error = ex is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : ex.Message;
                    Unregister(entry);
                    Debug.LogWarning("YoluPainter: the plugin " + entry.Id + " was not loaded: " + entry.Error);
                }
            }
        }

        static void LoadOne(Entry entry)
        {
            var type = entry.Type;
            if (type == null) throw new ArgumentException("The plugin type is null.");
            if (!typeof(PainterPlugin).IsAssignableFrom(type)) throw new ArgumentException(type.FullName + " does not derive from PainterPlugin.");
            if (type.IsAbstract || type.ContainsGenericParameters) throw new ArgumentException(type.FullName + " is abstract or generic.");
            if (type.GetConstructor(Type.EmptyTypes) == null) throw new ArgumentException(type.FullName + " has no public parameterless constructor.");
            var plugin = (PainterPlugin)Activator.CreateInstance(type);
            string id = plugin.Id;
            if (id == null || !IdPattern.IsMatch(id)) throw new ArgumentException("The plugin id \"" + id + "\" is not 1–64 letters, digits, dots, hyphens or underscores.");
            entry.Id = id; entry.Name = string.IsNullOrWhiteSpace(plugin.DisplayName) ? id : plugin.DisplayName;
            if (s_entries.Any(e => e != entry && e.Loaded && e.Id == id)) throw new ArgumentException("Another plugin already uses the id \"" + id + "\".");
            var required = plugin.RequiredApi;
            if (required != null && (required.Major != PainterApi.Version.Major || required.Minor > PainterApi.Version.Minor))
                throw new NotSupportedException("It needs YoluPainter plugin API " + required + "; this YoluPainter has " + PainterApi.Version + ".");
            entry.Plugin = plugin;
            plugin.Configure(new Builder(entry));
        }

        static void UnloadAll()
        {
            if (s_entries != null) foreach (var e in s_entries) Unregister(e);
            s_commands.Clear();
        }

        static void Unregister(Entry entry)
        {
            s_commands.RemoveAll(c => c.Owner == entry);
            foreach (var id in entry.Icons) PainterToolIcons.Unregister(id);
            entry.Icons.Clear();
            if (entry.Error != null) entry.Plugin = null;
        }

        /// <summary>コマンドを走らせる。プラグインの例外は受け止めて理由を返す（成功なら null）。</summary>
        internal static string Run(Command command, IPainterSession session)
        {
            try { command.Run(session); return null; }
            catch (Exception ex)
            {
                Debug.LogWarning("YoluPainter: the plugin command " + command.Menu + "/" + command.Path + " (" + command.Owner.Id + ") failed: " + ex);
                return ex.Message;
            }
        }

        /// <summary>コマンドが今使えるか。プラグインの例外は「使えない」にする。</summary>
        internal static bool IsEnabled(Command command, IPainterSession session)
        {
            if (command.Enabled == null) return true;
            try { return command.Enabled(session); }
            catch (Exception) { return false; }
        }

        sealed class Builder : IPainterPluginBuilder
        {
            readonly Entry entry;
            public Builder(Entry entry) { this.entry = entry; }

            public void AddCommand(string menuPath, Action<IPainterSession> run, Func<IPainterSession, bool> enabled = null)
            {
                if (run == null) throw new ArgumentNullException(nameof(run));
                var parts = (menuPath ?? "").Split('/');
                if (parts.Length < 1 || parts.Length > MaxMenuDepth || parts.Any(p => p.Trim().Length == 0 || p.Length > MaxMenuSegment))
                    throw new ArgumentException("The menu path \"" + menuPath + "\" needs 1–" + MaxMenuDepth + " non-empty parts of at most " + MaxMenuSegment + " characters.");
                string menu = BuiltInMenus.FirstOrDefault(m => string.Equals(m, parts[0], StringComparison.OrdinalIgnoreCase));
                string path = menu != null ? string.Join("/", parts.Skip(1)) : string.Join("/", parts);
                if (menu != null && path.Length == 0) throw new ArgumentException("\"" + menuPath + "\" names a menu but no command in it.");
                menu = menu ?? PluginsMenu;
                if (s_commands.Any(c => c.Menu == menu && c.Path == path))
                {
                    entry.Notes.Add("The command " + menu + "/" + path + " is already used by another plugin; this one was left out.");
                    return;
                }
                s_commands.Add(new Command { Owner = entry, Menu = menu, Path = path, Run = run, Enabled = enabled });
            }

            public void SetToolIcon(string toolId, Texture2D icon, Texture2D selected = null)
            {
                PainterToolIcons.Register(toolId, icon, selected);
                if (!entry.Icons.Contains(toolId)) entry.Icons.Add(toolId);
            }
        }
    }
}
