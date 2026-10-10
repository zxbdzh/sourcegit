using System;
using System.Collections.Generic;
using System.IO;

using System.Linq;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;

using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;

using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace SourceGit.Models
{
    public static class GrammarUtility
    {
        private static readonly ExtraGrammar[] s_extraGrammars =
        [
            new ExtraGrammar("source.toml", [".toml"], "toml.json"),
            new ExtraGrammar("source.kotlin", [".kotlin", ".kt", ".kts"], "kotlin.json"),
            new ExtraGrammar("source.hx", [".hx"], "haxe.json"),
            new ExtraGrammar("source.hxml", [".hxml"], "hxml.json"),
            new ExtraGrammar("text.html.jsp", [".jsp", ".jspf", ".tag"], "jsp.json"),
            new ExtraGrammar("source.vue", [".vue"], "vue.json"),
            new ExtraGrammar("source.erlang", [".erl", ".escript", ".hrl"], "erlang.json"),
            new ExtraGrammar("source.ocaml", [".ml", ".mli"], "ocaml.json"),
            new ExtraGrammar("source.ocamllex", [".mll"], "ocamllex.json"),
            new ExtraGrammar("source.ocamlyacc", [".mly"], "ocamlyacc.json"),
            new ExtraGrammar("source.swift", [".swift"], "swift.json"),
        ];

        private static readonly Dictionary<string, string> s_fenceExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["c"] = ".c", ["h"] = ".h", ["cpp"] = ".cpp", ["c++"] = ".cpp", ["cc"] = ".cpp", ["hpp"] = ".cpp",
            ["cs"] = ".cs", ["csharp"] = ".cs", ["c#"] = ".cs",
            ["fs"] = ".fs", ["fsharp"] = ".fs",
            ["js"] = ".js", ["javascript"] = ".js", ["jsx"] = ".jsx",
            ["ts"] = ".ts", ["typescript"] = ".ts", ["tsx"] = ".tsx",
            ["py"] = ".py", ["python"] = ".py",
            ["rb"] = ".rb", ["ruby"] = ".rb",
            ["rs"] = ".rs", ["rust"] = ".rs",
            ["go"] = ".go", ["golang"] = ".go",
            ["java"] = ".java",
            ["kt"] = ".kt", ["kotlin"] = ".kt",
            ["swift"] = ".swift",
            ["php"] = ".php",
            ["lua"] = ".lua",
            ["sql"] = ".sql",
            ["json"] = ".json",
            ["xml"] = ".xml",
            ["html"] = ".html", ["htm"] = ".html",
            ["css"] = ".css",
            ["sh"] = ".sh", ["bash"] = ".sh", ["shell"] = ".sh", ["zsh"] = ".sh",
            ["ps1"] = ".ps1", ["powershell"] = ".ps1",
            ["yaml"] = ".yaml", ["yml"] = ".yaml",
            ["md"] = ".md", ["markdown"] = ".md",
            ["diff"] = ".diff", ["patch"] = ".diff",
            ["toml"] = ".toml",
            ["vue"] = ".vue",
            ["dart"] = ".dart",
        };
        private static readonly Dictionary<string, IRawGrammar> s_cachedRawGrammars = new();

        public static string GetScope(string file, RegistryOptions reg)
        {
            var extension = Path.GetExtension(file);
            if (extension == ".h")
                extension = ".cpp";
            else if (extension is ".resx" or ".plist" or ".manifest" or ".sln" or ".slnx")
                extension = ".xml";
            else if (extension == ".command")
                extension = ".sh";

            foreach (var grammar in s_extraGrammars)
            {
                foreach (var ext in grammar.Extensions)
                {
                    if (ext.Equals(extension, StringComparison.OrdinalIgnoreCase))
                        return grammar.Scope;
                }
            }

            return reg.GetScopeByExtension(extension);
        }

        public static string GetScopeFromFence(string info, RegistryOptions reg)
        {
            if (string.IsNullOrWhiteSpace(info))
                return null;

            var lang = info.Trim();
            var cut = lang.IndexOfAny([' ', '\t', '{']);
            if (cut > 0)
                lang = lang[..cut];
            lang = lang.TrimStart('.');
            if (lang.Length == 0)
                return null;

            if (!s_fenceExtensions.TryGetValue(lang, out var ext))
                ext = "." + lang;
            return GetScope("file" + ext, reg);
        }

        public static IRawGrammar GetGrammar(string scopeName, RegistryOptions reg)
        {
            if (string.IsNullOrEmpty(scopeName))
                return null;

            if (s_cachedRawGrammars.TryGetValue(scopeName, out var cached))
                return cached;

            foreach (var grammar in s_extraGrammars)
            {
                if (grammar.Scope.Equals(scopeName, StringComparison.OrdinalIgnoreCase))
                {
                    var asset = AssetLoader.Open(new Uri($"avares://SourceGit/Resources/Grammars/{grammar.File}",
                        UriKind.RelativeOrAbsolute));

                    try
                    {
                        var raw = GrammarReader.ReadGrammarSync(new StreamReader(asset));
                        s_cachedRawGrammars.Add(scopeName, raw);
                        return raw;
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            var fallback = reg.GetGrammar(scopeName);
            s_cachedRawGrammars.Add(scopeName, fallback);
            return fallback;
        }

        private record ExtraGrammar(string Scope, List<string> Extensions, string File)
        {
            public readonly string Scope = Scope;
            public readonly List<string> Extensions = Extensions;
            public readonly string File = File;
        }
    }

    public class RegistryOptionsWrapper(ThemeName defaultTheme) : IRegistryOptions
    {
        public string LastScope { get; set; } = string.Empty;

        public IRawTheme GetTheme(string scopeName) => _backend.GetTheme(scopeName);
        public IRawTheme GetDefaultTheme() => _backend.GetDefaultTheme();
        public ICollection<string> GetInjections(string scopeName) => _backend.GetInjections(scopeName);
        public IRawGrammar GetGrammar(string scopeName) => GrammarUtility.GetGrammar(scopeName, _backend);
        public string GetScope(string filename) => GrammarUtility.GetScope(filename, _backend);
        public string GetScopeFromFence(string info) => GrammarUtility.GetScopeFromFence(info, _backend);

        public IRawTheme LoadTheme(ThemeName name)
        {
            if (s_cachedTheme.TryGetValue(name, out var cached))
                return cached;

            var loaded = _backend.LoadTheme(name);
            s_cachedTheme.Add(name, loaded);
            return loaded;
        }

        private static readonly Dictionary<ThemeName, IRawTheme> s_cachedTheme = new();
        private readonly RegistryOptions _backend = new(defaultTheme);
    }

    public static class TextMateHelper
    {
        public static TextMate.Installation CreateForEditor(TextEditor editor)
        {
            return editor.InstallTextMate(Application.Current?.ActualThemeVariant == ThemeVariant.Dark ?
                new RegistryOptionsWrapper(ThemeName.DarkPlus) :
                new RegistryOptionsWrapper(ThemeName.LightPlus));
        }

        public static void SetThemeByApp(TextMate.Installation installation)
        {
            if (installation is { RegistryOptions: RegistryOptionsWrapper reg })
            {
                var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
                installation.SetTheme(reg.LoadTheme(isDark ? ThemeName.DarkPlus : ThemeName.LightPlus));
            }
        }

        public static void SetGrammarByFileName(TextMate.Installation installation, string filePath)
        {
            if (installation is { RegistryOptions: RegistryOptionsWrapper reg } && !string.IsNullOrEmpty(filePath))
            {
                var scope = reg.GetScope(filePath);
                if (reg.LastScope != scope)
                {
                    reg.LastScope = scope;
                    installation.SetGrammar(scope);
                    GC.Collect();
                }
            }
        }
        public static void Highlight(InlineCollection inlines, string code, string language)
        {
            if (inlines == null || string.IsNullOrEmpty(code))
                return;

            try
            {
                var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
                var session = dark ? s_dark ??= new HighlightSession(true) : s_light ??= new HighlightSession(false);
                if (!session.TryGetGrammar(language, out var grammar))
                {
                    inlines.Add(new Run(code));
                    return;
                }

                IStateStack stack = null;
                var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0)
                        inlines.Add(new LineBreak());

                    var line = lines[i];
                    if (line.Length == 0)
                        continue;

                    var result = grammar.TokenizeLine(line, stack, TimeSpan.FromMilliseconds(200));
                    stack = result.RuleStack;
                    var tokens = result.Tokens;
                    var tokenList = tokens.ToList();
                    for (var t = 0; t < tokenList.Count; t++)
                    {
                        var token = tokenList[t];
                        var start = Math.Max(0, token.StartIndex);
                        var end = t + 1 < tokenList.Count ? tokenList[t + 1].StartIndex : line.Length;
                        if (end > line.Length)
                            end = line.Length;
                        if (start >= end)
                            continue;

                        var run = new Run(line[start..end]);
                        if (session.TryColor(token.Scopes, out var brush, out var bold, out var italic))
                        {
                            if (brush != null)
                                run.Foreground = brush;
                            if (bold)
                                run.FontWeight = FontWeight.Bold;
                            if (italic)
                                run.FontStyle = Avalonia.Media.FontStyle.Italic;
                        }

                        inlines.Add(run);
                    }
                }
            }
            catch
            {
                inlines.Clear();
                inlines.Add(new Run(code));
            }
        }

        private static HighlightSession s_light;
        private static HighlightSession s_dark;

        private sealed class HighlightSession
        {
            public HighlightSession(bool dark)
            {
                _options = new RegistryOptionsWrapper(dark ? ThemeName.DarkPlus : ThemeName.LightPlus);
                _registry = new Registry(_options);
                _theme = _registry.GetTheme();
                foreach (var color in _theme.GetColorMap())
                {
                    try
                    {
                        _brushes[_theme.GetColorId(color)] = new ImmutableSolidColorBrush(Color.Parse(Normalize(color)));
                    }
                    catch
                    {
                        // 主题色字符串偶发非法时跳过该色，不影响其余 token。
                    }
                }
            }

            public bool TryGetGrammar(string language, out IGrammar grammar)
            {
                grammar = null;
                var scope = _options.GetScopeFromFence(language);
                if (string.IsNullOrEmpty(scope))
                    return false;
                grammar = _registry.LoadGrammar(scope);
                return grammar != null;
            }

            public bool TryColor(IList<string> scopes, out IBrush brush, out bool bold, out bool italic)
            {
                brush = null;
                bold = false;
                italic = false;
                if (scopes == null || scopes.Count == 0)
                    return false;

                var foreground = 0;
                var style = 0;
                foreach (var rule in _theme.Match(scopes))
                {
                    if (foreground == 0 && rule.foreground > 0)
                        foreground = rule.foreground;
                    if (style == 0 && (int)rule.fontStyle > 0)
                        style = (int)rule.fontStyle;
                }

                if (foreground != 0)
                    _brushes.TryGetValue(foreground, out brush);
                bold = (style & 2) != 0;
                italic = (style & 1) != 0;
                return brush != null || bold || italic;
            }

            private static string Normalize(string color)
            {
                if (color is { Length: 9 } && color[0] == '#')
                    return $"#{color[7]}{color[8]}{color[1]}{color[2]}{color[3]}{color[4]}{color[5]}{color[6]}";
                return color;
            }

            private readonly RegistryOptionsWrapper _options;
            private readonly Registry _registry;
            private readonly Theme _theme;
            private readonly Dictionary<int, IBrush> _brushes = [];
        }
    }
}
