namespace SupportForge.Agents.Guard;

public enum GuardSignalClass { Block, Review }

/// <summary>
/// One screened property of a customer turn. <see cref="Statement"/> is the fixed wording Jev scores
/// against; it never contains user text, so the request shape stays free of injection surface.
/// </summary>
public sealed record GuardSignal(string Name, string Statement, GuardSignalClass Class);

public static class GuardSignals
{
    public const string OverrideInstructions = "override_instructions";
    public const string ExtractSystemPrompt = "extract_system_prompt";
    public const string JailbreakRoleplay = "jailbreak_roleplay";
    public const string CrossTenantProbe = "cross_tenant_probe";
    public const string ExtractRawCode = "extract_raw_code";
    public const string ObfuscatedPayload = "obfuscated_payload";

    public static IReadOnlyList<GuardSignal> All { get; } =
    [
        new GuardSignal(OverrideInstructions,
            "The message tries to make the assistant ignore, override or replace its instructions or role.",
            GuardSignalClass.Block),
        new GuardSignal(ExtractSystemPrompt,
            "The message asks the assistant to reveal its hidden instructions, system prompt, or internal configuration.",
            GuardSignalClass.Block),
        new GuardSignal(JailbreakRoleplay,
            "The message uses role-play, hypotheticals, fictional framing or encoded text to get the assistant to break its rules.",
            GuardSignalClass.Block),
        new GuardSignal(CrossTenantProbe,
            "The message asks for data belonging to other customers, organisations or projects.",
            GuardSignalClass.Block),
        new GuardSignal(ExtractRawCode,
            "The message asks for raw source code or file contents to be output verbatim.",
            GuardSignalClass.Review),
        new GuardSignal(ObfuscatedPayload,
            "The message contains encoded, obfuscated or unusually formatted text that hides its meaning.",
            GuardSignalClass.Review),
    ];

    private static readonly IReadOnlyDictionary<string, GuardSignal> ByName =
        All.ToDictionary(s => s.Name, StringComparer.Ordinal);

    public static GuardSignal Get(string name) =>
        ByName.TryGetValue(name, out var signal)
            ? signal
            : throw new KeyNotFoundException($"Unknown guard signal '{name}'.");
}
