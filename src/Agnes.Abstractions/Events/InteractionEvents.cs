namespace Agnes.Abstractions.Events;

// Host action events for answering the agent's requests (permission prompts, questions). See the taxonomy
// note in SessionEvents.cs.

/// <summary>Before a permission response is sent to the agent. Interceptors may override the chosen option
/// (auto-allow/deny policy) via <see cref="OptionId"/>, or veto (the response is not sent).</summary>
public sealed class BeforePermissionResponseEvent(string sessionId, string requestId, string optionId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
    public string RequestId { get; } = requestId;
    public string OptionId { get; set; } = optionId;
}

/// <summary>Before an answer to an agent question is sent. Interceptors may rewrite the answers or veto.</summary>
public sealed class BeforeQuestionAnswerEvent(string sessionId, string requestId, IReadOnlyList<QuestionAnswer> answers) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
    public string RequestId { get; } = requestId;
    public IReadOnlyList<QuestionAnswer> Answers { get; set; } = answers;
}
