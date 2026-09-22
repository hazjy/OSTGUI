using System.Runtime;

namespace OSTGUI.Services;

/// <summary>
/// 内存收尾：把"一次性大缓冲"（整份 JSON / 整份清单 / 大 byte[]）用完留下的空洞还给系统。
/// </summary>
internal static class OstMemory
{
    /// <summary>
    /// 手动压一次大对象堆（LOH）。
    ///
    /// 为什么要手动：.NET 的 GC 默认**不移动大对象**（搬运成本高），于是像"整份 17.5MB 缓存"这种峰值过去后，
    /// 空洞仍占着已提交内存——任务管理器上看着像泄漏，其实只是没还。
    /// <c>CompactOnce</c> 只对紧接着的那次 GC 生效（不永久改变 GC 行为）；放后台线程跑，不卡 UI。
    ///
    /// ponytail: 治标。真正省内存靠"别整份物化"（见 `SudamaKeyCache` 的流式取键、`ManifestDownloadService` 的流式落盘）；
    /// 这里负责让已经用完的那一坨**还回去**。调用点都选在"用户刚干完一件事"的时刻（入库结束、缓存刷新完）。
    /// </summary>
    public static void CompactAfterLargeBuffers()
    {
        _ = Task.Run(() =>
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
            }
            catch { }
        });
    }
}
