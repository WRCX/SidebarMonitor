using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SidebarMonitor.UI;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>
/// Guards the translation table against the failure it is most prone to: silence. Strings are looked
/// up by their exact Spanish literal (<see cref="Loc.T"/>), so editing the text passed to Loc.T
/// without editing the matching key in <see cref="LocStrings"/> compiles, runs, passes every other
/// test — and shows Spanish to English users. That happened to the "other logged-in users" update
/// warning; nothing caught it.
/// </summary>
[Trait("Category", "Unit")]
public class LocStringsTests
{
    /// <summary>Every Spanish literal passed to a `Loc.T("…")` call in the UI project.</summary>
    private static readonly Lazy<HashSet<string>> UsedKeys = new(() =>
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        // The argument of Loc.T(…): one string literal, or several joined with + across lines, which
        // long paragraphs use and the compiler folds into a single key. Both spellings appear, and
        // mixed within one call (see FirstRunDialog's License.rtf message): a regular "…" and a
        // verbatim @"…", the latter so Windows paths need no doubled backslashes. Reading only the
        // first literal, or only regular ones, reports live translations as orphans.
        var call = new Regex(@"Loc\.T\(\s*(" + Literal + @"(?:\s*\+\s*" + Literal + @")*)",
                             RegexOptions.Compiled);
        var one = new Regex(Literal, RegexOptions.Compiled);
        foreach (var file in Directory.EnumerateFiles(UiSourceDir, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) is "LocStrings.cs" or "Loc.cs") continue;
            foreach (Match m in call.Matches(File.ReadAllText(file)))
                keys.Add(string.Concat(one.Matches(m.Groups[1].Value).Select(l => Value(l.Value))));
        }
        return keys;
    });

    /// <summary>
    /// A translation nobody looks up any more. This is the exact shape of the bug: the literal in the
    /// code was edited, so the old key is now orphaned and the new text has no translation at all.
    /// An entry that is genuinely obsolete should be deleted, not left to rot.
    /// </summary>
    [Fact]
    public void Every_translation_entry_is_still_used_by_some_Loc_T_call()
    {
        var orphans = LocStrings.En.Keys.Where(k => !UsedKeys.Value.Contains(k)).ToList();

        Assert.True(orphans.Count == 0,
            "LocStrings entries no key in the code matches — the Spanish literal was probably edited " +
            "without updating its key here, which leaves the new text untranslated:\n  " +
            string.Join("\n  ", orphans.Select(o => $"\"{Escape(o)}\"")));
    }

    /// <summary>
    /// Loc.T's params overload runs the translation through string.Format, so a placeholder present in
    /// Spanish but missing in English throws FormatException at runtime — a crash, in the language we
    /// test least. Indices must match as a set; order within the string may legitimately differ.
    /// </summary>
    [Fact]
    public void Translations_use_the_same_format_placeholders_as_their_key()
    {
        var arg = new Regex(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);
        static SortedSet<string> Indices(Regex r, string s) =>
            new(r.Matches(s).Select(m => m.Groups[1].Value));

        var broken = LocStrings.En
            .Where(kv => !Indices(arg, kv.Key).SetEquals(Indices(arg, kv.Value)))
            .Select(kv => $"\"{Escape(kv.Key)}\"\n    es: {{{string.Join(",", Indices(arg, kv.Key))}}}" +
                          $"  en: {{{string.Join(",", Indices(arg, kv.Value))}}}")
            .ToList();

        Assert.True(broken.Count == 0,
            "Translations whose placeholders differ from their key — string.Format throws on these:\n  " +
            string.Join("\n  ", broken));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The UI project's source, found by walking up to the directory holding the solution.</summary>
    private static string UiSourceDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SidebarMonitor.slnx")))
                dir = dir.Parent;
            Assert.True(dir is not null, "Could not find SidebarMonitor.slnx above " + AppContext.BaseDirectory);
            var ui = Path.Combine(dir!.FullName, "src", "SidebarMonitor.UI");
            Assert.True(Directory.Exists(ui), "UI source not found at " + ui);
            return ui;
        }
    }

    /// <summary>A C# string literal in source form: verbatim (@"…", where "" is a quote and a
    /// backslash is itself) or regular ("…", with backslash escapes).</summary>
    private const string Literal = @"(?:@""(?:[^""]|"""")*""|""(?:[^""\\]|\\.)*"")";

    /// <summary>The string a literal's source text denotes — what the compiler would hand to Loc.T.</summary>
    private static string Value(string source) =>
        source.StartsWith('@')
            ? source[2..^1].Replace("\"\"", "\"")     // verbatim: only "" is an escape
            : Unescape(source[1..^1]);

    /// <summary>Turns a regular C# string literal's body into the string the compiler would produce.</summary>
    private static string Unescape(string literal)
    {
        var sb = new StringBuilder(literal.Length);
        for (int i = 0; i < literal.Length; i++)
        {
            if (literal[i] != '\\') { sb.Append(literal[i]); continue; }
            i++;
            switch (literal[i])
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case 'u': sb.Append((char)Convert.ToInt32(literal.Substring(i + 1, 4), 16)); i += 4; break;
                default: sb.Append(literal[i]); break;   // \" \\ and friends are themselves
            }
        }
        return sb.ToString();
    }

    /// <summary>Newlines back to "\n" so a failure message stays on readable lines.</summary>
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r");
}
