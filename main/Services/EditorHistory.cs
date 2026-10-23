namespace OSTGUI.Services;

/// <summary>
/// 编辑器自己的撤销/重做栈（纯逻辑，可自检）。
/// 刻意不用 RichEditBox 自带撤销：逐 token 上色会往它的撤销队列里塞一堆"格式变更"，
/// 用户按 Ctrl+Z 会先撤销一堆看不见的东西。这里改成整段文本快照：
/// 行为可预测、跨上色无副作用，还能单独测。
/// </summary>
public sealed class EditorHistory
{
    private readonly List<string> _stack = new();
    private int _index = -1;

    public EditorHistory(int capacity = 100)
    {
        Capacity = capacity < 2 ? 2 : capacity;
    }

    public int Capacity { get; }
    public int Count => _stack.Count;
    public bool CanUndo => _index > 0;
    public bool CanRedo => _index >= 0 && _index < _stack.Count - 1;

    /// <summary>清空并以给定文本作为唯一基线（打开文件时调用）。</summary>
    public void Reset(string initial)
    {
        _stack.Clear();
        _stack.Add(initial ?? "");
        _index = 0;
    }

    /// <summary>
    /// 记一次快照。与当前位置相同则忽略（避免定时器把同一份文本反复入栈）；
    /// 写入新快照会丢弃"重做尾巴"；超出容量时从最旧端裁剪。
    /// </summary>
    public void Push(string snapshot)
    {
        snapshot ??= "";
        if (_index >= 0 && _stack[_index] == snapshot) return;

        if (_index < _stack.Count - 1)
            _stack.RemoveRange(_index + 1, _stack.Count - _index - 1);

        _stack.Add(snapshot);
        _index = _stack.Count - 1;

        while (_stack.Count > Capacity)
        {
            _stack.RemoveAt(0);
            _index--;
        }
    }

    /// <summary>回退一步并返回目标文本；无可回退时返回 null。</summary>
    public string? Undo()
    {
        if (!CanUndo) return null;
        _index--;
        return _stack[_index];
    }

    /// <summary>前进一步并返回目标文本；无可重做时返回 null。</summary>
    public string? Redo()
    {
        if (!CanRedo) return null;
        _index++;
        return _stack[_index];
    }

    /// <summary>自检：序列正确性、去重、重做尾巴被丢弃、容量裁剪。</summary>
    public static string SelfCheck()
    {
        var h = new EditorHistory(4);
        if (h.CanUndo || h.CanRedo) return "空历史不应可撤销/可重做";

        h.Reset("a");
        if (h.CanUndo) return "仅基线时不应可撤销";

        h.Push("ab");
        h.Push("abc");
        if (h.Undo() != "ab") return "第一次撤销未回到 ab";
        if (h.Undo() != "a") return "第二次撤销未回到 a";
        if (h.Undo() != null) return "已在基线仍能撤销";
        if (h.Redo() != "ab") return "重做未回到 ab";

        // 重做之后写入新快照：重做尾巴必须丢弃
        h.Push("ax");
        if (h.CanRedo) return "写入新快照后仍可重做";
        if (h.Undo() != "ab") return "写入新快照后撤销结果错误";

        // 连续相同快照不重复入栈
        var h2 = new EditorHistory(4);
        h2.Reset("s");
        h2.Push("s");
        h2.Push("s");
        if (h2.Count != 1) return $"重复快照被入栈：{h2.Count}";

        // 容量裁剪：超出后最旧的被丢弃，且索引随之平移
        var h3 = new EditorHistory(3);
        h3.Reset("s0");
        h3.Push("s1");
        h3.Push("s2");
        h3.Push("s3");
        if (h3.Count != 3) return $"容量裁剪失效：{h3.Count}";
        if (h3.Undo() != "s2") return "裁剪后撤销结果错误";
        if (h3.Undo() != "s1") return "裁剪后第二次撤销结果错误";
        if (h3.Undo() != null) return "裁剪后仍能撤销到被丢弃的快照";

        return "";
    }
}
