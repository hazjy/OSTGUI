// 会话请求的形状（GUI 侧 OSTGUI/Utils/SessionRequest.cs 写，宿主读）。两处字段名必须一致。
internal sealed class SessionRequestPayload
{
    public string GameExe { get; set; } = "";
    public uint SessionAppId { get; set; }
    public bool ViaAppIdFile { get; set; }
}
