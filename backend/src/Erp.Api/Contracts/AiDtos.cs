using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Erp.Api.Contracts;

// POST /api/ai/chat. Same shape and limits as the AI service's REST /ai/chat, so clients can switch paths only.

public record AiChatTurnDto(
    [Required, RegularExpression("^(user|assistant)$")] string Role,
    [Required(AllowEmptyStrings = true), MaxLength(4000)] string Content);

public record AiChatRequestDto(
    [Required, StringLength(1000, MinimumLength = 1)] string Question,
    [MaxLength(10)] List<AiChatTurnDto>? History);

/// <summary>Rows: the AI service's JSON passed through untouched. latency_ms stays snake_case like /ai/chat.</summary>
public record AiChatResponseDto(
    string Answer, IReadOnlyList<string> Sql, IReadOnlyList<string> Columns, JsonElement Rows, bool Cached,
    [property: JsonPropertyName("latency_ms")] int LatencyMs);
