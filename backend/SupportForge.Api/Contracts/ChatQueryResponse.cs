namespace SupportForge.Api.Contracts;

public sealed class ChatQueryResponse
{
    public required string Draft { get; init; }
    public required double Confidence { get; init; }
    public required IReadOnlyList<SourceDto> Sources { get; init; }
}

public sealed record SourceDto(string Label, string Url);
