using System.Buffers;
using System.Net;
using System.Text.Json;

namespace SupportForge.Agents.Jev;

// U4: the ONLY file that knows Jev's JSON shapes. JevClient and the public models never touch JSON, so a
// wire change is a one-file edit. Shapes come from docs/integrations/typesafe-jev-api.md, which was taken
// from TypeSafe's official SDKs.
internal static class JevWire
{
    internal const string RequestIdHeader = "x-typesafe-request-id";

    // detail[].msg is echoed into an exception message, so cap it. It is validation wording, not customer text.
    private const int MaxValidationMessageChars = 200;

    internal static byte[] BuildRequestBody(string model, object state, IReadOnlyDictionary<string, JevQuestion> questions)
    {
        ValidateQuestions(questions);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);

            // state goes through the default serializer as-is: anonymous objects, dictionaries and lists work,
            // and property names keep the casing the caller wrote (no camelCase policy).
            writer.WritePropertyName("state");
            JsonSerializer.Serialize(writer, state);

            writer.WriteStartObject("questions");
            foreach (var (name, question) in questions)
            {
                writer.WritePropertyName(name);
                WriteQuestion(writer, question);
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static string? ReadRequestId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(RequestIdHeader, out var values))
            return null;
        var id = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    // The body is never copied into the message: for a 422 it can echo the state, which is customer text.
    internal static JevApiException ToApiException(HttpStatusCode status, string? requestId, byte[] body)
    {
        var code = (int)status;
        var message = $"Jev request failed with HTTP {code}.";
        if (code == 422 && FirstValidationMessage(body) is { } detail)
            message += $" {detail}";
        return new JevApiException(code, requestId, message);
    }

    internal static JevResult ParseResponse(byte[] body, IReadOnlyDictionary<string, JevQuestion> questions, string requestedModel, string? requestId)
    {
        using var document = ParseDocument(body, requestId);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JevResponseException("Jev response is not a JSON object.", requestId);
        if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
            throw new JevResponseException("Jev response has no answers object.", requestId);

        // model is the resolved model and can differ from the alias that was sent. Fall back to the alias rather
        // than discard a good answer over a missing echo.
        var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString() ?? requestedModel
            : requestedModel;
        var (inputTokens, outputTokens) = ReadUsage(root);

        var parsed = new Dictionary<string, JevAnswer>(questions.Count, StringComparer.Ordinal);
        foreach (var (name, question) in questions)
        {
            if (!answers.TryGetProperty(name, out var answer))
                throw new JevResponseException($"Jev response has no answer for question '{name}'.", requestId);
            parsed[name] = ParseAnswer(name, question, answer, requestId);
        }

        // Unasked answers are dropped on purpose: Jev may add fields, and only the questions we asked matter.
        return new JevResult(model, parsed, inputTokens, outputTokens, requestId);
    }

    private static void ValidateQuestions(IReadOnlyDictionary<string, JevQuestion> questions)
    {
        if (questions.Count == 0)
            throw new ArgumentException("Jev needs at least one question.", nameof(questions));

        foreach (var (name, question) in questions)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Question names must not be blank.", nameof(questions));
            if (question is null)
                throw new ArgumentException($"Question '{name}' is null.", nameof(questions));

            // The contract rejects these with a 422 anyway. Failing here saves a round trip on the critical path
            // and gives the caller a clear error (U4).
            switch (question)
            {
                case JevChoiceQuestion choice when choice.Criteria is null || choice.Criteria.Count == 0:
                    throw new ArgumentException($"Choice question '{name}' needs at least one criterion.", nameof(questions));
                case JevScoreQuestion score when score.Criteria is null || score.Criteria.Count == 0:
                    throw new ArgumentException($"Score question '{name}' needs at least one criterion.", nameof(questions));
                case JevScoreQuestion score when score.Criteria.Any(level => level is null):
                    throw new ArgumentException($"Score question '{name}' has a null criterion.", nameof(questions));
                case JevNoulQuestion noul when string.IsNullOrWhiteSpace(noul.Instructions)
                                               && string.IsNullOrWhiteSpace(noul.WhenTrue)
                                               && string.IsNullOrWhiteSpace(noul.WhenFalse):
                    throw new ArgumentException($"Noul question '{name}' needs instructions or a described true/false side.", nameof(questions));
                case JevChoiceQuestion or JevNoulQuestion or JevScoreQuestion:
                    break;
                default:
                    throw new ArgumentException($"Question '{name}' has an unsupported type.", nameof(questions));
            }
        }
    }

    private static void WriteQuestion(Utf8JsonWriter writer, JevQuestion question)
    {
        writer.WriteStartObject();
        switch (question)
        {
            case JevChoiceQuestion choice:
                writer.WriteString("type", "choice");
                WriteInstructions(writer, choice.Instructions);
                writer.WriteStartObject("criteria");
                foreach (var (label, description) in choice.Criteria)
                {
                    // A null description is sent as JSON null, not omitted: the contract allows label -> null.
                    writer.WritePropertyName(label);
                    if (description is null)
                        writer.WriteNullValue();
                    else
                        writer.WriteStringValue(description);
                }
                writer.WriteEndObject();
                break;

            case JevNoulQuestion noul:
                writer.WriteString("type", "noul");
                WriteInstructions(writer, noul.Instructions);
                // criteria is omitted entirely when neither side is described, and an unset side is omitted rather
                // than sent as null (U4). The checks are inline so the compiler sees each side as non-null.
                if (!string.IsNullOrWhiteSpace(noul.WhenTrue) || !string.IsNullOrWhiteSpace(noul.WhenFalse))
                {
                    writer.WriteStartObject("criteria");
                    if (!string.IsNullOrWhiteSpace(noul.WhenTrue))
                        writer.WriteString("true", noul.WhenTrue);
                    if (!string.IsNullOrWhiteSpace(noul.WhenFalse))
                        writer.WriteString("false", noul.WhenFalse);
                    writer.WriteEndObject();
                }
                break;

            case JevScoreQuestion score:
                writer.WriteString("type", "score");
                WriteInstructions(writer, score.Instructions);
                // criteria is an ordered array: the position is the score level counted from 0, so order is the contract.
                writer.WriteStartArray("criteria");
                foreach (var level in score.Criteria)
                    writer.WriteStringValue(level);
                writer.WriteEndArray();
                break;

            default:
                throw new ArgumentException("Unsupported question type.", nameof(question));
        }
        writer.WriteEndObject();
    }

    // Unset optional fields are omitted, not sent as null (contract, Request section). Blank counts as unset.
    private static void WriteInstructions(Utf8JsonWriter writer, string? instructions)
    {
        if (!string.IsNullOrWhiteSpace(instructions))
            writer.WriteString("instructions", instructions);
    }

    private static JsonDocument ParseDocument(byte[] body, string? requestId)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // The parser's message can quote part of the body, so it is deliberately not carried over.
            throw new JevResponseException("Jev response is not valid JSON.", requestId);
        }
    }

    private static JevAnswer ParseAnswer(string name, JevQuestion question, JsonElement answer, string? requestId)
    {
        if (answer.ValueKind != JsonValueKind.Object)
            throw new JevResponseException($"Jev answer '{name}' is not an object.", requestId);

        // A type mismatch is a contract violation, not a value to coerce. Answers are never read by position.
        var expected = TypeName(question);
        if (!answer.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || typeElement.GetString() != expected)
        {
            throw new JevResponseException($"Jev answer '{name}' does not match the '{expected}' question that was asked.", requestId);
        }

        switch (question)
        {
            case JevChoiceQuestion choice:
                return ParseChoice(name, choice, answer, requestId);
            case JevNoulQuestion:
                return new JevNoulAnswer(ReadFiniteNumber(answer, "noul", name, requestId));
            case JevScoreQuestion:
                return new JevScoreAnswer(
                    ReadFiniteNumber(answer, "score", name, requestId),
                    ReadFiniteNumber(answer, "confidence", name, requestId),
                    ReadProbabilities(answer, name, requestId));
            default:
                throw new ArgumentException("Unsupported question type.", nameof(question));
        }
    }

    private static JevChoiceAnswer ParseChoice(string name, JevChoiceQuestion question, JsonElement answer, string? requestId)
    {
        if (!answer.TryGetProperty("choice", out var choiceElement) || choiceElement.ValueKind != JsonValueKind.String)
            throw new JevResponseException($"Jev answer '{name}' has no string 'choice'.", requestId);
        var choice = choiceElement.GetString() ?? string.Empty;

        // Only an asked label is acceptable. An unknown label would look like a valid answer that matches no
        // branch the caller handles (U4). The label text itself is not echoed into the message.
        if (!question.Criteria.ContainsKey(choice))
            throw new JevResponseException($"Jev answer '{name}' chose an option that was not asked.", requestId);

        var confidence = ReadFiniteNumber(answer, "confidence", name, requestId);
        var probabilities = ReadProbabilities(answer, name, requestId);
        return new JevChoiceAnswer(choice, confidence, probabilities);
    }

    private static double ReadFiniteNumber(JsonElement obj, string property, string name, string? requestId)
    {
        // Kind is checked before TryGetDouble, because the TryGet methods throw on a non-number element.
        if (obj.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
            && double.IsFinite(number))
        {
            return number;
        }
        throw new JevResponseException($"Jev answer '{name}' has a missing or non-numeric '{property}'.", requestId);
    }

    private static Dictionary<string, double> ReadProbabilities(JsonElement answer, string name, string? requestId)
    {
        if (!answer.TryGetProperty("probabilities", out var probabilities) || probabilities.ValueKind != JsonValueKind.Object)
            throw new JevResponseException($"Jev answer '{name}' has no probabilities object.", requestId);

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in probabilities.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetDouble(out var value)
                || !double.IsFinite(value))
            {
                throw new JevResponseException($"Jev answer '{name}' has a non-numeric probability.", requestId);
            }
            result[property.Name] = value;
        }
        return result;
    }

    private static (int Input, int Output) ReadUsage(JsonElement root)
    {
        // usage is billing accounting, not a correctness field, so a missing count reads as 0 rather than
        // failing a good answer (U4).
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return (0, 0);
        return (ReadInt(usage, "input_tokens"), ReadInt(usage, "output_tokens"));
    }

    private static int ReadInt(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static string? FirstValidationMessage(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("detail", out var detail)
                || detail.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in detail.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("msg", out var msg)
                    && msg.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(msg.GetString()))
                {
                    var text = msg.GetString()!;
                    return text.Length <= MaxValidationMessageChars ? text : text[..MaxValidationMessageChars];
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string TypeName(JevQuestion question) => question switch
    {
        JevChoiceQuestion => "choice",
        JevNoulQuestion => "noul",
        JevScoreQuestion => "score",
        _ => throw new ArgumentException("Unsupported question type.", nameof(question)),
    };
}
