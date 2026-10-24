namespace OSTGUI.Services;

using System.Text;

/// <summary>注释切换的结果：新文本 + 应该保持选中的区间（模型坐标）。</summary>
public readonly record struct CommentToggleResult(string Text, int SelectionStart, int SelectionEnd);

/// <summary>
/// 编辑器与文件之间的**唯一**换行/分隔符转换点（纯逻辑，可自检）。
/// 为什么要专门写：RichEditBox 用 <c>\r</c> 当段分隔符，磁盘上的 lua 却可能是 CRLF 或 LF；
/// 直接互相搬运会让"保存一次就整篇换行变了"，在 git 里表现为整文件改写。
/// </summary>
public static class LuaTextCodec
{
    /// <summary>取文件里第一个换行符的风格；没有换行就用 LF。</summary>
    public static string DetectNewLine(string fileText)
    {
        for (int i = 0; i < fileText.Length; i++)
        {
            if (fileText[i] == '\r')
                return i + 1 < fileText.Length && fileText[i + 1] == '\n' ? "\r\n" : "\r";
            if (fileText[i] == '\n')
                return "\n";
        }
        return "\n";
    }

    /// <summary>文件原文 → 内部模型（一律 <c>\n</c>，与词法器的偏移口径一致）。</summary>
    public static string NormalizeForModel(string fileText) =>
        string.IsNullOrEmpty(fileText) ? "" : fileText.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>内部模型 → RichEditBox（<c>\n</c> 换成它的段分隔符 <c>\r</c>；字符数与模型一致，偏移可直接复用）。</summary>
    public static string ToEditor(string model) =>
        string.IsNullOrEmpty(model) ? "" : model.Replace("\n", "\r");

    /// <summary>RichEditBox 取回的文本 → 内部模型。</summary>
    public static string FromEditor(string editorText) =>
        string.IsNullOrEmpty(editorText) ? "" : editorText.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>内部模型 → 写盘文本（还原文件原本的换行风格）。</summary>
    public static string ToFile(string model, string newLine) =>
        string.IsNullOrEmpty(model) ? "" : model.Replace("\n", newLine);

    /// <summary>把文档偏移换算成 1 基的 行 / 列（状态栏用；纯逻辑，可自检）。</summary>
    public static (int Line, int Column) LineColumn(string text, int offset)
    {
        if (string.IsNullOrEmpty(text)) return (1, 1);
        if (offset < 0) offset = 0;
        if (offset > text.Length) offset = text.Length;

        int line = 1, lastBreak = -1;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] == '\n') { line++; lastBreak = i; }
        }
        return (line, offset - lastBreak);
    }

    /// <summary>
    /// 批量加 / 去行首注释（纯逻辑，可自检）。按**整行**判定：选中行里凡是**有内容**的行全部已注释，
    /// 就取消注释；否则给有内容的行加 <c>-- </c>。空行与纯空白行永远原样保留
    ///（2026-10-24 的 FATAL 就是取消注释时没跳过空行、`IndexOf` 返回 -1 造成的）。
    /// </summary>
    public static CommentToggleResult ToggleComment(string text, int selectionStart, int selectionEnd)
    {
        text ??= "";
        int a = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        int b = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length);

        int lineStart = a == 0 ? 0 : text.LastIndexOf('\n', a - 1) + 1;
        // 选区末尾若正好落在下一行开头，不要把下一行算进来
        int probe = (b > a ? b - 1 : b);
        probe = Math.Clamp(probe, 0, Math.Max(0, text.Length - 1));
        int lineEnd = text.Length == 0 ? 0 : text.IndexOf('\n', probe);
        if (lineEnd < 0) lineEnd = text.Length;

        var lines = text[lineStart..lineEnd].Split('\n');
        bool hasContent = lines.Any(l => l.Trim().Length > 0);
        bool allCommented = hasContent && lines.Where(l => l.Trim().Length > 0)
            .All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal));

        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            var line = lines[i];

            if (line.Trim().Length == 0) { sb.Append(line); continue; }   // 空行/纯空白行一律不动

            if (allCommented)
            {
                int dash = line.IndexOf("--", StringComparison.Ordinal);
                if (dash < 0) { sb.Append(line); continue; }              // 防御：判定与执行不一致时别炸
                var stripped = line.Remove(dash, 2);
                if (stripped.StartsWith(' ')) stripped = stripped[1..];
                sb.Append(stripped);
            }
            else
            {
                sb.Append("-- ").Append(line);
            }
        }

        var updated = text[..lineStart] + sb + text[lineEnd..];
        return new CommentToggleResult(updated, lineStart, lineStart + sb.Length);
    }

    /// <summary>自检：三种换行风格往返一致、混合换行按首个分隔符判定、编辑器分隔符往返、注释切换边界。</summary>
    public static string SelfCheck()
    {
        foreach (var nl in new[] { "\r\n", "\n", "\r" })
        {
            var file = "addappid(1)" + nl + "addtoken(2, \"t\")" + nl;
            var model = NormalizeForModel(file);
            if (model.Contains('\r')) return $"{Escape(nl)}：模型里残留 CR";
            if (ToFile(model, nl) != file) return $"{Escape(nl)}：往返不一致";

            var editor = ToEditor(model);
            if (editor.Contains('\n')) return $"{Escape(nl)}：编辑器文本里残留 LF";
            if (FromEditor(editor) != model) return $"{Escape(nl)}：编辑器往返不一致";
        }

        // 混合换行：以第一个为准（Windows 上真遇到过手工编辑过的混合文件）
        if (DetectNewLine("a\r\nb\nc") != "\r\n") return "混合换行未按首个分隔符判定";
        if (DetectNewLine("a\nb\r\nc") != "\n") return "混合换行（LF 在前）判定错误";

        // 空文件 / 无换行文件
        if (NormalizeForModel("") != "") return "空文件归一化异常";
        if (DetectNewLine("addappid(1)") != "\n") return "无换行文件应回退 LF";

        // 行/列换算
        const string doc = "addappid(1)\naddtoken(2)";
        if (LineColumn(doc, 0) != (1, 1)) return $"行首换算错误：{LineColumn(doc, 0)}";
        if (LineColumn(doc, 12) != (2, 1)) return $"次行首换算错误：{LineColumn(doc, 12)}";
        if (LineColumn(doc, 15) != (2, 4)) return $"列换算错误：{LineColumn(doc, 15)}";
        if (LineColumn(doc, 999) != (2, 12)) return $"越界偏移未夹紧：{LineColumn(doc, 999)}";

        // ── 注释切换 ──────────────────────────────────────────────
        // 1) 选中已注释块（中间带空行）→ 取消注释，空行原样保留
        //    ← 2026-10-24 的 FATAL 就是这个场景：旧实现没跳过空行，IndexOf 返回 -1 后 Remove 抛异常
        const string commentedWithBlank = "-- a\n\n-- b\n";
        var r = ToggleComment(commentedWithBlank, 0, commentedWithBlank.Length);
        if (r.Text != "a\n\nb\n") return $"取消注释（含空行）结果错误：{Escape(r.Text)}";

        // 2) 未注释 → 加注释；纯空白行不动
        const string plain = "addappid(1)\n   \naddtoken(2)\n";
        r = ToggleComment(plain, 0, plain.Length);
        if (r.Text != "-- addappid(1)\n   \n-- addtoken(2)\n") return $"加注释结果错误：{Escape(r.Text)}";

        // 3) 只有空行 / 空文本：什么也不做，且不抛
        if (ToggleComment("", 0, 0).Text != "") return "空文本被改动了";
        if (ToggleComment("\n", 0, 1).Text != "\n") return "空行被改动了";

        // 4) 混合（部分已注释）→ 视为"没全注释"，给有内容的行都加上
        const string mixed = "-- a\nb\n";
        r = ToggleComment(mixed, 0, mixed.Length);
        if (r.Text != "-- -- a\n-- b\n") return $"混合注释结果错误：{Escape(r.Text)}";

        // 5) 光标（零长选区）只影响所在行
        const string two = "a\nb\n";
        r = ToggleComment(two, 2, 2);
        if (r.Text != "a\n-- b\n") return $"零长选区影响范围错误：{Escape(r.Text)}";

        // 6) 选区末尾正好在下一行行首时，不牵连下一行
        const string boundary = "a\nb\n";
        r = ToggleComment(boundary, 0, 2);
        if (r.Text != "-- a\nb\n") return $"行尾边界多带了下一行：{Escape(r.Text)}";

        return "";
    }

    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");
}
