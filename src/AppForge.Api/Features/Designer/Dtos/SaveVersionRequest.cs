using System.Text.Json.Nodes;

namespace AppForge.Api.Features.Designer.Dtos;

internal sealed record SaveVersionRequest(JsonNode? RootElement);
