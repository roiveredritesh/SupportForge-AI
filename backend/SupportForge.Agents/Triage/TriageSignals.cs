using System.Collections.ObjectModel;

namespace SupportForge.Agents.Triage;

// U5 (JEV intent classification plan, KTD3): names and fixed wording of the atomic Noul signals asked in the same Jev
// request as the intent Choice. The statements are constants, never customer text (the customer only appears in state).
// Signals feed TriageDecisionPolicy's override rules and are logged. They are not a second classifier, and
// TriageDecisionPolicy reads all four: about_source_code keeps an uncertain code question from going to "unclear".
public static class TriageSignals
{
    public const string HasSupportQuestion = "has_support_question";
    public const string ReportsMalfunction = "reports_malfunction";
    public const string AboutSourceCode = "about_source_code";
    public const string RefersToImage = "refers_to_image";

    private static readonly IReadOnlyDictionary<string, string> StatementMap = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>
        {
            [HasSupportQuestion] = "The latest message contains an actual support question or request, not only a greeting or thanks.",
            [ReportsMalfunction] = "The user reports that something is broken, failing, or showing an error.",
            [AboutSourceCode] = "The question is about how the product's source code is implemented.",
            [RefersToImage] = "The latest message refers to an attached image or screenshot.",
        });

    // Signal name -> the fixed statement sent to Jev as a Noul question. Insertion order is the order they are asked.
    public static IReadOnlyDictionary<string, string> Statements => StatementMap;
}
