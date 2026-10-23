using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OSTGUI.Services;
using System.Text;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace OSTGUI.Views;

/// <summary>
/// 应用内 Lua 编辑器（替代原来的"记事本打开"）。
/// 设计取舍：
///  · 不上第三方控件、不引 WebView2 —— 词法自己写（<see cref="LuaHighlighter"/>），只上 4 种颜色；
///  · 关掉 RichEditBox 自带撤销（<c>UndoLimit = 0</c>）：逐 token 上色会往它的撤销队列里塞格式变更，
///    Ctrl+Z 会先撤销看不见的东西；改用 <see cref="EditorHistory"/> 的整段文本快照；
///  · 保存仍走 <see cref="LuaConfigService.WriteLuaFileAsync"/> 的原子写，内核 LuaFileWatcher 热重载，无需重启 Steam。
/// ⚠️ AOT：本视图不写 <c>{Binding}</c>，所有控件属性都在 code-behind 赋值（见 REF-AOT适配 §1）。
/// </summary>
public sealed partial class LuaEditorView : UserControl
{
    /// <summary>超过这个长度就不上色了（正常入库 lua 只有几 KB）。</summary>
    private const int MaxHighlightLength = 200_000;

    private readonly LuaConfigService _luaService;
    private readonly string _appId;
    private readonly EditorHistory _history = new(100);

    // 全名限定：Windows.System 里也有一个 DispatcherQueueTimer，同时 using 两个命名空间会 CS0104
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _debounce;
    private string _originalModel = "";
    private string _newLine = "\n";
    private bool _suppressEvents;

    /// <summary>用户按 Ctrl+S 时抛给外层弹窗处理（外层负责保存成功后关闭）。</summary>
    public event EventHandler? SaveRequested;

    // 读文档失败时按"有改动"处理（宁可多拦一次，也不要让用户在关窗过程中撞异常卡死）
    public bool HasUnsavedChanges
    {
        get
        {
            try { return CurrentModel() != _originalModel; }
            catch { return true; }
        }
    }

    public LuaEditorView(LuaConfigService luaService, string appId, string? displayName)
    {
        InitializeComponent();
        _luaService = luaService;
        _appId = appId;

        TitleText.Text = string.IsNullOrWhiteSpace(displayName)
            ? $"{appId}.lua"
            : $"{displayName}（{appId}.lua）";

        try { Editor.Document.UndoLimit = 0; } catch { /* 某些版本不接受，可忽略 */ }

        Editor.TextChanged += Editor_TextChanged;
        Editor.SelectionChanged += (_, _) => UpdateStatus();
        Editor.KeyDown += Editor_KeyDown;
        Editor.ActualThemeChanged += (_, _) => HighlightAll();
    }

    // ── 载入 / 保存 ─────────────────────────────────────────────

    /// <summary>读取 <c>&lt;AppId&gt;.lua</c> 并整篇上色。</summary>
    public async Task<(bool ok, string message)> LoadAsync()
    {
        var raw = await _luaService.ReadLuaContentAsync(_appId);
        if (raw is null) return (false, $"未找到 {_appId}.lua，请先在库里刷新。");

        _newLine = LuaTextCodec.DetectNewLine(raw);
        _originalModel = LuaTextCodec.NormalizeForModel(raw);
        ApplyModel(_originalModel, resetHistory: true);
        return (true, "");
    }

    /// <summary>写回磁盘（原子写；换行风格沿用原文件）。</summary>
    public async Task<(bool ok, string message)> SaveAsync()
    {
        var model = CurrentModel();
        var content = LuaTextCodec.ToFile(model, _newLine);
        var (ok, message, _) = await _luaService.WriteLuaFileAsync(_appId, content);
        if (ok)
        {
            _originalModel = model;
            UpdateStatus();
        }
        return (ok, message);
    }

    // ── 文本模型（\n 口径）与编辑器文本（\r 段分隔符）互转 ────────

    private string CurrentModel()
    {
        Editor.Document.GetText(TextGetOptions.None, out var raw);
        // GetText 会带上结尾的段落符；只去掉这一个，别把用户真写的空行吃掉
        if (raw.EndsWith('\r')) raw = raw[..^1];
        return LuaTextCodec.FromEditor(raw);
    }

    private void ApplyModel(string model, bool resetHistory)
    {
        _suppressEvents = true;
        Editor.Document.SetText(TextSetOptions.None, LuaTextCodec.ToEditor(model));
        _suppressEvents = false;

        if (resetHistory) _history.Reset(model);
        HighlightAll();
        UpdateStatus();
    }

    // ── 上色 ───────────────────────────────────────────────────

    private readonly record struct Palette(Color Default, Color Comment, Color Function, Color Number, Color String);

    private static readonly Palette DarkPalette = new(
        Color.FromArgb(255, 0xD4, 0xD4, 0xD4),
        Color.FromArgb(255, 0x6A, 0x99, 0x55),
        Color.FromArgb(255, 0x56, 0x9C, 0xD6),
        Color.FromArgb(255, 0xB5, 0xCE, 0xA8),
        Color.FromArgb(255, 0xCE, 0x91, 0x78));

    private static readonly Palette LightPalette = new(
        Color.FromArgb(255, 0x1F, 0x1F, 0x1F),
        Color.FromArgb(255, 0x00, 0x80, 0x00),
        Color.FromArgb(255, 0x00, 0x00, 0xFF),
        Color.FromArgb(255, 0x09, 0x86, 0x58),
        Color.FromArgb(255, 0xA3, 0x15, 0x15));

    private Palette CurrentPalette =>
        Editor.ActualTheme == ElementTheme.Dark ? DarkPalette : LightPalette;

    private Color ColorFor(Palette palette, LuaTokenKind kind) => kind switch
    {
        LuaTokenKind.Comment => palette.Comment,
        LuaTokenKind.Function => palette.Function,
        LuaTokenKind.Number => palette.Number,
        LuaTokenKind.String => palette.String,
        _ => palette.Default,
    };

    /// <summary>整篇重绘（每次改动 120 ms 后跑一次；7 KB 文件实测开销可忽略）。</summary>
    private void HighlightAll()
    {
        var model = CurrentModel();
        if (model.Length > MaxHighlightLength)
        {
            StatusText.Text = $"文件超过 {MaxHighlightLength:N0} 字符，已跳过着色";
            return;
        }

        var spans = LuaHighlighter.Tokenize(model);
        var palette = CurrentPalette;

        Editor.Document.BatchDisplayUpdates();
        try
        {
            // 先整篇复位成默认色，再逐段上色：删掉的 token 不会留残色
            Editor.Document.GetRange(0, model.Length).CharacterFormat.ForegroundColor = palette.Default;
            foreach (var span in spans)
            {
                Editor.Document.GetRange(span.Start, span.Start + span.Length)
                      .CharacterFormat.ForegroundColor = ColorFor(palette, span.Kind);
            }
        }
        finally
        {
            Editor.Document.ApplyDisplayUpdates();
        }
    }

    // ── 编辑事件 ───────────────────────────────────────────────

    private void Editor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        UpdateStatus();
        RestartDebounce();
    }

    private void RestartDebounce()
    {
        if (_debounce is null)
        {
            _debounce = DispatcherQueue.CreateTimer();
            _debounce.Interval = TimeSpan.FromMilliseconds(120);
            _debounce.IsRepeating = false;
            _debounce.Tick += (_, _) =>
            {
                _history.Push(CurrentModel());
                HighlightAll();
                UpdateStatus();
            };
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == VirtualKey.S)
        {
            e.Handled = true;
            SaveRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (ctrl && (e.Key == VirtualKey.Z || e.Key == VirtualKey.Y))
        {
            var target = e.Key == VirtualKey.Z ? _history.Undo() : _history.Redo();
            if (target is not null) ApplyModel(target, resetHistory: false);
            e.Handled = true;
            return;
        }
        if (!ctrl && e.Key == VirtualKey.Tab)
        {
            Editor.Document.Selection.Text = "    ";   // lua 里 4 空格比制表符稳
            e.Handled = true;
            CommitEdit();
        }
    }

    /// <summary>插入/注释这类程序化改动后：立即记快照 + 重绘（不等防抖）。</summary>
    private void CommitEdit()
    {
        _history.Push(CurrentModel());
        HighlightAll();
        UpdateStatus();
    }

    // ── 快速插入 ───────────────────────────────────────────────

    /// <summary>在光标处插入模板；<paramref name="selectLength"/> 为 0 时把光标停在 <paramref name="caretOffset"/>。</summary>
    private void InsertSnippet(string snippet, int caretOffset, int selectLength = 0)
    {
        var selection = Editor.Document.Selection;
        selection.Text = snippet;

        int insertStart = selection.StartPosition - snippet.Length;
        if (insertStart < 0) insertStart = 0;

        int at = Math.Clamp(insertStart + caretOffset, 0, int.MaxValue);
        Editor.Document.Selection.SetRange(at, at + Math.Max(0, selectLength));

        CommitEdit();
    }

    private void InsertAppId_Click(object sender, RoutedEventArgs e) =>
        InsertSnippet("addappid()", 9);                 // 光标停在括号里

    private void InsertDepotKey_Click(object sender, RoutedEventArgs e) =>
        InsertSnippet("addappid(0, 1, \"\")", 9, 1);    // 选中占位的 0

    private void InsertToken_Click(object sender, RoutedEventArgs e) =>
        InsertSnippet("addtoken()", 9);

    private void InsertManifestId_Click(object sender, RoutedEventArgs e) =>
        InsertSnippet("setmanifestid(0, \"\", 0)", 14, 1);

    /// <summary>给选中行（或光标所在行）批量加 / 去 <c>-- </c>。</summary>
    private void ToggleComment_Click(object sender, RoutedEventArgs e)
    {
        var model = CurrentModel();
        var selection = Editor.Document.Selection;

        int selStart = Math.Clamp(Math.Min(selection.StartPosition, selection.EndPosition), 0, model.Length);
        int selEnd = Math.Clamp(Math.Max(selection.StartPosition, selection.EndPosition), 0, model.Length);

        int lineStart = selStart == 0 ? 0 : model.LastIndexOf('\n', selStart - 1) + 1;
        int lineEnd = model.IndexOf('\n', Math.Min(selEnd, model.Length));
        if (lineEnd < 0) lineEnd = model.Length;

        var block = model[lineStart..lineEnd];
        var lines = block.Split('\n');

        bool allCommented = lines.Where(l => l.Trim().Length > 0).All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal));

        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            var line = lines[i];
            if (allCommented)
            {
                int dash = line.IndexOf("--", StringComparison.Ordinal);
                var stripped = line.Remove(dash, 2);
                if (stripped.StartsWith(' ')) stripped = stripped[1..];
                sb.Append(stripped);
            }
            else
            {
                sb.Append(line.Length == 0 ? line : "-- " + line);
            }
        }

        var updated = model[..lineStart] + sb + model[lineEnd..];
        ApplyModel(updated, resetHistory: false);
        Editor.Document.Selection.SetRange(lineStart, lineStart);
        CommitEdit();
    }

    // ── 状态栏 ─────────────────────────────────────────────────

    /// <summary>
    /// 关窗被拦下时的提示（两步确认：再点一次「关闭」才放弃）。
    /// 不弹二级对话框——同一 XamlRoot 同时只能开一个 ContentDialog，嵌套会抛异常。
    /// </summary>
    public void ShowDiscardHint() => DirtyText.Text = "有未保存改动：再点一次「关闭」将放弃";

    private void UpdateStatus()
    {
        var model = CurrentModel();
        int offset = Editor.Document.Selection.StartPosition;
        var (line, column) = LuaTextCodec.LineColumn(model, offset);

        StatusText.Text = $"行 {line}，列 {column} · {model.Length} 字符";
        DirtyText.Text = model == _originalModel ? "未修改" : "已修改";
    }
}
