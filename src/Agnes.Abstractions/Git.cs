namespace Agnes.Abstractions;

/// <summary>
/// An open pull/merge request on a git forge, as an <c>IGitHostProvider</c> reports it. Forge-neutral: the
/// same shape describes a GitHub PR, a GitLab MR, etc. <see cref="Id"/> is the forge's request identifier
/// (e.g. the PR number as a string), <see cref="SourceBranch"/> the branch the request would check out.
/// </summary>
public sealed record PullRequestInfo(string Id, string Title, string SourceBranch, string Url, string Author);
