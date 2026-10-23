namespace OSTGUI.Services;

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

    /// <summary>自检：三种换行风格往返一致、混合换行按首个分隔符判定、编辑器分隔符往返。</summary>
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

        return "";
    }

    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");
}
