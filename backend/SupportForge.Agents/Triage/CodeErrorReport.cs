using System.Text.RegularExpressions;

namespace SupportForge.Agents.Triage;

// "The login fails with a SecurityException ... Where is this check done?", "label_replace never matches ... Why?": a bug
// report that names something from the code (an exception type, an identifier, a call, a command-line flag, a query
// parameter) and asks where or why. In the five-language code-QA run the small LLM classifier sent such reports to
// "unclear" (the customer got a clarifying question) and Jev sent some to kb_question (no code searched). code_issue
// searches the KB as well as the code (IntentRouting), so taking it for these reports loses nothing. English only.
// Both parts are needed: product questions name settings ("api key") in plain words, and a bare "why" is not enough.
public static partial class CodeErrorReport
{
    // Case-sensitive: the casing is what makes a word an identifier.
    [GeneratedRegex(
        @"\b[A-Z][A-Za-z0-9]*(?:Exception|Error)\b"                     // SecurityException, TypeError
        + @"|\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b"                         // label_replace, honor_labels
        + @"|\b[a-z]{2,}[a-z0-9]*(?:[A-Z][a-z0-9]+)+\b"                 // setCookie, parseLong (not iPhone, eBay)
        + @"|\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+){2,}\b"                  // LoginAttemptsBeforeLockout (not PayPal, GitHub)
        + @"|\b[A-Z][a-z0-9]+(?:Service|Module|Controller|Manager|Provider|Factory|Handler|Pipe|Guard|Interceptor|Middleware|Resolver|Repository|Config|Options|Policy)\b"
        + @"|\b[A-Za-z_][\w.]*\("                                        // get(, label_replace(
        + @"|(?<![\w-])--[a-z][\w.-]+"                                   // --storage.tsdb.retention.size
        + @"|[?&][a-z_]+=",                                              // ?active=1
        RegexOptions.CultureInvariant)]
    private static partial Regex CodeEvidence();

    [GeneratedRegex(
        @"\b(?:where|why|what\s+(?:decides|determines|causes|counts|triggers)|(?:which|what)\s+(?:code|part|component|file|line)s?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AsksWhereOrWhy();

    public static bool Matches(string query) => CodeEvidence().IsMatch(query) && AsksWhereOrWhy().IsMatch(query);

    // Applied after the classifier's own rules and CodeElementQuestion, in every mode. Only "unclear" and kb_question are
    // replaced.
    public static TriagePolicyResult Apply(TriagePolicyResult policy, string query) =>
        (policy.Intent is TriageLabels.Unclear or TriageLabels.KbQuestion) && Matches(query)
            ? new TriagePolicyResult(TriageLabels.CodeIssue, "code-error-report")
            : policy;
}
