namespace OSTGUI.Services;

/// <summary>Lua 里需要着色的词法种类（故意不区分标点：给括号上色只会让画面更乱）。</summary>
public enum LuaTokenKind
{
    Comment,
    Function,
    Number,
    String,
}

/// <summary>一段着色区间：<see cref="Start"/> 与 <see cref="Length"/> 对应"以 \n 分行"的规范化文本。</summary>
public readonly record struct LuaSpan(int Start, int Length, LuaTokenKind Kind);

/// <summary>
/// 入库 lua 用的极简词法器（纯逻辑、零 WinUI 依赖，便于挂自检）。
/// 只认这几样：<c>--</c> 行注释、<c>--[[ ]]</c> 块注释、<c>"</c>/<c>'</c> 短字符串、<c>[[ ]]</c> 长字符串、
/// 数字（含 0x）、内核真正注册的函数名（大小写不敏感，见 ZSteamTool/src/Utils/Config/LuaConfig.cpp:513-526）。
/// 词法分析不追求完整 Lua 语法：宁可少着色，也不要把普通标识符染成关键字。
/// </summary>
public static class LuaHighlighter
{
    /// <summary>内核 register_func 注册的名字，外加几个 Lua 控制字。</summary>
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "addappid", "addtoken", "setlegacycdkey", "addprocess", "forcedenuvo", "seteticketurl",
        "setmanifestid", "http_get", "http_post", "setappticket", "seteticket", "setstat",
        "local", "function", "return", "if", "then", "else", "elseif", "end", "for", "while",
        "do", "in", "and", "or", "not", "nil", "true", "false",
    };

    /// <summary>把整段文本切成着色区间；返回的区间保证升序、互不重叠、落在文本范围内。</summary>
    public static List<LuaSpan> Tokenize(string text)
    {
        var spans = new List<LuaSpan>();
        if (string.IsNullOrEmpty(text)) return spans;

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            // ── 注释 ───────────────────────────────────────────
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                int start = i;
                if (TryMatchLongBracket(text, i + 2, out int bodyStart, out int bodyEnd, out int afterEnd))
                {
                    // --[[ ... ]]（含 [==[ ]==] 变体）；未闭合时吃到文件尾
                    spans.Add(new LuaSpan(start, afterEnd - start, LuaTokenKind.Comment));
                    i = afterEnd;
                    continue;
                }
                int eol = text.IndexOf('\n', i);
                int end = eol < 0 ? text.Length : eol;
                spans.Add(new LuaSpan(start, end - start, LuaTokenKind.Comment));
                i = end;
                continue;
            }

            // ── 长字符串 [[ ... ]] ─────────────────────────────
            if (c == '[' && (i == 0 || !IsIdentChar(text[i - 1])))
            {
                if (TryMatchLongBracket(text, i, out _, out _, out int longEnd))
                {
                    spans.Add(new LuaSpan(i, longEnd - i, LuaTokenKind.String));
                    i = longEnd;
                    continue;
                }
            }

            // ── 短字符串 ───────────────────────────────────────
            if (c == '"' || c == '\'')
            {
                int start = i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\') { i += 2; continue; }      // 转义：跳过被转义的字符
                    if (text[i] == c) { i++; break; }               // 闭合
                    if (text[i] == '\n') break;                     // 未闭合：止于行尾
                    i++;
                }
                if (i > text.Length) i = text.Length;
                spans.Add(new LuaSpan(start, i - start, LuaTokenKind.String));
                continue;
            }

            // ── 数字（十进制 / 小数 / 0x） ─────────────────────
            if (char.IsDigit(c))
            {
                int start = i;
                if (c == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < text.Length && Uri.IsHexDigit(text[i])) i++;
                }
                else
                {
                    while (i < text.Length && char.IsDigit(text[i])) i++;
                    if (i < text.Length && text[i] == '.')
                    {
                        i++;
                        while (i < text.Length && char.IsDigit(text[i])) i++;
                    }
                }
                spans.Add(new LuaSpan(start, i - start, LuaTokenKind.Number));
                continue;
            }

            // ── 标识符：只给函数名/关键字上色 ───────────────────
            if (IsIdentStart(c))
            {
                int start = i;
                while (i < text.Length && IsIdentChar(text[i])) i++;
                var word = text.Substring(start, i - start);
                if (Functions.Contains(word))
                    spans.Add(new LuaSpan(start, word.Length, LuaTokenKind.Function));
                continue;
            }

            i++;
        }

        return spans;
    }

    /// <summary>
    /// 从 <paramref name="pos"/> 起尝试匹配 <c>[=*[</c>…<c>]=*]</c>。
    /// 未闭合时按"吃到文件尾"处理（词法器不报错，只保证区间不越界）。
    /// </summary>
    private static bool TryMatchLongBracket(string text, int pos, out int bodyStart, out int bodyEnd, out int afterEnd)
    {
        bodyStart = bodyEnd = afterEnd = 0;
        if (pos >= text.Length || text[pos] != '[') return false;

        int p = pos + 1, level = 0;
        while (p < text.Length && text[p] == '=') { level++; p++; }
        if (p >= text.Length || text[p] != '[') return false;

        bodyStart = p + 1;
        var closer = "]" + new string('=', level) + "]";
        int idx = text.IndexOf(closer, bodyStart, StringComparison.Ordinal);
        if (idx < 0)
        {
            bodyEnd = text.Length;
            afterEnd = text.Length;
            return true;
        }
        bodyEnd = idx;
        afterEnd = idx + closer.Length;
        return true;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// 自检（<c>OSTGUI.exe --trainer-selftest</c> 一并跑）：返回失败说明，全通过返回空串。
    /// 覆盖的是"错了会静默染色错误"的点：字符串里的 <c>--</c>、转义引号、未闭合、大小写、区间不变量。
    /// </summary>
    public static string SelfCheck()
    {
        // 1) 字符串里的 -- 不是注释
        var s = Tokenize("addtoken(1, \"a--b\")\n");
        if (s.Count(x => x.Kind == LuaTokenKind.Comment) != 0) return "字符串内的 -- 被当成注释";
        var strSpan = s.First(x => x.Kind == LuaTokenKind.String);
        if (strSpan.Start != 12) return $"字符串起点异常：{strSpan.Start}";

        // 2) 行尾注释
        s = Tokenize("addappid(1) -- note\n");
        var last = s[^1];
        if (last.Kind != LuaTokenKind.Comment) return "行尾注释未识别";
        if (last.Length != "-- note".Length) return $"行尾注释长度异常：{last.Length}";

        // 3) 转义引号不提前闭合
        s = Tokenize("addtoken(1, \"a\\\"b\")\n");
        var q = s.First(x => x.Kind == LuaTokenKind.String);
        if (q.Length != 6) return $"转义引号处理错误，长度 {q.Length}";

        // 4) 未闭合字符串止于行尾
        s = Tokenize("addtoken(1, \"oops\naddappid(2)\n");
        q = s.First(x => x.Kind == LuaTokenKind.String);
        if (q.Start + q.Length != 17) return $"未闭合字符串未止于行尾：{q.Start}+{q.Length}";

        // 5) 块注释（含 == 变体）与未闭合块注释
        s = Tokenize("--[[ a \"b\" c ]]\naddappid(1)\n");
        var cmt = s.First(x => x.Kind == LuaTokenKind.Comment);
        if (cmt.Length != 15) return $"块注释长度异常：{cmt.Length}";
        if (s.Any(x => x.Kind == LuaTokenKind.String)) return "块注释里的引号被当成字符串";
        s = Tokenize("--[==[ open\nstill comment\n");
        cmt = s.First(x => x.Kind == LuaTokenKind.Comment);
        if (cmt.Start + cmt.Length != "--[==[ open\nstill comment\n".Length) return "未闭合块注释未吃到文件尾";

        // 6) 函数名大小写不敏感 + 数字（含 0x）
        if (Tokenize("AddAppID(1)").Count(x => x.Kind == LuaTokenKind.Function) != 1)
            return "函数名大小写不敏感失效";
        var nums = Tokenize("setmanifestid(2, \"g\", 1024)\naddappid(0x1f)\n")
            .Where(x => x.Kind == LuaTokenKind.Number).ToList();
        if (nums.Count != 3) return $"数字识别数量异常：{nums.Count}";
        if (nums[^1].Length != 4) return $"十六进制数字长度异常：{nums[^1].Length}";

        // 7) 普通标识符不着色（宁可少染）
        if (Tokenize("foo = addappidx(1)").Count(x => x.Kind == LuaTokenKind.Function) != 0)
            return "非注册函数名被当成关键字";

        // 8) 区间不变量
        const string sample = "-- c\naddappid(731, 1, \"aa\")\n--[[ x ]] setmanifestid(1, \"2\", 0x10)\nfoo\n";
        var spans = Tokenize(sample);
        int prevEnd = 0;
        foreach (var sp in spans)
        {
            if (sp.Length <= 0) return "出现零长度区间";
            if (sp.Start < prevEnd) return "区间重叠或未排序";
            if (sp.Start + sp.Length > sample.Length) return "区间越界";
            prevEnd = sp.Start + sp.Length;
        }

        return "";
    }
}
