using System.Text.Json;

namespace OSTGUI.Services;

/// <summary>
/// JsonElement 扩展方法
/// </summary>
public static class JsonElementExtensions
{
    public static async Task<JsonElement> ReadAsStringJsonAsync(this HttpContent content)
    {
        var json = await content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement;
    }
}
