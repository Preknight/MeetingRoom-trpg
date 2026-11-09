using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingRoom.Core.Events;

/// <summary>
/// 事件载荷的 JSON 约定：camelCase 字段名（前后端 DTO 命名一致）+ 中文原样输出。
/// 放宽编码器是安全的：SSE 是数据通道而非 HTML 上下文，前端一律用 <c>textContent</c> 渲染载荷。
/// </summary>
public static class EventJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 枚举一律序列化成 snake_case 字符串（"host_review"、"waiting_approval"），
        // 与前端 wwwroot/js/dom.js 的状态标签约定一致——默认的数字枚举会让前端拿到 1/2/3。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
