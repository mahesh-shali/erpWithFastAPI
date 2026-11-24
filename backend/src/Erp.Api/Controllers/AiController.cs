using Erp.Api.Contracts;
using Erp.Api.Domain;
using Erp.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Erp.Api.Controllers;

/// <summary>
/// AI Assistant. The API authenticates the caller and checks the page right, then forwards the question to the
/// AI service over internal gRPC (IAiAssistantGateway) with the caller's own token, which the AI service
/// validates again before running SQL under RLS for that tenant.
/// </summary>
[ApiController]
[Route("api/ai")]
public class AiController(IAiAssistantGateway ai) : ControllerBase
{
    [HttpPost("chat"), RequirePage(PageKeys.AiChat)]
    public async Task<ActionResult<AiChatResponseDto>> Chat(AiChatRequestDto req, CancellationToken ct)
    {
        // Authentication already validated this bearer token (signature + active session)
        var header = Request.Headers[HeaderNames.Authorization].ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Unauthorized();
        var token = header["Bearer ".Length..].Trim();

        try
        {
            var history = (req.History ?? []).Select(t => new AiChatTurn(t.Role, t.Content)).ToList();
            var a = await ai.ChatAsync(token, req.Question, history, ct);
            return new AiChatResponseDto(a.Answer, a.Sql, a.Columns, a.Rows, a.Cached, a.LatencyMs);
        }
        catch (AiServiceException e)
        {
            return Problem(title: e.Message, statusCode: e.Status);
        }
    }
}
