using System.Text.RegularExpressions;

namespace SupportForge.Agents.Triage;

// "Which class signs values?", "Which Reflector method merges metadata?", "What exception is raised when ...?": a question
// asking for a named part of the code. In the five-language code-QA run both classifiers sent such questions to "unclear"
// (the LLM because they could also be KB questions, Jev because its probabilities were spread) or to kb_question, so the
// customer got a clarifying question or "no documentation". code_question searches the KB as well as the code
// (IntentRouting), so taking it for these questions loses nothing. English only. Words that also name product features
// (package, provider, filter, field, "type of") are left out, so "What type of account do I need?" stays where it was.
public static partial class CodeElementQuestion
{
    [GeneratedRegex(
        @"\b(?:which|what)\s+(?:[\w-]+\s+){0,2}?(?:class(?:es)?|types?(?!\s+of\b)|interfaces?|structs?|traits?|enums?|methods?|functions?|helpers?|decorators?|annotations?|pipes?|interceptors?|middlewares?|controllers?|resolvers?|handlers?|exceptions?|modules?|services?|hooks?|guards?|validators?|parsers?|serializers?|loggers?|factory|factories|constructors?|callbacks?|listeners?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool Matches(string query) => Pattern().IsMatch(query);

    // Applied after the classifier's own rules, in every mode. Only "unclear" and kb_question are replaced.
    public static TriagePolicyResult Apply(TriagePolicyResult policy, string query) =>
        (policy.Intent is TriageLabels.Unclear or TriageLabels.KbQuestion) && Matches(query)
            ? new TriagePolicyResult(TriageLabels.CodeQuestion, "which-code-element")
            : policy;
}
